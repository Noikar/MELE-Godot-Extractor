using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;

namespace LexExport;

// Reads what a cooked material actually draws from its compiled pixel shader. Cooking strips
// the expression graph, but every package's SeekFreeShaderCache keeps the base-pass shaders
// (DX11 bytecode in LE), which the system's d3dcompiler disassembles. A small symbolic pass
// then reduces the shader's color and opacity outputs to "constant + weighted terms", where
// a term is one channel of one texture sample, or something opaque (cubemaps, scene color,
// view-dependent math), so the exporter can bake the result into an ordinary texture.
public static class ShaderAnalysis
{
    // One texture sample in the shader: which material texture slot (Texture2D_<n>), which
    // channel (0..3), and whether it reads the mesh's first UV set (scaled, maybe panning over
    // time) or anything else (view direction, screen position).
    public record Sample(int Slot, int Channel, bool Uv0, float ScaleU, float ScaleV, bool Panning);

    // A shader's outputs: Color is RGB, Opacity is alpha. Samples describes each texture term,
    // and Textures maps its slots to the material's textures.
    public record Result(Linear[] Color, Linear Opacity, Dictionary<string, Sample> Samples,
        MaterialUniformExpressionTexture[] Textures = null);

    // A material instance's value for a vector or scalar parameter, or null for the default.
    public delegate double[] ParameterLookup(string name);

    // The average of one channel of the texture behind a shader texture slot, or null.
    public delegate double? TextureMean(MaterialUniformExpressionTexture texture, int channel);

    // Parsed shader caches by package path, shared by every entry of a run (and thread-safe:
    // entries export side by side). Lazy, so two threads asking at once parse it only once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<ShaderCache>> Caches = new(StringComparer.OrdinalIgnoreCase);

    // The shader map a material draws with. An instance that sets static switches
    // (bHasStaticPermutationResource) has a compiled permutation of its own, keyed by its static
    // parameters (Volumetric_1SIDED_MM has four: with or without the density texture and the
    // far fade); anything else draws with its base material's map. textures is the map's
    // default textures by slot (a permutation keeps its own list), and staticNote names the
    // switches that are on when the instance's own permutation was found.
    public static MaterialShaderMap ShaderMapOf(ShaderCache cache, ExportEntry material, ExportEntry baseMaterial,
        out int[] textures, out string staticNote)
    {
        staticNote = null;
        for (ExportEntry current = material; current != null && current.ClassName.StartsWith("MaterialInstance");)
        {
            var props = current.GetProperties();
            if (props.GetProp<BoolProperty>("bHasStaticPermutationResource")?.Value == true)
            {
                var instance = ObjectBinary.From<MaterialInstance>(current);
                StaticParameterSet set = instance.SM3StaticParameterSet;
                MaterialShaderMap own = cache.MaterialShaderMaps.Values.FirstOrDefault(m => m.StaticParameters == set);
                if (own != null)
                {
                    textures = instance.SM3StaticPermutationResource.UniformExpressionTextures;
                    string[] on = set.StaticSwitchParameters.Where(s => s.Value).Select(s => s.ParameterName.Instanced).ToArray();
                    staticNote = "static switches: " + (on.Length > 0 ? string.Join(", ", on) : "all off");
                    return own;
                }
                break;
            }
            int parent = props.GetProp<ObjectProperty>("Parent")?.Value ?? 0;
            current = current.FileRef.TryGetUExport(parent, out ExportEntry next) ? next : null;
        }
        MaterialResource resource = ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource;
        textures = resource.UniformExpressionTextures;
        return cache.MaterialShaderMaps.Values.FirstOrDefault(m => m.StaticParameters.BaseMaterialId == resource.ID);
    }

    // The analyzed base-pass pixel shader of a Material, or null (with a reason) when the
    // package has no shader for it or the shader uses something the pass can't follow.
    public static Result Analyze(IMEPackage pcc, ExportEntry baseMaterial, ParameterLookup parameters, TextureMean mean,
        out string failure)
    {
        failure = null;
        ShaderCache cache = CacheOf(pcc);
        if (cache == null)
        {
            failure = "no shader cache";
            return null;
        }
        Guid id = ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource.ID;
        MaterialShaderMap map = cache.MaterialShaderMaps.Values.FirstOrDefault(m => m.StaticParameters.BaseMaterialId == id);
        if (map == null)
        {
            failure = "no shader map";
            return null;
        }

        // Static meshes use the local vertex factory. Unlit materials have one base pass
        // shader; lit ones have one per light-map policy, and any of them carries the same
        // material expressions.
        var shaders = map.MeshShaderMaps
            .Where(m => m.VertexFactoryType.Instanced == "FLocalVertexFactory")
            .SelectMany(m => m.Shaders)
            .Where(s => s.Key.Instanced.StartsWith("TBasePassPixelShader"))
            .OrderBy(s => s.Key.Instanced.Contains("NoLightMapPolicy") ? 0 : 1)
            .ToList();
        foreach (var (_, reference) in shaders)
        {
            if (!cache.Shaders.TryGetValue(reference.Id, out Shader shader))
                continue;
            string asm = Disassemble(shader.ShaderByteCode);
            if (asm == null)
                continue;
            Result result = new Evaluator(asm, map, parameters, mean).Run(out failure);
            return result == null ? null : result with { Textures = map.Uniform2DTextureExpressions };
        }
        failure = "no base pass pixel shader";
        return null;
    }

    internal static ShaderCache CacheOf(IMEPackage pcc) =>
        Caches.GetOrAdd(pcc.FilePath, _ => new Lazy<ShaderCache>(() => Parse(pcc))).Value;

    // A package file's shader cache, read and closed again (the shaders inspection command).
    internal static ShaderCache CacheOfFile(string path) =>
        Caches.GetOrAdd(path, _ => new Lazy<ShaderCache>(() => ParseFile(path))).Value;

    // A package file's shader cache cut down to the materials compiled for one vertex factory and
    // their shaders for it. The game's RefShaderCache is a 731 MB file (ME2's 1 GB) whose parsed
    // cache keeps 1.4 GB (2 GB) for the whole run; the character bakes only read its skinned
    // (FGPUSkinVertexFactory) shaders.
    internal static ShaderCache CacheOfFile(string path, string vertexFactory) =>
        Caches.GetOrAdd(path + "|" + vertexFactory, _ => new Lazy<ShaderCache>(() =>
        {
            ShaderCache full = ParseFile(path);
            if (full == null)
                return null;
            var maps = new UMultiMap<StaticParameterSet, MaterialShaderMap>();
            var shaders = new UMultiMap<Guid, Shader>();
            // Through Select: LEX's UMap.CopyTo is broken.
            foreach (var (key, map) in full.MaterialShaderMaps.Select(kv => (kv.Key, kv.Value)))
            {
                MeshShaderMap[] meshMaps = map.MeshShaderMaps.Where(m => m.VertexFactoryType.Instanced == vertexFactory).ToArray();
                if (meshMaps.Length == 0)
                    continue;
                maps.Add(key, map);
                foreach (ShaderReference reference in meshMaps.SelectMany(m => m.Shaders.Select(kv => kv.Value)))
                {
                    if (!shaders.ContainsKey(reference.Id) && full.Shaders.TryGetValue(reference.Id, out Shader shader) && shader != null)
                        shaders.Add(reference.Id, shader);
                }
            }
            full.MaterialShaderMaps = maps;
            full.Shaders = shaders;
            return full;
        })).Value;

    private static ShaderCache ParseFile(string path)
    {
        if (!File.Exists(path))
            return null;
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(path);
        return Parse(pcc);
    }

    // Drops a package's parsed cache once its entry is done with it (a character's or a
    // sublevel's own), so a long run doesn't keep every package's shaders.
    internal static void Forget(IMEPackage pcc)
    {
        if (pcc?.FilePath != null)
            Caches.TryRemove(pcc.FilePath, out _);
    }

    private static ShaderCache Parse(IMEPackage pcc)
    {
        ExportEntry export = pcc.Exports.FirstOrDefault(e => e.ClassName == "ShaderCache");
        if (export == null)
            return null;
        ShaderCache cache = ObjectBinary.From<ShaderCache>(export);
        // The parsed cache holds its export, and through it the whole decompressed package.
        // Export is init-only, so it's cleared through its setter by reflection.
        typeof(ObjectBinary).GetProperty(nameof(ObjectBinary.Export))!.SetValue(cache, null);
        return cache;
    }

    // ---------------------------------------------------------------
    // Uniform expressions
    // ---------------------------------------------------------------

    // A material uniform's value: constants, parameters, and math folded from those. Values
    // that change at runtime are taken at time 0 (panners at their start, flicker at its low)
    // and flagged through varies. SelectionColor is the editor's selection highlight, black
    // in game. Null for expression kinds this doesn't know.
    public static double[] Evaluate(MaterialUniformExpression expression, ParameterLookup parameters, ref bool varies)
    {
        double[] Of(MaterialUniformExpression e, ref bool v) => Evaluate(e, parameters, ref v);
        switch (expression)
        {
            case MaterialUniformExpressionConstant c:
                return [c.R, c.G, c.B, c.A];
            case MaterialUniformExpressionVectorParameter v:
                if (v.ParameterName.Instanced == "SelectionColor")
                    return [0, 0, 0, 0];
                return parameters(v.ParameterName.Instanced) ?? [v.DefaultR, v.DefaultG, v.DefaultB, v.DefaultA];
            case MaterialUniformExpressionScalarParameter s:
                return parameters(s.ParameterName.Instanced) ?? [s.DefaultValue];
            case MaterialUniformExpression time when IsTime(time):
                varies = true;
                return [0];
            case MaterialUniformExpressionAppendVector append:
            {
                double[] head = Of(append.A, ref varies), tail = Of(append.B, ref varies);
                return head == null || tail == null ? null : [.. head.Take((int)append.NumComponentsA), .. tail];
            }
            case MaterialUniformExpressionFoldedMath math:
            {
                double[] a = Of(math.A, ref varies), b = Of(math.B, ref varies);
                if (a == null || b == null)
                    return null;
                // FMO_Add, FMO_Sub, FMO_Mul, FMO_Div, FMO_Dot; a single value applies to every component.
                if (math.Op == 4)
                    return [Enumerable.Range(0, Math.Min(a.Length, b.Length)).Sum(i => a[i] * b[i])];
                return Enumerable.Range(0, Math.Max(a.Length, b.Length)).Select(i => math.Op switch
                {
                    0 => At(a, i) + At(b, i),
                    1 => At(a, i) - At(b, i),
                    2 => At(a, i) * At(b, i),
                    _ => At(b, i) != 0 ? At(a, i) / At(b, i) : 0,
                }).ToArray();
            }
            case MaterialUniformExpressionClamp clamp:
            {
                double[] x = Of(clamp.Input, ref varies), lo = Of(clamp.Min, ref varies), hi = Of(clamp.Max, ref varies);
                return x == null || lo == null || hi == null ? null
                    : x.Select((value, i) => Math.Clamp(value, At(lo, i), Math.Max(At(lo, i), At(hi, i)))).ToArray();
            }
            case MaterialUniformExpressionSine sine:
            {
                double[] x = Of(sine.X, ref varies);
                return x?.Select(value => sine.bIsCosine ? Math.Cos(value) : Math.Sin(value)).ToArray();
            }
            case MaterialUniformExpressionUnaryOp unary when unary.ExpressionType.Instanced == "FMaterialUniformExpressionPeriodic":
            {
                double[] x = Of(unary.X, ref varies);
                return x?.Select(value => value - Math.Floor(value)).ToArray();
            }
            default:
                return null;
        }
    }

    private static double At(double[] values, int i) => values[Math.Min(i, values.Length - 1)];

    private static bool IsTime(MaterialUniformExpression e) =>
        e.ExpressionType.Instanced is "FMaterialUniformExpressionTime" or "FMaterialUniformExpressionRealTime";

    // A uniform that changes over time (Evaluate flagged it as varying) as Godot shading
    // language over TIME, one expression per component, following Evaluate case by case:
    // a value shorter than another applies its last component to the rest. Parts that don't
    // change are folded into constants. Null for expression kinds Evaluate doesn't know.
    public static string[] Glsl(MaterialUniformExpression expression, ParameterLookup parameters)
    {
        bool varies = false;
        double[] folded = Evaluate(expression, parameters, ref varies);
        if (folded == null)
            return null;
        if (!varies)
            return folded.Select(GlslFloat).ToArray();

        string[] Of(MaterialUniformExpression e) => Glsl(e, parameters);
        static string Get(string[] values, int i) => values[Math.Min(i, values.Length - 1)];
        switch (expression)
        {
            case MaterialUniformExpression time when IsTime(time):
                return ["TIME"];
            case MaterialUniformExpressionAppendVector append:
            {
                string[] head = Of(append.A), tail = Of(append.B);
                return head == null || tail == null ? null : [.. head.Take((int)append.NumComponentsA), .. tail];
            }
            case MaterialUniformExpressionFoldedMath math:
            {
                string[] a = Of(math.A), b = Of(math.B);
                if (a == null || b == null)
                    return null;
                if (math.Op == 4)
                    return [string.Join(" + ", Enumerable.Range(0, Math.Min(a.Length, b.Length)).Select(i => $"{a[i]} * {b[i]}"))];
                string op = math.Op switch { 0 => "+", 1 => "-", 2 => "*", _ => "/" };
                return Enumerable.Range(0, Math.Max(a.Length, b.Length)).Select(i => $"({Get(a, i)} {op} {Get(b, i)})").ToArray();
            }
            case MaterialUniformExpressionClamp clamp:
            {
                string[] x = Of(clamp.Input), lo = Of(clamp.Min), hi = Of(clamp.Max);
                return x == null || lo == null || hi == null ? null
                    : x.Select((value, i) => $"clamp({value}, {Get(lo, i)}, max({Get(lo, i)}, {Get(hi, i)}))").ToArray();
            }
            case MaterialUniformExpressionSine sine:
                return Of(sine.X)?.Select(value => $"{(sine.bIsCosine ? "cos" : "sin")}({value})").ToArray();
            case MaterialUniformExpressionUnaryOp unary when unary.ExpressionType.Instanced == "FMaterialUniformExpressionPeriodic":
                return Of(unary.X)?.Select(value => $"fract({value})").ToArray();
            default:
                return null;
        }
    }

    private static string GlslFloat(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    // ---------------------------------------------------------------
    // Disassembly
    // ---------------------------------------------------------------

    [DllImport("d3dcompiler_47.dll")]
    private static extern int D3DDisassemble(byte[] source, nint size, uint flags, string comments, out ID3DBlob disassembly);

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] nint GetBufferPointer();
        [PreserveSig] nint GetBufferSize();
    }

    // Unreal prefixes the DXBC container with its own header; find the container and hand it
    // to d3dcompiler.
    internal static string Disassemble(byte[] code)
    {
        int at = -1;
        for (int i = 0; i + 4 <= code.Length; i++)
        {
            if (code[i] == 'D' && code[i + 1] == 'X' && code[i + 2] == 'B' && code[i + 3] == 'C')
            {
                at = i;
                break;
            }
        }
        if (at < 0 || at + 28 > code.Length)
            return null;
        int size = BitConverter.ToInt32(code, at + 24);
        if (at + size > code.Length)
            return null;
        byte[] container = code[at..(at + size)];
        try
        {
            if (D3DDisassemble(container, container.Length, 0, null, out ID3DBlob blob) != 0)
                return null;
            return Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize());
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------
    // Symbolic evaluation
    // ---------------------------------------------------------------

    // A value as Constant + Σ coefficient × term. Terms are named: "uv.x"/"uv.y" (first UV
    // set), "tex…" (texture samples), and "op…" (anything the pass doesn't follow, such as a
    // cubemap, the scene color, or view-dependent math). Varies marks values that change over
    // time (panners).
    public sealed class Linear
    {
        public double Constant;
        public Dictionary<string, double> Terms = new();
        public bool Varies;

        public bool IsConstant => Terms.Count == 0;

        public static Linear Of(double constant, bool varies = false) => new() { Constant = constant, Varies = varies };

        public static Linear Term(string name) => new() { Terms = { [name] = 1.0 } };

        public Linear Plus(Linear other)
        {
            var sum = new Linear { Constant = Constant + other.Constant, Terms = new(Terms), Varies = Varies || other.Varies };
            foreach (var (name, k) in other.Terms)
                sum.Terms[name] = sum.Terms.GetValueOrDefault(name) + k;
            return sum;
        }

        public Linear Times(double k)
        {
            var scaled = new Linear { Constant = Constant * k, Varies = Varies };
            if (k != 0.0)
            {
                foreach (var (name, c) in Terms)
                    scaled.Terms[name] = c * k;
            }
            return scaled;
        }

        public override string ToString() =>
            string.Join(" + ", new[] { Constant != 0.0 || Terms.Count == 0 ? Constant.ToString("0.###", CultureInfo.InvariantCulture) : null }
                .Concat(Terms.Select(t => $"{t.Value.ToString("0.###", CultureInfo.InvariantCulture)}*{t.Key}"))
                .Where(s => s != null));
    }

    private sealed class Evaluator
    {
        private readonly string[] _lines;
        private readonly MaterialShaderMap _map;
        private readonly ParameterLookup _parameters;
        private readonly TextureMean _mean;
        private readonly Dictionary<string, Linear[]> _registers = new();
        private readonly Dictionary<string, Sample> _samples = new();
        // Shader resource register (t<n>) -> material texture slot (Texture2D_<slot>). Cubemaps,
        // the scene color, and the scene depth aren't listed.
        private readonly Dictionary<int, int> _textureSlots = new();
        // (cb0 register, component) -> (vector or scalar uniform, its index, its component).
        private readonly Dictionary<(int, int), (bool Vector, int Index, int Component)> _uniforms = new();
        private int _uvRegister = -1;
        private int _opaque;

        private static readonly Regex CbufferField = new(@"^//\s+float(4?) UniformPixel(Vector|Scalar)_(\d+);\s+// Offset:\s+(\d+)");
        private static readonly Regex Binding = new(@"^//\s+Texture2D_(\d+)\s+texture\s+\S+\s+\S+\s+t(\d+)");
        private static readonly Regex InputRow = new(@"^//\s+TEXCOORD\s+0\s+[xyzw]+\s+(\d+)\s");
        private static readonly Regex Operand = new(@"^(?<neg>-)?(?<abs>\|)?(?<reg>[a-z]+)(?<num>\d+)?(\[(?<idx>\d+)\])?(\.(?<swz>[xyzw]+))?\|?$");

        public Evaluator(string asm, MaterialShaderMap map, ParameterLookup parameters, TextureMean mean)
        {
            _lines = asm.Split('\n').Select(l => l.Trim()).ToArray();
            _map = map;
            _parameters = parameters;
            _mean = mean;
            foreach (string line in _lines)
            {
                Match m;
                if ((m = CbufferField.Match(line)).Success)
                {
                    // Vectors take a whole register; scalars are packed four to a register.
                    int offset = int.Parse(m.Groups[4].Value), index = int.Parse(m.Groups[3].Value);
                    if (m.Groups[2].Value == "Vector")
                    {
                        for (int c = 0; c < 4; c++)
                            _uniforms[(offset / 16, c)] = (true, index, c);
                    }
                    else
                        _uniforms[(offset / 16, offset % 16 / 4)] = (false, index, 0);
                }
                else if ((m = Binding.Match(line)).Success)
                    _textureSlots[int.Parse(m.Groups[2].Value)] = int.Parse(m.Groups[1].Value);
                else if ((m = InputRow.Match(line)).Success)
                    _uvRegister = int.Parse(m.Groups[1].Value);
            }
        }

        public Result Run(out string failure)
        {
            failure = null;
            foreach (string line in _lines)
            {
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("dcl_") || line.StartsWith("ps_"))
                    continue;
                if (line == "ret")
                    break;
                int space = line.IndexOf(' ');
                string opcode = space < 0 ? line : line[..space];
                if (opcode is "if_nz" or "if_z" or "else" or "endif" or "loop" or "endloop" or "break" or "breakc_nz" or "breakc_z" or "switch")
                {
                    failure = $"flow control ({opcode})";
                    return null;
                }
                if (space < 0)
                    continue;
                Execute(opcode, SplitOperands(line[(space + 1)..]));
            }

            if (!_registers.TryGetValue("o0", out Linear[] output) || output[0] == null)
            {
                failure = "no color output";
                return null;
            }
            return new Result(output[..3], output[3] ?? Linear.Of(1.0), _samples);
        }

        private void Execute(string opcode, List<string> operands)
        {
            bool saturate = opcode.EndsWith("_sat");
            string op = saturate ? opcode[..^4] : opcode;
            Match d = Operand.Match(operands[0]);
            if (!d.Success)
                return;
            string register = d.Groups["reg"].Value + d.Groups["num"].Value;
            string mask = d.Groups["swz"].Success ? d.Groups["swz"].Value : "xyzw";
            if (!_registers.TryGetValue(register, out Linear[] values))
                _registers[register] = values = new Linear[4];

            // Sources are read before any component is written (dest may also be a source).
            var results = new List<(int Component, Linear Value)>();
            string opaque = null;
            foreach (char c in mask)
            {
                int i = "xyzw".IndexOf(c);
                Linear value;
                if (op.StartsWith("sample"))
                    value = SampleTerm(op, operands, i);
                else
                {
                    switch (op)
                    {
                        case "mov":
                            value = Read(operands[1], i);
                            break;
                        case "add":
                            value = Read(operands[1], i).Plus(Read(operands[2], i));
                            break;
                        case "mul":
                            value = Product(Read(operands[1], i), Read(operands[2], i));
                            break;
                        case "mad":
                            // Fog: Unreal's translucent shaders end with color * fog.w + fog.rgb,
                            // both from one interpolator. Without fog that's the color itself.
                            if (register == "o0" && IsFog(operands[2], operands[3]))
                                value = Read(operands[1], i);
                            else
                                value = Product(Read(operands[1], i), Read(operands[2], i)).Plus(Read(operands[3], i));
                            break;
                        case "dp2":
                        case "dp3":
                        case "dp4":
                            // A dot product is linear when one side is constant (channel pickers,
                            // luminance weights).
                            int n = op[2] - '0';
                            value = Linear.Of(0.0);
                            for (int k = 0; k < n; k++)
                                value = value.Plus(Product(Read(operands[1], k), Read(operands[2], k)));
                            break;
                        case "max":
                        case "min":
                            Linear a = Read(operands[1], i), b = Read(operands[2], i);
                            value = a.IsConstant && b.IsConstant
                                ? Linear.Of(op == "max" ? Math.Max(a.Constant, b.Constant) : Math.Min(a.Constant, b.Constant), a.Varies || b.Varies)
                                : Opaque();
                            break;
                        default:
                            // rsq, div, exp, log, movc, ...: one opaque value for the whole write.
                            value = Linear.Term(opaque ??= "op" + _opaque++);
                            break;
                    }
                }
                if (saturate && value.IsConstant)
                    value = Linear.Of(Math.Clamp(value.Constant, 0.0, 1.0), value.Varies);
                results.Add((i, value));
            }
            foreach (var (i, value) in results)
                values[i] = value;
        }

        private Linear Opaque() => Linear.Term("op" + _opaque++);

        // A product stays linear when one side is constant. A product of two textured values
        // (a sign times its scanline overlay) keeps the simpler side and takes the other at
        // its average; anything else is opaque.
        private Linear Product(Linear a, Linear b)
        {
            if (a.IsConstant)
                return b.Times(a.Constant);
            if (b.IsConstant)
                return a.Times(b.Constant);
            (Linear keep, Linear average) = a.Terms.Count <= b.Terms.Count ? (a, b) : (b, a);
            double? mean = Mean(average);
            return mean.HasValue ? keep.Times(mean.Value) : Opaque();
        }

        // A value's average over the surface, when it's made only of texture samples.
        private double? Mean(Linear value)
        {
            double sum = value.Constant;
            foreach (var (name, k) in value.Terms)
            {
                if (!_samples.TryGetValue(name, out Sample sample) || sample.Slot >= (_map.Uniform2DTextureExpressions?.Length ?? 0))
                    return null;
                double? mean = _mean(_map.Uniform2DTextureExpressions[sample.Slot], sample.Channel);
                if (!mean.HasValue)
                    return null;
                sum += k * mean.Value;
            }
            return sum;
        }

        private static bool IsFog(string scale, string bias)
        {
            Match s = Operand.Match(scale), b = Operand.Match(bias);
            return s.Success && b.Success && s.Groups["reg"].Value == "v" && b.Groups["reg"].Value == "v"
                && s.Groups["num"].Value == b.Groups["num"].Value && s.Groups["swz"].Value.All(c => c == 'w');
        }

        // A texture sample's value for one destination component: a term naming the texture
        // slot and channel, described in _samples. Cubemaps and scene textures are opaque.
        private Linear SampleTerm(string op, List<string> operands, int component)
        {
            Match t = Operand.Match(operands[2]);
            int resource = t.Success && t.Groups["num"].Success ? int.Parse(t.Groups["num"].Value) : -1;
            if (!op.Contains("texture2d") || !_textureSlots.TryGetValue(resource, out int slot))
                return Opaque();
            string swizzle = t.Groups["swz"].Success ? t.Groups["swz"].Value : "xyzw";
            int channel = "xyzw".IndexOf(swizzle.Length == 1 ? swizzle[0] : swizzle[component]);

            Linear u = Read(operands[1], 0), v = Read(operands[1], 1);
            bool uv0 = u.Terms.Count == 1 && u.Terms.ContainsKey("uv.x") && v.Terms.Count == 1 && v.Terms.ContainsKey("uv.y");
            var sample = new Sample(slot, channel, uv0,
                uv0 ? (float)u.Terms["uv.x"] : 0f, uv0 ? (float)v.Terms["uv.y"] : 0f, uv0 && (u.Varies || v.Varies));
            string name = $"tex{slot}.{"rgba"[channel]}@" + (uv0 ? $"uv*{sample.ScaleU:0.###},{sample.ScaleV:0.###}" + (sample.Panning ? "+pan" : "") : "other");
            _samples[name] = sample;
            return Linear.Term(name);
        }

        // One component of a source operand. A register never written reads as opaque.
        private Linear Read(string operand, int component)
        {
            operand = operand.Trim();
            if (operand.StartsWith("l("))
            {
                string[] parts = operand[2..^1].Split(',');
                string part = parts.Length == 1 ? parts[0] : parts[component];
                return Linear.Of(double.Parse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            Match m = Operand.Match(operand);
            if (!m.Success)
                return Opaque();
            string swizzle = m.Groups["swz"].Success ? m.Groups["swz"].Value : "xyzw";
            int c = "xyzw".IndexOf(swizzle.Length == 1 ? swizzle[0] : swizzle[component]);
            string reg = m.Groups["reg"].Value;
            int number = m.Groups["num"].Success ? int.Parse(m.Groups["num"].Value) : 0;

            Linear value = reg switch
            {
                "r" or "o" => _registers.TryGetValue(reg + number, out Linear[] values) ? values[c] : null,
                "v" => number == _uvRegister && c < 2 ? Linear.Term(c == 0 ? "uv.x" : "uv.y") : null,
                "cb" when number == 0 && m.Groups["idx"].Success => Uniform(int.Parse(m.Groups["idx"].Value), c),
                _ => null,
            };
            value ??= Opaque();
            if (m.Groups["abs"].Success)
                value = value.IsConstant ? Linear.Of(Math.Abs(value.Constant), value.Varies) : Opaque();
            if (m.Groups["neg"].Success)
                value = value.Times(-1.0);
            return value;
        }

        // A cb0 component: a material uniform's value, or null for engine constants (fog,
        // depth parameters) and expressions that can't be evaluated.
        private Linear Uniform(int register, int component)
        {
            if (!_uniforms.TryGetValue((register, component), out var uniform))
                return null;
            MaterialUniformExpression[] expressions = uniform.Vector ? _map.UniformPixelVectorExpressions : _map.UniformPixelScalarExpressions;
            if (expressions == null || uniform.Index >= expressions.Length)
                return null;
            bool varies = false;
            double[] value = Evaluate(expressions[uniform.Index], _parameters, ref varies);
            return value != null ? Linear.Of(At(value, uniform.Component), varies) : null;
        }

        // Splits "r0.xyz, l(1, 2, 3, 4), t0.xyzw" at top-level commas.
        private static List<string> SplitOperands(string text)
        {
            var operands = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                    depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    operands.Add(text[start..i].Trim());
                    start = i + 1;
                }
            }
            operands.Add(text[start..].Trim());
            return operands;
        }
    }
}
