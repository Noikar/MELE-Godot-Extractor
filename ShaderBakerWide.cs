using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SixLabors.ImageSharp.PixelFormats;

namespace LexExport;

// The compiled shaders eight texels at a time (AVX2). Every register component is its own
// Vector256<float> local, a lane per texel, so swizzles and write masks cost nothing and each
// instruction is a few eight-wide operations instead of one Vector4's. The runs that differ in
// one operand (RunsCompiler) share their work the same way, per component.
//
// The bakes stay bit-identical: every component goes through the same float operations, in the
// same order, as the Vector4 code (Vector4.Dot sums in pairs; Vector256's Min, Max, Clamp, Abs,
// and negation match Vector4's on NaN and signed zeros, checked over 2 million inputs), and
// texture samples and the transcendental functions run per lane through the same scalar code.
public static partial class ShaderBaker
{
    // LEXEXPORT_SCALAR=1 bakes a texel at a time, to compare the two.
    private static readonly bool WideEnabled = Avx2.IsSupported && Environment.GetEnvironmentVariable("LEXEXPORT_SCALAR") != "1";
    private const int Lanes = 8;

    // inputs[4r + c]: input register r's component c; variants[4k + c]: the varying operand in run
    // k; constants: the constant buffer's components (4i + c), then the literals'; outputs[8k + c]
    // and [8k + 4 + c]: run k's o0 and o2, then at [8 × runs] the mask of texels kept (all bits
    // set) or discarded (zero).
    private delegate void Wide(Vector256<float>[] inputs, Vector256<float>[] variants, Vector256<float>[] constants, Vector256<float>[] outputs);

    // A sample's four channels, eight texels each.
    private struct Quad
    {
        public Vector256<float> X, Y, Z, W;
    }

    private sealed class WideCompiler
    {
        private static readonly string[] Channels = ["X", "Y", "Z", "W"];

        private readonly CompiledShader _shader;
        private readonly Instruction[] _code;
        private readonly Texels[] _textures;
        private readonly int _runs;
        private readonly Func<Operand, bool> _variant;
        private readonly ParameterExpression _inputs = Expression.Parameter(typeof(Vector256<float>[]), "inputs");
        private readonly ParameterExpression _variants = Expression.Parameter(typeof(Vector256<float>[]), "variants");
        private readonly ParameterExpression _constants = Expression.Parameter(typeof(Vector256<float>[]), "constants");
        private readonly ParameterExpression _outputs = Expression.Parameter(typeof(Vector256<float>[]), "outputs");
        // The literals after the constant buffer's components, by their bits.
        private readonly List<float> _literals = new();
        private readonly Dictionary<uint, int> _literalIndex = new();

        // Registers are numbered temps first, then the 8 outputs. Each component is written or not
        // yet (reads as zero, as the machine clears the registers), and shared (run 0's local holds
        // it for every run) or per run.
        private readonly int _registers;
        private readonly bool[,] _written, _perRun;
        private readonly Dictionary<(int Register, int Component, int Run), ParameterExpression> _locals = new();

        // Texels discarded so far end with their outputs as they were when discarded.
        private readonly bool _hasDiscards;
        private readonly ParameterExpression _alive = Expression.Variable(typeof(Vector256<float>), "alive");
        private readonly ParameterExpression[,,] _saved;
        private bool _discardVaries;

        private WideCompiler(CompiledShader shader, Instruction[] code, Texels[] textures, int runs, Func<Operand, bool> variant)
        {
            _shader = shader;
            _code = code;
            _textures = textures;
            _runs = runs;
            _variant = variant;
            _registers = shader.Temps + 8;
            _written = new bool[_registers, 4];
            _perRun = new bool[_registers, 4];
            _hasDiscards = code.Any(ins => ins.Code is OpCode.DiscardNz or OpCode.DiscardZ);
            _saved = new ParameterExpression[runs, 2, 4];
            for (int k = 0; k < runs; k++)
                for (int slot = 0; slot < 2; slot++)
                    for (int c = 0; c < 4; c++)
                        _saved[k, slot, c] = Expression.Variable(typeof(Vector256<float>), $"saved{k}_{slot}_{c}");
        }

        // Null when a discard depends on the variant (the runs could end at different points);
        // the scalar compilers handle those.
        public static Wide Compile(CompiledShader shader, Instruction[] code, Texels[] textures, int runs, Func<Operand, bool> variant,
            float[] constants, out Vector256<float>[] wideConstants, out int shared)
        {
            wideConstants = null;
            shared = 0;
            WideCompiler compiler = new WideCompiler(shader, code, textures, runs, variant);
            List<Expression> body = new List<Expression>();
            if (compiler._hasDiscards)
            {
                body.Add(Expression.Assign(compiler._alive, Expression.Constant(Vector256<float>.AllBitsSet)));
                foreach (ParameterExpression saved in compiler._saved)
                    body.Add(Expression.Assign(saved, Expression.Default(typeof(Vector256<float>))));
            }
            for (int i = 0; i < code.Length; i++)
                body.Add(compiler.Emit(i, ref shared));
            if (compiler._discardVaries)
                return null;
            body.Add(compiler.StoreOutputs());

            List<ParameterExpression> variables = compiler._locals.Values.ToList();
            if (compiler._hasDiscards)
            {
                variables.Add(compiler._alive);
                variables.AddRange(compiler._saved.Cast<ParameterExpression>());
            }
            BlockExpression block = Expression.Block(typeof(void), variables, body);
            Wide wide = Expression.Lambda<Wide>(block, compiler._inputs, compiler._variants, compiler._constants, compiler._outputs).Compile();

            int buffer = shader.ConstantCount * 4;
            wideConstants = new Vector256<float>[buffer + compiler._literals.Count];
            for (int i = 0; i < buffer; i++)
                wideConstants[i] = Vector256.Create(constants[i]);
            for (int i = 0; i < compiler._literals.Count; i++)
                wideConstants[buffer + i] = Vector256.Create(compiler._literals[i]);
            return wide;
        }

        private Expression Emit(int index, ref int shared)
        {
            Instruction ins = _code[index];
            Operand[] o = ins.Operands;
            if (ins.Code == OpCode.Unsupported)
            {
                // Thrown only when a texel gets this far, as with the interpreter.
                Expression unsupported = ShaderBaker.Unsupported(ins);
                return _hasDiscards ? Expression.IfThen(Call(nameof(W8.Any), _alive), unsupported) : unsupported;
            }
            if (ins.Code is OpCode.DiscardNz or OpCode.DiscardZ)
                return Discard(ins);

            // Each destination component's value, per run when its sources differ between runs.
            bool sincos = ins.Code == OpCode.Sincos;
            List<(Operand Destination, int Component, bool Varies, Expression[] Values)> writes = new();
            Operand[] destinations = sincos ? [o[0], o[1]] : [o[0]];
            // A dot product or a sample computes once per run for all its components.
            Dictionary<int, Expression> perRun = new Dictionary<int, Expression>();
            List<ParameterExpression> temps = new List<ParameterExpression>();
            List<Expression> steps = new List<Expression>();
            bool anyVaries = false;
            for (int d = 0; d < destinations.Length; d++)
            {
                Operand destination = destinations[d];
                if (destination.Kind is not (Kind.Temp or Kind.Output))
                    continue;
                for (int c = 0; c < 4; c++)
                {
                    if (!destination.Mask[c])
                        continue;
                    bool varies = Varies(ins, c);
                    anyVaries |= varies;
                    Expression[] values = new Expression[varies ? _runs : 1];
                    for (int k = 0; k < values.Length; k++)
                    {
                        Expression value = Value(index, d, c, k, perRun, temps, steps);
                        values[k] = ins.Saturate ? Call(nameof(W8.Saturate), value) : value;
                    }
                    writes.Add((destination, c, varies, values));
                }
            }
            if (!anyVaries)
                shared++;

            // An instruction reading a register it writes computes every value before any write.
            HashSet<int> read = o[(sincos ? 2 : 1)..].Where(s => s.Kind is Kind.Temp or Kind.Output).Select(Register).ToHashSet();
            bool staged = writes.Any(w => read.Contains(Register(w.Destination)));
            List<(ParameterExpression Local, Expression Value)> assignments = new();
            foreach ((Operand destination, int c, bool varies, Expression[] values) in writes)
            {
                int register = Register(destination);
                for (int k = 0; k < values.Length; k++)
                    assignments.Add((Local(register, c, k), values[k]));
            }
            if (staged)
            {
                List<Expression> late = new List<Expression>();
                foreach ((ParameterExpression local, Expression value) in assignments)
                {
                    ParameterExpression temp = Expression.Variable(typeof(Vector256<float>), "w");
                    temps.Add(temp);
                    steps.Add(Expression.Assign(temp, value));
                    late.Add(Expression.Assign(local, temp));
                }
                steps.AddRange(late);
            }
            else
                steps.AddRange(assignments.Select(a => (Expression)Expression.Assign(a.Local, a.Value)));
            foreach ((Operand destination, int c, bool varies, _) in writes)
            {
                _written[Register(destination), c] = true;
                _perRun[Register(destination), c] = varies;
            }
            steps.Add(Expression.Empty());
            return Expression.Block(temps, steps);
        }

        // Whether destination component c's value differs between runs.
        private bool Varies(Instruction ins, int c)
        {
            Operand[] o = ins.Operands;
            switch (ins.Code)
            {
                case OpCode.Sincos:
                    return Varies(o[2], c);
                case OpCode.Sample:
                    return Varies(o[1], 0) || Varies(o[1], 1);
                case OpCode.Dp2:
                case OpCode.Dp3:
                case OpCode.Dp4:
                {
                    int n = ins.Code == OpCode.Dp2 ? 2 : ins.Code == OpCode.Dp3 ? 3 : 4;
                    return Enumerable.Range(0, n).Any(i => Varies(o[1], i) || Varies(o[2], i));
                }
                case OpCode.Deriv:
                    return false;
                default:
                    return o[1..].Any(s => Varies(s, c));
            }
        }

        // Whether a source's component c (after its swizzle) differs between runs.
        private bool Varies(Operand op, int c)
        {
            if (_variant(op))
                return true;
            if (op.Kind is not (Kind.Temp or Kind.Output))
                return false;
            int register = Register(op), component = op.Swizzle[c];
            return _written[register, component] && _perRun[register, component];
        }

        // Destination d's component c in run k.
        private Expression Value(int index, int d, int c, int k, Dictionary<int, Expression> perRun,
            List<ParameterExpression> temps, List<Expression> steps)
        {
            Instruction ins = _code[index];
            Operand[] o = ins.Operands;
            Expression S(int i) => Read(o[i], c, k);
            switch (ins.Code)
            {
                case OpCode.Sincos:
                    return Call(d == 0 ? nameof(W8.Sin) : nameof(W8.Cos), Read(o[2], c, k));
                case OpCode.Sample:
                {
                    Texels texels = _textures[index];
                    if (texels == null)
                        return Expression.Default(typeof(Vector256<float>));
                    if (!perRun.TryGetValue(k, out Expression quad))
                    {
                        ParameterExpression sampled = Expression.Variable(typeof(Quad), "sampled");
                        temps.Add(sampled);
                        steps.Add(Expression.Assign(sampled, Expression.Call(typeof(W8).GetMethod(nameof(W8.Sample))!,
                            Expression.Constant(texels), Read(o[1], 0, k), Read(o[1], 1, k))));
                        perRun[k] = quad = sampled;
                    }
                    return Expression.Field(quad, Channels[o[2].Swizzle[c]]);
                }
                case OpCode.Dp2:
                case OpCode.Dp3:
                case OpCode.Dp4:
                {
                    if (!perRun.TryGetValue(k, out Expression dot))
                    {
                        int n = ins.Code == OpCode.Dp2 ? 2 : ins.Code == OpCode.Dp3 ? 3 : 4;
                        Expression[] args = Enumerable.Range(0, n).SelectMany(i => new[] { Read(o[1], i, k), Read(o[2], i, k) }).ToArray();
                        ParameterExpression result = Expression.Variable(typeof(Vector256<float>), "dot");
                        temps.Add(result);
                        steps.Add(Expression.Assign(result, Call(n == 2 ? nameof(W8.Dp2) : n == 3 ? nameof(W8.Dp3) : nameof(W8.Dp4), args)));
                        perRun[k] = dot = result;
                    }
                    return dot;
                }
                case OpCode.Mov: return S(1);
                case OpCode.Add: return Call(nameof(W8.Add), S(1), S(2));
                case OpCode.Mul: return Call(nameof(W8.Mul), S(1), S(2));
                case OpCode.Mad: return Call(nameof(W8.Mad), S(1), S(2), S(3));
                case OpCode.Div: return Call(nameof(W8.Div), S(1), S(2));
                case OpCode.Min: return Call(nameof(W8.Min), S(1), S(2));
                case OpCode.Max: return Call(nameof(W8.Max), S(1), S(2));
                case OpCode.Sqrt: return Call(nameof(W8.Sqrt), S(1));
                case OpCode.Rsq: return Call(nameof(W8.Rsq), S(1));
                case OpCode.Log: return Call(nameof(W8.Log), S(1));
                case OpCode.Exp: return Call(nameof(W8.Exp), S(1));
                case OpCode.Frc: return Call(nameof(W8.Frc), S(1));
                case OpCode.RoundNe: return Call(nameof(W8.RoundNe), S(1));
                case OpCode.RoundNi: return Call(nameof(W8.RoundNi), S(1));
                case OpCode.RoundPi: return Call(nameof(W8.RoundPi), S(1));
                case OpCode.RoundZ: return Call(nameof(W8.RoundZ), S(1));
                case OpCode.Lt: return Call(nameof(W8.Lt), S(1), S(2));
                case OpCode.Ge: return Call(nameof(W8.Ge), S(1), S(2));
                case OpCode.Eq: return Call(nameof(W8.Eq), S(1), S(2));
                case OpCode.Ne: return Call(nameof(W8.Ne), S(1), S(2));
                case OpCode.Movc: return Call(nameof(W8.Movc), S(1), S(2), S(3));
                case OpCode.And: return Call(nameof(W8.And), S(1), S(2));
                case OpCode.Or: return Call(nameof(W8.Or), S(1), S(2));
                // Screen-space derivatives are zero over a flat texel grid.
                case OpCode.Deriv: return Expression.Default(typeof(Vector256<float>));
                default: throw new InvalidOperationException($"no value for {ins.Op}");
            }
        }

        // A discard is shared by every run; each discarded texel keeps its outputs as they are now.
        private Expression Discard(Instruction ins)
        {
            Operand source = ins.Operands[0];
            if (Varies(source, 0))
            {
                _discardVaries = true;
                return Expression.Empty();
            }
            ParameterExpression discarded = Expression.Variable(typeof(Vector256<float>), "discarded");
            ParameterExpression newly = Expression.Variable(typeof(Vector256<float>), "newly");
            List<Expression> steps = new List<Expression>
            {
                Expression.Assign(discarded, Call(ins.Code == OpCode.DiscardNz ? nameof(W8.NonZero) : nameof(W8.IsZero), Read(source, 0, 0))),
                Expression.Assign(newly, Call(nameof(W8.And), discarded, _alive)),
            };
            for (int k = 0; k < _runs; k++)
                for (int slot = 0; slot < 2; slot++)
                    for (int c = 0; c < 4; c++)
                        steps.Add(Expression.Assign(_saved[k, slot, c], Call(nameof(W8.Select), newly, Output(slot, c, k), _saved[k, slot, c])));
            steps.Add(Expression.Assign(_alive, Call(nameof(W8.AndNot), _alive, discarded)));
            return Expression.Block([discarded, newly], steps);
        }

        // Every run's o0 and o2, discarded texels' as they were when discarded, and the kept mask.
        private Expression StoreOutputs()
        {
            List<Expression> steps = new List<Expression>();
            for (int k = 0; k < _runs; k++)
                for (int slot = 0; slot < 2; slot++)
                    for (int c = 0; c < 4; c++)
                    {
                        Expression value = _hasDiscards ? Call(nameof(W8.Select), _alive, Output(slot, c, k), _saved[k, slot, c]) : Output(slot, c, k);
                        steps.Add(Expression.Assign(Expression.ArrayAccess(_outputs, Expression.Constant(8 * k + 4 * slot + c)), value));
                    }
            steps.Add(Expression.Assign(Expression.ArrayAccess(_outputs, Expression.Constant(8 * _runs)),
                _hasDiscards ? _alive : Expression.Constant(Vector256<float>.AllBitsSet)));
            return Expression.Block(steps);
        }

        // o0 (slot 0) or o2 (slot 1), component c, in run k.
        private Expression Output(int slot, int c, int k) => Current(_shader.Temps + (slot == 0 ? 0 : 2), c, k);

        private Expression Current(int register, int c, int k) =>
            !_written[register, c] ? Expression.Default(typeof(Vector256<float>)) : Local(register, c, _perRun[register, c] ? k : 0);

        private ParameterExpression Local(int register, int c, int k)
        {
            if (!_locals.TryGetValue((register, c, k), out ParameterExpression local))
                _locals[(register, c, k)] = local = Expression.Variable(typeof(Vector256<float>), $"x{register}_{c}_{k}");
            return local;
        }

        private int Register(Operand op) => op.Kind == Kind.Temp ? op.Index : _shader.Temps + op.Index;

        // A source's component c in run k, its swizzle and modifiers applied (a literal isn't
        // swizzled, as in the scalar compilers).
        private Expression Read(Operand op, int c, int k)
        {
            int component = op.Kind == Kind.Literal ? c : op.Swizzle[c];
            Expression value;
            if (_variant(op))
                value = Expression.ArrayIndex(_variants, Expression.Constant(4 * k + component));
            else
                value = op.Kind switch
                {
                    Kind.Temp or Kind.Output => Current(Register(op), component, k),
                    Kind.Input => op.Index < _shader.Inputs
                        ? Expression.ArrayIndex(_inputs, Expression.Constant(4 * op.Index + component))
                        : Expression.Default(typeof(Vector256<float>)),
                    Kind.Constant => op.Index < _shader.ConstantCount
                        ? Expression.ArrayIndex(_constants, Expression.Constant(4 * op.Index + component))
                        : Expression.Default(typeof(Vector256<float>)),
                    Kind.Literal => Literal(component == 0 ? op.Value.X : component == 1 ? op.Value.Y : component == 2 ? op.Value.Z : op.Value.W),
                    _ => Expression.Default(typeof(Vector256<float>)),
                };
            if (op.Absolute)
                value = Call(nameof(W8.Abs), value);
            if (op.Negate)
                value = Call(nameof(W8.Neg), value);
            return value;
        }

        private Expression Literal(float value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            if (!_literalIndex.TryGetValue(bits, out int index))
            {
                _literalIndex[bits] = index = _literals.Count;
                _literals.Add(value);
            }
            return Expression.ArrayIndex(_constants, Expression.Constant(_shader.ConstantCount * 4 + index));
        }

        private static Expression Call(string name, params Expression[] args) => Expression.Call(typeof(W8).GetMethod(name)!, args);
    }

    // The instructions' math eight lanes at a time, matching Ops (the Vector4 versions) bit for bit.
    private static class W8
    {
        private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

        [MethodImpl(Inline)] public static Vector256<float> Add(Vector256<float> a, Vector256<float> b) => a + b;
        [MethodImpl(Inline)] public static Vector256<float> Mul(Vector256<float> a, Vector256<float> b) => a * b;
        [MethodImpl(Inline)] public static Vector256<float> Mad(Vector256<float> a, Vector256<float> b, Vector256<float> c) => a * b + c;
        [MethodImpl(Inline)] public static Vector256<float> Div(Vector256<float> a, Vector256<float> b) => a / b;
        [MethodImpl(Inline)] public static Vector256<float> Min(Vector256<float> a, Vector256<float> b) => Vector256.Min(a, b);
        [MethodImpl(Inline)] public static Vector256<float> Max(Vector256<float> a, Vector256<float> b) => Vector256.Max(a, b);
        [MethodImpl(Inline)] public static Vector256<float> Sqrt(Vector256<float> v) => Vector256.Sqrt(v);
        [MethodImpl(Inline)] public static Vector256<float> Rsq(Vector256<float> v) => Vector256<float>.One / Vector256.Sqrt(v);
        [MethodImpl(Inline)] public static Vector256<float> Frc(Vector256<float> v) => v - Avx.Floor(v);
        [MethodImpl(Inline)] public static Vector256<float> RoundNe(Vector256<float> v) => Avx.RoundToNearestInteger(v);
        [MethodImpl(Inline)] public static Vector256<float> RoundNi(Vector256<float> v) => Avx.Floor(v);
        [MethodImpl(Inline)] public static Vector256<float> RoundPi(Vector256<float> v) => Avx.Ceiling(v);
        [MethodImpl(Inline)] public static Vector256<float> RoundZ(Vector256<float> v) => Avx.RoundToZero(v);

        [MethodImpl(Inline)]
        public static Vector256<float> Dp2(Vector256<float> ax, Vector256<float> bx, Vector256<float> ay, Vector256<float> by) => ax * bx + ay * by;

        [MethodImpl(Inline)]
        public static Vector256<float> Dp3(Vector256<float> ax, Vector256<float> bx, Vector256<float> ay, Vector256<float> by,
            Vector256<float> az, Vector256<float> bz) => ax * bx + ay * by + az * bz;

        // Vector4.Dot sums its products in pairs.
        [MethodImpl(Inline)]
        public static Vector256<float> Dp4(Vector256<float> ax, Vector256<float> bx, Vector256<float> ay, Vector256<float> by,
            Vector256<float> az, Vector256<float> bz, Vector256<float> aw, Vector256<float> bw) => (ax * bx + ay * by) + (az * bz + aw * bw);

        [MethodImpl(Inline)] public static Vector256<float> Lt(Vector256<float> a, Vector256<float> b) => Vector256.LessThan(a, b);
        [MethodImpl(Inline)] public static Vector256<float> Ge(Vector256<float> a, Vector256<float> b) => Vector256.GreaterThanOrEqual(a, b);
        [MethodImpl(Inline)] public static Vector256<float> Eq(Vector256<float> a, Vector256<float> b) => Vector256.Equals(a, b);
        [MethodImpl(Inline)] public static Vector256<float> Ne(Vector256<float> a, Vector256<float> b) => ~Vector256.Equals(a, b);

        // Any bit set picks a, as the scalar Movc does.
        [MethodImpl(Inline)]
        public static Vector256<float> Movc(Vector256<float> c, Vector256<float> a, Vector256<float> b) =>
            Vector256.ConditionalSelect(IsZero(c), b, a);

        [MethodImpl(Inline)] public static Vector256<float> And(Vector256<float> a, Vector256<float> b) => a & b;
        [MethodImpl(Inline)] public static Vector256<float> Or(Vector256<float> a, Vector256<float> b) => a | b;
        [MethodImpl(Inline)] public static Vector256<float> AndNot(Vector256<float> a, Vector256<float> b) => a & ~b;
        [MethodImpl(Inline)] public static Vector256<float> Saturate(Vector256<float> v) => Vector256.Clamp(v, Vector256<float>.Zero, Vector256<float>.One);
        [MethodImpl(Inline)] public static Vector256<float> Abs(Vector256<float> v) => Vector256.Abs(v);
        [MethodImpl(Inline)] public static Vector256<float> Neg(Vector256<float> v) => -v;
        [MethodImpl(Inline)] public static Vector256<float> Select(Vector256<float> mask, Vector256<float> a, Vector256<float> b) => Vector256.ConditionalSelect(mask, a, b);
        [MethodImpl(Inline)] public static bool Any(Vector256<float> mask) => mask.AsUInt32() != Vector256<uint>.Zero;

        // All bits set where the lane's bits are all zero, else zero.
        [MethodImpl(Inline)] public static Vector256<float> IsZero(Vector256<float> v) => Vector256.Equals(v.AsUInt32(), Vector256<uint>.Zero).AsSingle();
        [MethodImpl(Inline)] public static Vector256<float> NonZero(Vector256<float> v) => ~IsZero(v);

        // The transcendental functions lane by lane, through the same calls as Ops.
        public static Vector256<float> Log(Vector256<float> v) => PerLane(v, MathF.Log2);
        public static Vector256<float> Exp(Vector256<float> v) => PerLane(v, x => MathF.Pow(2f, x));
        public static Vector256<float> Sin(Vector256<float> v) => PerLane(v, MathF.Sin);
        public static Vector256<float> Cos(Vector256<float> v) => PerLane(v, MathF.Cos);

        private static Vector256<float> PerLane(Vector256<float> v, Func<float, float> f)
        {
            Span<float> lanes = stackalloc float[Lanes];
            for (int i = 0; i < Lanes; i++)
                lanes[i] = f(v.GetElement(i));
            return Vector256.Create<float>(lanes);
        }

        // Eight bilinear samples (Texels.SampleWide).
        public static Quad Sample(Texels texels, Vector256<float> u, Vector256<float> v) => texels.SampleWide(u, v);
    }

    // LEXEXPORT_WIDE_CHECK=1 runs each bake's shaders through the scalar compilers too (every 4th
    // row) and prints where the two differ.
    private static readonly bool WideCheck = Environment.GetEnvironmentVariable("LEXEXPORT_WIDE_CHECK") == "1";

    private static void CheckWide(Program program, float[] constants, Dictionary<int, Texels> slots, int width, int height,
        Program lightProgram, float[] lightConstants, CompiledShader wide, CompiledShader lightWide, Rgba32[] normals)
    {
        static bool Same(Vector4 a, Vector4 b) => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
            && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
            && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z)
            && BitConverter.SingleToUInt32Bits(a.W) == BitConverter.SingleToUInt32Bits(b.W);

        CompiledShader scalar = new CompiledShader(program, constants, slots, wide: false);
        Machine machine = new Machine(scalar, constants);
        WideMachine wideMachine = new WideMachine(wide);
        int lit = wide.WideRuns - 1;
        int checkedTexels = 0;
        Dictionary<string, (int Count, string First)> bad = new();
        void Note(string what, string detail)
        {
            bad.TryGetValue(what, out (int Count, string First) entry);
            bad[what] = (entry.Count + 1, entry.First ?? detail);
        }
        Span<float> u = stackalloc float[Lanes];
        for (int y = 0; y < height; y += 4)
            for (int x0 = 0; x0 < width; x0 += Lanes)
            {
                for (int lane = 0; lane < Lanes; lane++)
                    u[lane] = (Math.Min(x0 + lane, width - 1) + 0.5f) / width;
                wideMachine.SetUv(Vector256.Create<float>(u), Vector256.Create((y + 0.5f) / height));
                wideMachine.Run();
                for (int lane = 0; lane < Lanes && x0 + lane < width; lane++)
                {
                    int x = x0 + lane;
                    bool kept = machine.Run(new Vector2((x + 0.5f) / width, (y + 0.5f) / height), out Vector4 emit, out Vector4 litColor, out Vector4 viewNormal);
                    checkedTexels++;
                    string at = $"({x}, {y})";
                    if (kept != wideMachine.Kept(lane))
                        Note("kept", $"{at}: scalar {kept}");
                    if (!Same(emit, wideMachine.Color(0, lane)))
                        Note("emit", $"{at}: scalar {emit:R} wide {wideMachine.Color(0, lane):R}");
                    if (!Same(litColor, wideMachine.Color(lit, lane)))
                        Note("lit", $"{at}: scalar {litColor:R} wide {wideMachine.Color(lit, lane):R}");
                    if (!Same(viewNormal, wideMachine.Normal(lit, lane)))
                        Note("normal", $"{at}: scalar {viewNormal:R} wide {wideMachine.Normal(lit, lane):R}");
                }
            }

        if (lightWide?.Wide != null)
        {
            CompiledShader lightScalar = new CompiledShader(lightProgram, lightConstants, slots, lit: true, wide: false);
            Machine lightMachine = new Machine(lightScalar, lightConstants);
            WideMachine lightWideMachine = new WideMachine(lightWide);
            Vector3 n = Vector3.Normalize(new Vector3(0.2f, -0.3f, 0.9f));
            (Vector3 across, Vector3 tilted) = SpecularCameras(n);
            Vector256<float> Broadcast(float f) => Vector256.Create(f);
            lightWideMachine.SetLight(Broadcast(n.X), Broadcast(n.Y), Broadcast(n.Z));
            lightWideMachine.SetVariant(0, Broadcast(n.X), Broadcast(n.Y), Broadcast(n.Z), Vector256<float>.Zero);
            lightWideMachine.SetVariant(1, Broadcast(across.X), Broadcast(across.Y), Broadcast(across.Z), Vector256<float>.Zero);
            lightWideMachine.SetVariant(2, Broadcast(tilted.X), Broadcast(tilted.Y), Broadcast(tilted.Z), Vector256<float>.Zero);
            for (int y = 0; y < height; y += 16)
                for (int x0 = 0; x0 < width; x0 += Lanes * 4)
                {
                    for (int lane = 0; lane < Lanes; lane++)
                        u[lane] = (Math.Min(x0 + lane, width - 1) + 0.5f) / width;
                    lightWideMachine.SetUv(Vector256.Create<float>(u), Vector256.Create((y + 0.5f) / height));
                    lightWideMachine.Run();
                    for (int lane = 0; lane < Lanes && x0 + lane < width; lane++)
                    {
                        int x = x0 + lane;
                        lightMachine.RunLit(new Vector2((x + 0.5f) / width, (y + 0.5f) / height), n, n, across, tilted,
                            out Vector4 full, out Vector4 none, out Vector4 partial);
                        Vector4[] scalarColors = [full, none, partial];
                        for (int k = 0; k < 3; k++)
                            if (!Same(scalarColors[k], lightWideMachine.Color(k, lane)))
                                Note($"specular{k}", $"({x}, {y}): scalar {scalarColors[k]:R} wide {lightWideMachine.Color(k, lane):R}");
                    }
                }
        }
        Console.WriteLine(bad.Count == 0
            ? $"  wide check: {checkedTexels} texels identical"
            : $"  wide check: {string.Join("; ", bad.Select(kv => $"{kv.Key} differs at {kv.Value.Count}, first {kv.Value.First}"))}");
    }

    // Runs a wide shader for one thread: its inputs, the variants, and the outputs it reads back.
    private sealed class WideMachine
    {
        private readonly CompiledShader _shader;
        private readonly Vector256<float>[] _inputs, _variants, _outputs;

        public WideMachine(CompiledShader shader)
        {
            _shader = shader;
            _inputs = new Vector256<float>[shader.InputValues.Length * 4];
            for (int r = 0; r < shader.InputValues.Length; r++)
            {
                Vector4 value = shader.InputValues[r];
                _inputs[4 * r] = Vector256.Create(value.X);
                _inputs[4 * r + 1] = Vector256.Create(value.Y);
                _inputs[4 * r + 2] = Vector256.Create(value.Z);
                _inputs[4 * r + 3] = Vector256.Create(value.W);
            }
            _variants = new Vector256<float>[shader.WideRuns * 4];
            _outputs = new Vector256<float>[shader.WideRuns * 8 + 1];
            // The base pass's runs: ambient at 0 (the emissive), then at 1 (the lit color). The
            // specular bake sets its cameras per call.
            if (shader.WideRuns == 2)
                SetVariant(1, Vector256<float>.One, Vector256<float>.One, Vector256<float>.One, Vector256<float>.One);
        }

        public int Runs => _shader.WideRuns;

        // TEXCOORD0, (u, v, 0, 0) a lane per texel.
        public void SetUv(Vector256<float> u, Vector256<float> v)
        {
            foreach (int register in _shader.UvRegisters)
            {
                _inputs[4 * register] = u;
                _inputs[4 * register + 1] = v;
                _inputs[4 * register + 2] = Vector256<float>.Zero;
                _inputs[4 * register + 3] = Vector256<float>.Zero;
            }
        }

        // The light vector (x, y, z, 0), for the specular bake.
        public void SetLight(Vector256<float> x, Vector256<float> y, Vector256<float> z)
        {
            foreach (int register in _shader.LightRegisters)
            {
                _inputs[4 * register] = x;
                _inputs[4 * register + 1] = y;
                _inputs[4 * register + 2] = z;
                _inputs[4 * register + 3] = Vector256<float>.Zero;
            }
        }

        public void SetVariant(int run, Vector256<float> x, Vector256<float> y, Vector256<float> z, Vector256<float> w)
        {
            _variants[4 * run] = x;
            _variants[4 * run + 1] = y;
            _variants[4 * run + 2] = z;
            _variants[4 * run + 3] = w;
        }

        public void Run() => _shader.Wide(_inputs, _variants, _shader.WideConstants, _outputs);

        // Run k's o0 or o2 at one lane.
        public Vector4 Color(int run, int lane) => Lane(8 * run, lane);
        public Vector4 Normal(int run, int lane) => Lane(8 * run + 4, lane);
        public bool Kept(int lane) => BitConverter.SingleToUInt32Bits(_outputs[8 * _shader.WideRuns].GetElement(lane)) != 0;

        private Vector4 Lane(int at, int lane) => new Vector4(_outputs[at].GetElement(lane), _outputs[at + 1].GetElement(lane),
            _outputs[at + 2].GetElement(lane), _outputs[at + 3].GetElement(lane));
    }
}
