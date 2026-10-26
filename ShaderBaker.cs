using System.Globalization;
using System.Linq.Expressions;
using System.Numerics;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LexExport;

// Bakes a character material's look straight from its compiled base-pass pixel shader: the
// shader is run on the CPU once per texel, over the material's own textures and parameters,
// so tint masks, skin tones, face paint, and detail layers come out exactly as Mass Effect
// combines them, with no per-material recipe. It reads two outputs:
//   o0: the color. The no-lightmap base pass adds diffuse × AmbientColorAndSkyFactor to any
//       emissive, so running it with that ambient at 1 and at 0 splits the two.
//   o2: the view-space normal's XY. With an identity tangent frame and view matrix that is
//       the shader's final tangent-space normal (detail normals and all), in Unreal's
//       DirectX convention like the normal textures themselves.
// A texel the shader discards is cut out (alpha 0).
//
// The specular comes from the base pass Unreal 3 drew a light environment's dominant light with
// (FDirectionalLightLightMapPolicy): Diffuse · N·L + SpecularColor · pow(R·L, SpecularPower),
// times the light's color, where R is the camera vector reflected about the normal. With the
// light along the texel's own normal (N·L = 1) and the camera at three angles to it, R·L takes
// the camera's cosine, so three runs solve for the color and the power, whatever math or
// textures the material computes them from. The specular map holds the color in RGB (sRGB,
// divided by SpecularScale when it passes 1) and the power in alpha as log2(power) / 8.
public static class ShaderBaker
{
    public record Result(MemoryImage Albedo, MemoryImage? Normal, MemoryImage? Emissive, MemoryImage? Specular,
        float SpecularScale, bool Cutout, string Note);

    // The no-lightmap base pass for skinned meshes; any no-lightmap variant will do.
    private const string VertexFactory = "FGPUSkinVertexFactory";
    private const string ShaderType = "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight";
    // The dominant light's base pass, which holds the material's specular.
    private const string LightShaderType = "TBasePassPixelShaderFDirectionalLightLightMapPolicyNoSkyLight";
    // The camera's cosine to the normal in the run that measures the power: close to 1, so
    // powers up to 256 still leave a readable highlight (0.99^256 is about 0.08).
    private const float PowerCosine = 0.99f;
    private const float MaxPower = 256f;
    // Below this, a material has no highlight worth a map.
    private const float MinSpecular = 0.004f;
    private const int MaxSpecularSize = 1024;

    public static Result Bake(IMEPackage pcc, ExportEntry material, Dictionary<string, ExportEntry> textures,
        Dictionary<string, Vector3> colors, Dictionary<string, float> scalars, int maxSize, out string failure)
    {
        failure = null;
        ExportEntry baseMaterial = BaseMaterial(pcc, material);
        if (baseMaterial == null)
        {
            failure = "no base material";
            return null;
        }
        Guid id = ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource.ID;
        MaterialShaderMap map = null;
        ShaderCache cache = null;
        foreach (ShaderCache candidate in Caches(pcc, baseMaterial))
        {
            map = candidate.MaterialShaderMaps.Values.FirstOrDefault(m => m.StaticParameters.BaseMaterialId == id
                && m.MeshShaderMaps.Any(s => s.VertexFactoryType.Instanced == VertexFactory));
            if (map != null)
            {
                cache = candidate;
                break;
            }
        }
        if (map == null)
        {
            failure = $"no skinned shader for {baseMaterial.ObjectName.Instanced}";
            return null;
        }
        var shaders = map.MeshShaderMaps.Where(s => s.VertexFactoryType.Instanced == VertexFactory).SelectMany(s => s.Shaders.Select(kv => kv)).ToList();  // through Select: LEX's UMap.CopyTo is broken
        var reference = shaders.FirstOrDefault(s => s.Key.Instanced == ShaderType).Value
            ?? shaders.FirstOrDefault(s => s.Key.Instanced.StartsWith("TBasePassPixelShaderFNoLightMapPolicy")).Value;
        if (reference == null || !cache.Shaders.TryGetValue(reference.Id, out Shader shader))
        {
            failure = "no no-lightmap base pass";
            return null;
        }
        string asm = ShaderAnalysis.Disassemble(shader.ShaderByteCode);
        if (asm == null)
        {
            failure = "no disassembly";
            return null;
        }

        Program program;
        try
        {
            program = new Program(asm);
        }
        catch (NotSupportedException ex)
        {
            failure = ex.Message;
            return null;
        }

        // The specular's shader, when the material was cooked for light environments.
        var lightReference = shaders.FirstOrDefault(s => s.Key.Instanced == LightShaderType).Value;
        Program lightProgram = null;
        string specularNote = "";
        if (lightReference == null || !cache.Shaders.TryGetValue(lightReference.Id, out Shader lightShader))
            specularNote = ", no specular shader";
        else if (ShaderAnalysis.Disassemble(lightShader.ShaderByteCode) is not string lightAsm)
            specularNote = ", specular not disassembled";
        else
        {
            try
            {
                lightProgram = new Program(lightAsm);
            }
            catch (NotSupportedException ex)
            {
                specularNote = $", specular not baked ({ex.Message})";
            }
        }

        // Textures by shader slot: an instance parameter, else the base material's default.
        int[] defaults = ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource.UniformExpressionTextures;
        var slotTextures = new Dictionary<int, ExportEntry>();
        // The specular shader may sample maps the base pass doesn't (a specular power map).
        foreach (int slot in program.TextureSlots.Union(lightProgram?.TextureSlots ?? []))
        {
            MaterialUniformExpressionTexture expression = slot < (map.Uniform2DTextureExpressions?.Length ?? 0) ? map.Uniform2DTextureExpressions[slot] : null;
            ExportEntry texture = null;
            if (expression is MaterialUniformExpressionTextureParameter parameter)
                textures.TryGetValue(parameter.ParameterName.Instanced, out texture);
            if (texture == null && expression != null && expression.TextureIndex < defaults.Length)
                texture = Resolve(baseMaterial.FileRef, defaults[expression.TextureIndex]);
            slotTextures[slot] = texture?.ClassName == "Texture2D" ? texture : null;
        }

        double[] Parameter(string name) =>
            colors.TryGetValue(name, out Vector3 c) ? [c.X, c.Y, c.Z, 1]
            : scalars.TryGetValue(name, out float f) ? [f]
            : null;
        double[] Uniform(MaterialUniformExpression[] expressions, int slot)
        {
            MaterialUniformExpression expression = slot < (expressions?.Length ?? 0) ? expressions[slot] : null;
            bool varies = false;
            return (expression != null ? ShaderAnalysis.Evaluate(expression, Parameter, ref varies) : null) ?? [0, 0, 0, 0];
        }
        float[] constants = program.Constants(
            slot => Uniform(map.UniformPixelVectorExpressions, slot),
            slot => Uniform(map.UniformPixelScalarExpressions, slot));
        float[] lightConstants = lightProgram?.Constants(
            slot => Uniform(map.UniformPixelVectorExpressions, slot),
            slot => Uniform(map.UniformPixelScalarExpressions, slot));
        // Lit by the light alone: no ambient.
        if (lightProgram != null && lightProgram.AmbientRegister >= 0)
            Array.Clear(lightConstants, lightProgram.AmbientRegister * 4, 4);

        // The same shader over the same textures and constants bakes the same images, so models
        // sharing a material (eyes, scalps, outfits) bake it once per run.
        string key = string.Join("|", reference.Id.ToString(), lightProgram != null ? lightReference.Id.ToString() : "-",
            maxSize.ToString(CultureInfo.InvariantCulture),
            string.Join(",", constants.Select(c => BitConverter.SingleToUInt32Bits(c).ToString("x8"))),
            string.Join(",", slotTextures.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value?.InstancedFullPath}")));
        lock (Baked)
        {
            if (Baked.TryGetValue(key, out Result reused))
                return reused with { Note = reused.Note + ", reused" };
        }

        // Each texture decodes on its own thread; they're independent.
        var slots = new Dictionary<int, Texels>();
        foreach (var (slot, texture) in slotTextures)
            slots[slot] = null;
        Parallel.ForEach(slotTextures.Where(kv => kv.Value != null).ToArray(), ParallelLimits.Options, kv =>
        {
            Texels texels = Texels.Load(kv.Value);
            lock (slots)
                slots[kv.Key] = texels;
        });

        // Bake at the size of the largest texture it samples, within the cap.
        Texels[] loaded = slots.Values.Where(t => t != null).ToArray();
        if (loaded.Length == 0)
        {
            failure = "no textures";
            return null;
        }
        int width = loaded.Max(t => t.Width), height = loaded.Max(t => t.Height);
        while (width > maxSize || height > maxSize)
        {
            width /= 2;
            height /= 2;
        }

        var albedo = new Rgba32[width * height];
        var normal = new Rgba32[width * height];
        var emissive = new Rgba32[width * height];
        bool cutout = false, hasNormal = program.Writes(2), hasEmissive = false;
        var compiled = new CompiledShader(program, constants, slots);
        var lightCompiled = lightProgram != null ? new CompiledShader(lightProgram, lightConstants, slots, lit: true) : null;
        Parallel.For(0, height, ParallelLimits.Options, y =>
        {
            var machine = new Machine(compiled, constants);
            for (int x = 0; x < width; x++)
            {
                var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                bool discarded = !machine.Run(uv, out Vector4 emit, out Vector4 lit, out Vector4 viewNormal);
                Vector3 diffuse = lit.AsVector3() - emit.AsVector3();
                int i = y * width + x;
                albedo[i] = new Rgba32(MaterialBaker.Srgb(diffuse.X), MaterialBaker.Srgb(diffuse.Y), MaterialBaker.Srgb(diffuse.Z), discarded ? 0f : 1f);
                if (discarded)
                    cutout = true;
                Vector3 glow = Vector3.Max(emit.AsVector3(), Vector3.Zero);
                if (glow.X + glow.Y + glow.Z > 0.01f)
                    hasEmissive = true;
                emissive[i] = new Rgba32(MaterialBaker.Srgb(glow.X), MaterialBaker.Srgb(glow.Y), MaterialBaker.Srgb(glow.Z), 1f);
                if (hasNormal)
                {
                    // o2.xy is the normal packed to 0..1; rebuild Z and flip green from
                    // DirectX's convention to glTF's.
                    float nx = viewNormal.X * 2f - 1f, ny = viewNormal.Y * 2f - 1f;
                    float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
                    normal[i] = new Rgba32(nx * 0.5f + 0.5f, 0.5f - ny * 0.5f, nz * 0.5f + 0.5f, 1f);
                }
            }
        });

        // The specular, in a pass of its own at up to MaxSpecularSize: it costs three shader runs
        // a texel (MeasureSpecular), and highlights don't need the detail. Each texel takes the
        // normal (and cutout) of the full-size texel at its middle.
        int specularWidth = width, specularHeight = height;
        while (specularWidth > MaxSpecularSize || specularHeight > MaxSpecularSize)
        {
            specularWidth /= 2;
            specularHeight /= 2;
        }
        var specularColor = lightCompiled != null ? new Vector3[specularWidth * specularHeight] : null;
        var specularPower = lightCompiled != null ? new float[specularWidth * specularHeight] : null;
        if (lightCompiled != null)
        {
            Parallel.For(0, specularHeight, ParallelLimits.Options, y =>
            {
                var lightMachine = new Machine(lightCompiled, lightConstants);
                int step = width / specularWidth;
                for (int x = 0; x < specularWidth; x++)
                {
                    int fx = x * step + step / 2, fy = y * step + step / 2;
                    var uv = new Vector2((fx + 0.5f) / width, (fy + 0.5f) / height);
                    int i = y * specularWidth + x;
                    int full = fy * width + fx;
                    specularPower[i] = 1f;
                    if (albedo[full].A == 0)
                        continue;
                    // The normal in the shader's own (DirectX) tangent space: green flipped back.
                    Vector3 n = Vector3.UnitZ;
                    if (hasNormal)
                    {
                        Vector4 packed = normal[full].ToVector4();
                        n = Vector3.Normalize(new Vector3(packed.X * 2f - 1f, 1f - packed.Y * 2f, packed.Z * 2f - 1f));
                    }
                    (specularColor[i], specularPower[i]) = MeasureSpecular(lightMachine, uv, n);
                }
            });
        }

        // The map, scaled to fit 8 bits when the color passes 1.
        MemoryImage? specularImage = null;
        float specularScale = 1f;
        if (specularColor != null)
        {
            float peak = 0f;
            double powerSum = 0, weightSum = 0;
            for (int i = 0; i < specularColor.Length; i++)
            {
                float strength = MathF.Max(specularColor[i].X, MathF.Max(specularColor[i].Y, specularColor[i].Z));
                peak = MathF.Max(peak, strength);
                powerSum += specularPower[i] * strength;
                weightSum += strength;
            }
            if (peak < MinSpecular)
                specularNote = ", no specular";
            else
            {
                specularScale = MathF.Max(1f, peak);
                var specular = new Rgba32[specularWidth * specularHeight];
                for (int i = 0; i < specular.Length; i++)
                {
                    Vector3 c = specularColor[i] / specularScale;
                    specular[i] = new Rgba32(MaterialBaker.Srgb(c.X), MaterialBaker.Srgb(c.Y), MaterialBaker.Srgb(c.Z),
                        MathF.Log2(MathF.Max(specularPower[i], 1f)) / 8f);
                }
                specularImage = DdsTextures.Encode(specular, specularWidth, specularHeight, normalMap: false);
                specularNote = $", {specularWidth}x{specularHeight} specular (peak {peak:0.00}, power {powerSum / weightSum:0})";
            }
        }

        string note = $"{width}x{height} from {baseMaterial.ObjectName.Instanced}" + (cutout ? ", cutout" : "") + (hasEmissive ? ", emissive" : "")
            + specularNote;
        // Each encode is single-threaded, so the three run side by side.
        MemoryImage albedoImage = default;
        MemoryImage? normalImage = null, emissiveImage = null;
        Parallel.Invoke(ParallelLimits.Options,
            () => albedoImage = DdsTextures.Encode(albedo, width, height, normalMap: false),
            () => normalImage = hasNormal ? DdsTextures.Encode(normal, width, height, normalMap: true) : (MemoryImage?)null,
            () => emissiveImage = hasEmissive ? DdsTextures.Encode(emissive, width, height, normalMap: false) : (MemoryImage?)null);
        var result = new Result(albedoImage, normalImage, emissiveImage, specularImage, specularScale, cutout, note);
        Remember(key, result);
        return result;
    }

    // A texel's specular color and power, from the dominant light's shader lit along the normal
    // n: with the camera along n (R·L = 1), across it (R·L clamps to the shader's 0.0001), and
    // at PowerCosine. The first two differ by the color, the last two by the power.
    private static (Vector3 Color, float Power) MeasureSpecular(Machine machine, Vector2 uv, Vector3 n)
    {
        Vector3 across = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        Vector3 tilted = n * PowerCosine + across * MathF.Sqrt(1f - PowerCosine * PowerCosine);
        machine.RunLit(uv, n, n, out Vector4 full);
        machine.RunLit(uv, n, across, out Vector4 none);
        machine.RunLit(uv, n, tilted, out Vector4 partial);

        Vector3 color = Vector3.Max(full.AsVector3() - none.AsVector3(), Vector3.Zero);
        float strength = color.X + color.Y + color.Z;
        float measured = Vector3.Dot(partial.AsVector3() - none.AsVector3(), Vector3.One);
        // pow(PowerCosine, power) = measured / strength.
        float power = strength > 1e-5f && measured > 0f
            ? MathF.Log(MathF.Min(measured / strength, 1f)) / MathF.Log(PowerCosine)
            : 1f;
        return (color, Math.Clamp(float.IsFinite(power) ? power : 1f, 1f, MaxPower));
    }

    // Finished bakes by everything that decides their pixels, oldest dropped first past the
    // budget: most bakes are unique to one NPC, so only a little is worth keeping.
    private const long BakedBudget = 256L * 1024 * 1024;
    private static readonly Dictionary<string, Result> Baked = new();
    private static readonly Queue<(string Key, long Bytes)> BakedOrder = new();
    private static long _bakedBytes;

    private static void Remember(string key, Result result)
    {
        long bytes = result.Albedo.Content.Length + (result.Normal?.Content.Length ?? 0) + (result.Emissive?.Content.Length ?? 0)
            + (result.Specular?.Content.Length ?? 0);
        lock (Baked)
        {
            if (!Baked.TryAdd(key, result))
                return;
            BakedOrder.Enqueue((key, bytes));
            _bakedBytes += bytes;
            while (_bakedBytes > BakedBudget && BakedOrder.Count > 1)
            {
                var (oldest, size) = BakedOrder.Dequeue();
                Baked.Remove(oldest);
                _bakedBytes -= size;
            }
        }
    }

    // The Material a material instance renders with. ME1 characters' instances hang off an
    // RvrEffectsMaterialUser (which layers combat effects), whose base material is imported
    // from BIOC_Materials.
    private static ExportEntry BaseMaterial(IMEPackage pcc, ExportEntry material)
    {
        for (ExportEntry current = material; current != null;)
        {
            if (current.ClassName == "Material")
                return current;
            var props = current.GetProperties();
            int next = current.ClassName == "RvrEffectsMaterialUser"
                ? props.GetProp<ObjectProperty>("m_pBaseMaterial")?.Value ?? 0
                : props.GetProp<ObjectProperty>("Parent")?.Value ?? 0;
            current = Resolve(current.FileRef, next);
        }
        return null;
    }

    // An export of the package, or the export an import names in the global material package
    // (or the import's own package file, when the game has one).
    private static ExportEntry Resolve(IMEPackage pcc, int uIndex)
    {
        if (pcc.TryGetUExport(uIndex, out ExportEntry export))
            return export;
        if (pcc.GetEntry(uIndex) is not ImportEntry import || pcc.FilePath == null)
            return null;
        string folder = Path.GetDirectoryName(pcc.FilePath)!;
        string own = import.InstancedFullPath.Split('.')[0] + ".pcc";
        foreach (string file in new[] { own, "BIOC_Materials.pcc" })
        {
            if (EntryScope.OpenShared(Path.Combine(folder, file))?.FindExport(import.InstancedFullPath) is ExportEntry found)
                return found;
        }
        return null;
    }

    // Where a material's compiled shaders may live: the character's package, the base
    // material's package, and the game's RefShaderCache.
    private static IEnumerable<ShaderCache> Caches(IMEPackage pcc, ExportEntry baseMaterial)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IMEPackage package in new[] { pcc, baseMaterial.FileRef })
        {
            if (package?.FilePath == null || !seen.Add(package.FilePath))
                continue;
            if (ShaderAnalysis.CacheOf(package) is ShaderCache cache)
                yield return cache;
        }
        string refCache = Path.Combine(Path.GetDirectoryName(pcc.FilePath)!, ShaderAnalysis.RefShaderCache);
        if (ShaderAnalysis.CacheOfFile(refCache, VertexFactory) is ShaderCache shared)
            yield return shared;
    }

    // A texture decoded to linear floats (sRGB textures decoded as the GPU would), sampled
    // bilinearly with wrapping.
    private sealed class Texels
    {
        public int Width, Height;
        private Vector4[] _data;

        // Lazy, so a texture shared by two materials decoding at once is still decoded only once,
        // without one lock holding up every other texture's decode.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Texels>> Cache = new();

        // Decoded textures are 16 bytes a texel (64 MB at 2048²), so the cache keeps only the
        // most recently used ones: enough for models that share an outfit or species textures,
        // without a full manifest run growing past what a player's machine can hold.
        private const long CacheBudget = 1L * 1024 * 1024 * 1024;
        private static readonly object TrimLock = new();
        private static long _clock;
        private long _lastUsed;
        private long Bytes => _data.LongLength * 16;

        public static Texels Load(ExportEntry texture)
        {
            Texels texels;
            try
            {
                texels = Cache.GetOrAdd(texture.InstancedFullPath, _ => new Lazy<Texels>(() => Decode(texture))).Value;
            }
            catch
            {
                // A Lazy keeps its exception; let a later bake try this texture again.
                Cache.TryRemove(texture.InstancedFullPath, out _);
                throw;
            }
            if (texels != null)
            {
                Interlocked.Exchange(ref texels._lastUsed, Interlocked.Increment(ref _clock));
                Trim();
            }
            return texels;
        }

        // Drops the least recently used textures past the budget. A bake still holding one keeps
        // its own reference.
        private static void Trim()
        {
            lock (TrimLock)
            {
                var loaded = Cache.Where(kv => kv.Value.IsValueCreated && kv.Value.Value != null)
                    .Select(kv => (kv.Key, Texels: kv.Value.Value)).ToList();
                long total = loaded.Sum(t => t.Texels.Bytes);
                foreach (var (key, texels) in loaded.OrderBy(t => Interlocked.Read(ref t.Texels._lastUsed)))
                {
                    if (total <= CacheBudget)
                        break;
                    Cache.TryRemove(key, out _);
                    total -= texels.Bytes;
                }
            }
        }

        private static Texels Decode(ExportEntry texture)
        {
            MaterialBaker.Pixels pixels = MaterialBaker.Pixels.Load(texture);
            if (pixels == null)
                return null;
            // Converted in place: the decoded pixels belong to this texture alone.
            var texels = new Texels { Width = pixels.Width, Height = pixels.Height, _data = pixels.Data };
            texels._wrapX = (pixels.Width & (pixels.Width - 1)) == 0;
            texels._wrapY = (pixels.Height & (pixels.Height - 1)) == 0;
            if (MaterialBaker.IsSrgb(texture))
            {
                Vector4[] data = texels._data;
                Parallel.For(0, pixels.Height, ParallelLimits.Options, y =>
                {
                    for (int i = y * pixels.Width, end = i + pixels.Width; i < end; i++)
                        data[i] = new Vector4(MaterialBaker.Linear(data[i].AsVector3()), data[i].W);
                });
            }
            return texels;
        }

        // Power-of-two sides wrap with a mask (the same result as the modulo, much cheaper).
        private bool _wrapX, _wrapY;

        public Vector4 Sample(float u, float v)
        {
            float fx = u * Width - 0.5f, fy = v * Height - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int xa = WrapX(x0), xb = WrapX(x0 + 1);
            int ya = WrapY(y0) * Width, yb = WrapY(y0 + 1) * Width;
            return Vector4.Lerp(Vector4.Lerp(_data[ya + xa], _data[ya + xb], tx), Vector4.Lerp(_data[yb + xa], _data[yb + xb], tx), ty);
        }

        private int WrapX(int i) => _wrapX ? i & (Width - 1) : Wrap(i, Width);
        private int WrapY(int i) => _wrapY ? i & (Height - 1) : Wrap(i, Height);
        private static int Wrap(int i, int n) => ((i % n) + n) % n;
    }

    // ---------------------------------------------------------------
    // The shader, parsed once
    // ---------------------------------------------------------------

    private enum Kind { Temp, Input, Output, Constant, Literal, Null }

    private sealed class Operand
    {
        public Kind Kind;
        public int Index;
        public int[] Swizzle = [0, 1, 2, 3];
        public bool Negate, Absolute;
        public Vector4 Value;
        public bool[] Mask = [true, true, true, true];
        public bool Identity => Swizzle[0] == 0 && Swizzle[1] == 1 && Swizzle[2] == 2 && Swizzle[3] == 3;
        public bool FullMask => Mask[0] && Mask[1] && Mask[2] && Mask[3];
    }

    // Opcodes the machine runs; anything else is Unsupported and throws when reached, as before.
    private enum OpCode
    {
        Unsupported, DiscardNz, DiscardZ, Sample, Mov, Add, Mul, Mad, Div, Min, Max, Sqrt, Rsq, Log, Exp, Frc,
        RoundNe, RoundNi, RoundPi, RoundZ, Dp2, Dp3, Dp4, Lt, Ge, Eq, Ne, Movc, And, Or, Sincos, Deriv,
    }

    private static readonly Dictionary<string, OpCode> Codes = new()
    {
        ["discard_nz"] = OpCode.DiscardNz, ["discard_z"] = OpCode.DiscardZ,
        ["sample"] = OpCode.Sample, ["sample_indexable"] = OpCode.Sample, ["sample_l"] = OpCode.Sample,
        ["sample_b"] = OpCode.Sample, ["sample_d"] = OpCode.Sample,
        ["mov"] = OpCode.Mov, ["add"] = OpCode.Add, ["mul"] = OpCode.Mul, ["mad"] = OpCode.Mad, ["div"] = OpCode.Div,
        ["min"] = OpCode.Min, ["max"] = OpCode.Max, ["sqrt"] = OpCode.Sqrt, ["rsq"] = OpCode.Rsq, ["log"] = OpCode.Log,
        ["exp"] = OpCode.Exp, ["frc"] = OpCode.Frc, ["round_ne"] = OpCode.RoundNe, ["round_ni"] = OpCode.RoundNi,
        ["round_pi"] = OpCode.RoundPi, ["round_z"] = OpCode.RoundZ, ["dp2"] = OpCode.Dp2, ["dp3"] = OpCode.Dp3,
        ["dp4"] = OpCode.Dp4, ["lt"] = OpCode.Lt, ["ge"] = OpCode.Ge, ["eq"] = OpCode.Eq, ["ne"] = OpCode.Ne,
        ["movc"] = OpCode.Movc, ["and"] = OpCode.And, ["or"] = OpCode.Or, ["sincos"] = OpCode.Sincos,
    };

    private sealed class Instruction
    {
        public string Op;
        public OpCode Code;
        public bool Saturate;
        public Operand[] Operands;
        public int Resource = -1;
    }

    private sealed class Program
    {
        private static readonly Regex SignatureRow = new(@"^//\s+(\w+)\s+(\d+)\s+[xyzw]+\s+(\d+)\s+\w+\s+\w+");
        private static readonly Regex CbufferField = new(@"^//\s+\w+\s+(\w+)(\[\d+\])?;\s*// Offset:\s+(\d+)\s+Size:\s+(\d+)");
        private static readonly Regex Binding = new(@"^//\s+(\S+)\s+texture\s+\S+\s+(\S+)\s+t(\d+)\s");
        private static readonly Regex OperandPattern = new(@"^(?<neg>-)?(?<abs>\|)?(?<reg>[a-z]+)(?<num>\d+)?(\[(?<idx>\d+)\])?(\.(?<swz>[xyzw]+))?\|?$");

        public readonly List<Instruction> Code = new();
        public readonly int Temps;
        // Input register -> what it carries (semantic name + index).
        public readonly Dictionary<int, string> Inputs = new();
        // Resource register -> texture slot (-1: a cubemap or something else, sampled as black).
        public readonly Dictionary<int, int> Resources = new();
        public IEnumerable<int> TextureSlots => Resources.Values.Where(s => s >= 0).Distinct();
        private readonly List<(string Name, int Offset, int Size)> _fields = new();
        private readonly HashSet<int> _outputs = new();

        public bool Writes(int output) => _outputs.Contains(output);

        public Program(string asm)
        {
            string section = "";
            bool started = false;
            foreach (string raw in asm.Split('\n'))
            {
                string line = raw.Trim();
                if (!started)
                {
                    if (line.StartsWith("// Input signature"))
                        section = "input";
                    else if (line.StartsWith("// Output signature"))
                        section = "output";
                    else if (line.StartsWith("// Buffer Definitions"))
                        section = "cbuffer";
                    else if (line.StartsWith("// Resource Bindings"))
                        section = "bindings";
                    Match m;
                    if (section == "input" && (m = SignatureRow.Match(line)).Success)
                        Inputs[int.Parse(m.Groups[3].Value)] = m.Groups[1].Value + m.Groups[2].Value;
                    else if (section == "cbuffer" && (m = CbufferField.Match(line)).Success)
                        _fields.Add((m.Groups[1].Value, int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
                    else if (section == "bindings" && (m = Binding.Match(line)).Success)
                    {
                        Match slot = Regex.Match(m.Groups[1].Value, @"^Texture2D_(\d+)$");
                        Resources[int.Parse(m.Groups[3].Value)] = slot.Success ? int.Parse(slot.Groups[1].Value) : -1;
                    }
                    started = line.StartsWith("ps_");
                    continue;
                }
                if (line.Length == 0 || line.StartsWith("//"))
                    continue;
                if (line == "ret")
                    break;
                if (line.StartsWith("dcl_temps "))
                {
                    Temps = int.Parse(line["dcl_temps ".Length..]);
                    continue;
                }
                if (line.StartsWith("dcl_"))
                    continue;
                Code.Add(Parse(line));
            }
        }

        private Instruction Parse(string line)
        {
            int space = line.IndexOf(' ');
            string opcode = space < 0 ? line : line[..space];
            bool saturate = opcode.EndsWith("_sat");
            string op = saturate ? opcode[..^4] : opcode;
            // "sample_indexable(texture2d)(float,float,float,float)" -> "sample_indexable"
            op = op.Split('(')[0];
            List<string> texts = space < 0 ? new() : SplitOperands(line[(space + 1)..]);
            // Screen-space derivatives are zero over a flat texel grid.
            OpCode code = Codes.TryGetValue(op, out OpCode known) ? known : op.StartsWith("deriv_") ? OpCode.Deriv : OpCode.Unsupported;
            var instruction = new Instruction { Op = op, Code = code, Saturate = saturate, Operands = new Operand[texts.Count] };
            for (int i = 0; i < texts.Count; i++)
            {
                Match t = OperandPattern.Match(texts[i]);
                if (t.Success && t.Groups["reg"].Value == "t")
                {
                    // A texture resource: its swizzle picks the sampled channels.
                    instruction.Resource = int.Parse(t.Groups["num"].Value);
                    instruction.Operands[i] = new Operand { Kind = Kind.Null, Swizzle = Swizzle(t.Groups["swz"].Success ? t.Groups["swz"].Value : "xyzw") };
                    continue;
                }
                instruction.Operands[i] = ParseOperand(texts[i]);
            }
            return instruction;
        }

        private Operand ParseOperand(string text)
        {
            if (text == "null")
                return new Operand { Kind = Kind.Null };
            if (text.StartsWith("l("))
            {
                float[] values = text[2..^1].Split(',').Select(p => LiteralComponent(p.Trim())).ToArray();
                Vector4 v = values.Length == 1 ? new Vector4(values[0]) : new Vector4(values[0], values[1], values[2], values[3]);
                return new Operand { Kind = Kind.Literal, Value = v };
            }
            Match m = OperandPattern.Match(text);
            if (!m.Success)
                throw new NotSupportedException($"operand {text}");
            string reg = m.Groups["reg"].Value;
            string swizzle = m.Groups["swz"].Success ? m.Groups["swz"].Value : "xyzw";
            var operand = new Operand
            {
                Negate = m.Groups["neg"].Success,
                Absolute = m.Groups["abs"].Success,
                Swizzle = Swizzle(swizzle),
                Mask = [swizzle.Contains('x'), swizzle.Contains('y'), swizzle.Contains('z'), swizzle.Contains('w')],
            };
            int number = m.Groups["num"].Success ? int.Parse(m.Groups["num"].Value) : 0;
            switch (reg)
            {
                case "r":
                    operand.Kind = Kind.Temp;
                    operand.Index = number;
                    break;
                case "o":
                    operand.Kind = Kind.Output;
                    operand.Index = number;
                    _outputs.Add(number);
                    break;
                case "v":
                    operand.Kind = Kind.Input;
                    operand.Index = number;
                    break;
                // Samplers only carry filtering state.
                case "s":
                    operand.Kind = Kind.Null;
                    break;
                case "cb" when number == 0 && m.Groups["idx"].Success:
                    operand.Kind = Kind.Constant;
                    operand.Index = int.Parse(m.Groups["idx"].Value);
                    break;
                default:
                    throw new NotSupportedException($"operand {text}");
            }
            return operand;
        }

        // The constant buffer, a float4 per register: the material's uniforms, an identity
        // view matrix (so o2 is the tangent-space normal), and zero for the rest. Ambient is
        // set per run by the machine.
        public int AmbientRegister = -1;

        public float[] Constants(Func<int, double[]> vector, Func<int, double[]> scalar)
        {
            int size = _fields.Count == 0 ? 0 : _fields.Max(f => f.Offset + f.Size);
            var cb = new float[(size + 15) / 16 * 4 + 4];
            foreach (var (name, offset, fieldSize) in _fields)
            {
                Match slot;
                if ((slot = Regex.Match(name, @"^UniformPixelVector_(\d+)$")).Success)
                {
                    double[] value = vector(int.Parse(slot.Groups[1].Value));
                    for (int c = 0; c < 4; c++)
                        cb[offset / 4 + c] = (float)value[Math.Min(c, value.Length - 1)];
                }
                else if ((slot = Regex.Match(name, @"^UniformPixelScalar_(\d+)$")).Success)
                    cb[offset / 4] = (float)scalar(int.Parse(slot.Groups[1].Value))[0];
                // ME2 and ME3 pack four scalars a register (4k to 4k + 3).
                else if ((slot = Regex.Match(name, @"^UniformPixelScalars_(\d+)$")).Success)
                {
                    for (int c = 0; c < fieldSize / 4; c++)
                        cb[offset / 4 + c] = (float)scalar(int.Parse(slot.Groups[1].Value) * 4 + c)[0];
                }
                else if (name == "WorldToViewMatrix")
                {
                    for (int row = 0; row < 3; row++)
                        cb[offset / 4 + row * 4 + row] = 1f;
                }
                else if (name == "AmbientColorAndSkyFactor")
                    AmbientRegister = offset / 16;
                else if (name == "LightColorAndFalloffExponent")
                {
                    // A white light, for the specular bake.
                    for (int c = 0; c < 4; c++)
                        cb[offset / 4 + c] = 1f;
                }
            }
            return cb;
        }

        private static int[] Swizzle(string s)
        {
            string full = s.Length == 1 ? new string(s[0], 4) : s.PadRight(4, s[^1]);
            return full.Select(c => "xyzw".IndexOf(c)).ToArray();
        }

        private static float LiteralComponent(string text)
        {
            if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
                return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            uint bits = text.StartsWith("0x")
                ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : unchecked((uint)int.Parse(text, CultureInfo.InvariantCulture));
            return BitConverter.UInt32BitsToSingle(bits);
        }

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

    // ---------------------------------------------------------------
    // Running it: compiled once per bake, run by one machine per thread
    // ---------------------------------------------------------------

    // A stretch of the shader: reads the registers from r and o, runs, and writes them back
    // (also when it discards, returning false).
    private delegate bool Segment(Vector4[] v, Vector4[] cb, Vector4[] r, Vector4[] o);

    // The shader turned into .NET code. These shaders never branch, so each instruction becomes
    // straight-line math over locals, and the JIT compiles the whole thing to native code instead
    // of the bake interpreting every instruction at every texel. The math is the same float
    // operations the interpreter did, so the bakes are bit-identical.
    private sealed class CompiledShader
    {
        public readonly int AmbientRegister, Temps, Inputs, ConstantCount, Length;
        // Everything before the first read of AmbientColorAndSkyFactor is the same at either
        // ambient, so it runs once per texel and only the rest runs twice.
        public readonly int Split;
        public readonly Segment Prefix, Suffix;
        // What the vertex shader would pass (TEXCOORD0, the UV, is set per texel).
        public readonly Vector4[] InputValues;
        public readonly int[] UvRegisters;
        // The tangent-space light vector and camera vector (TEXCOORD6), set per run by the
        // specular bake (`lit`). The light vector is TEXCOORD4, except in translucent materials,
        // which pass it in TEXCOORD3 and their fog in TEXCOORD4.
        public readonly int[] LightRegisters, CameraRegisters;

        public CompiledShader(Program program, float[] constants, Dictionary<int, Texels> slots, bool lit = false)
        {
            Instruction[] code = program.Code.ToArray();
            Length = code.Length;
            AmbientRegister = program.AmbientRegister;
            Temps = Math.Max(program.Temps, 1);
            ConstantCount = constants.Length / 4;

            Split = code.Length;
            int inputs = program.Inputs.Keys.DefaultIfEmpty(0).Max() + 1;
            var textures = new Texels[code.Length];
            for (int i = 0; i < code.Length; i++)
            {
                Instruction ins = code[i];
                foreach (Operand o in ins.Operands)
                {
                    if (o.Kind == Kind.Input)
                        inputs = Math.Max(inputs, o.Index + 1);
                    if (o.Kind == Kind.Constant && o.Index == AmbientRegister && i < Split)
                        Split = i;
                }
                // Cubemaps (reflections) and unknown textures sample black, as in the level bakes.
                if (ins.Code == OpCode.Sample && program.Resources.TryGetValue(ins.Resource, out int slot) && slot >= 0)
                    slots.TryGetValue(slot, out textures[i]);
            }
            Inputs = inputs;

            // An identity tangent frame (tangent +X, normal +Z, binormal sign +1), a camera
            // straight down the normal, and white.
            InputValues = new Vector4[inputs];
            var uvRegisters = new List<int>();
            foreach (var (register, semantic) in program.Inputs)
            {
                if (semantic == "TEXCOORD0")
                    uvRegisters.Add(register);
                InputValues[register] = semantic switch
                {
                    "TEXCOORD10" => new Vector4(1f, 0f, 0f, 0f),
                    "TEXCOORD11" => new Vector4(0f, 0f, 1f, 1f),
                    "TEXCOORD6" or "TEXCOORD7" => new Vector4(0f, 0f, 1f, 0f),
                    // Translucent materials' height fog: nothing added (xyz), the color kept
                    // whole (w). Zero made their whole result black (the hair). Opaque lit
                    // shaders read their light vector here instead, set per run.
                    "TEXCOORD4" => new Vector4(0f, 0f, 0f, 1f),
                    "COLOR0" => Vector4.One,
                    "SV_IsFrontFace0" => new Vector4(BitConverter.UInt32BitsToSingle(0xFFFFFFFF)),
                    _ => Vector4.Zero,
                };
            }
            UvRegisters = uvRegisters.ToArray();
            bool translucent = lit && program.Inputs.ContainsValue("TEXCOORD3");
            string lightSemantic = translucent ? "TEXCOORD3" : "TEXCOORD4";
            LightRegisters = program.Inputs.Where(kv => kv.Value == lightSemantic).Select(kv => kv.Key).ToArray();
            CameraRegisters = program.Inputs.Where(kv => kv.Value == "TEXCOORD6").Select(kv => kv.Key).ToArray();

            Prefix = new SegmentCompiler(this, code, textures).Compile(0, Split);
            Suffix = new SegmentCompiler(this, code, textures).Compile(Split, code.Length);
        }
    }

    private sealed class SegmentCompiler
    {
        private readonly CompiledShader _shader;
        private readonly Instruction[] _code;
        private readonly Texels[] _textures;
        private readonly ParameterExpression _v = Expression.Parameter(typeof(Vector4[]), "v");
        private readonly ParameterExpression _cb = Expression.Parameter(typeof(Vector4[]), "cb");
        private readonly ParameterExpression _rArray = Expression.Parameter(typeof(Vector4[]), "r");
        private readonly ParameterExpression _oArray = Expression.Parameter(typeof(Vector4[]), "o");
        private readonly ParameterExpression[] _r, _o;
        private readonly LabelTarget _return = Expression.Label(typeof(bool), "done");

        public SegmentCompiler(CompiledShader shader, Instruction[] code, Texels[] textures)
        {
            _shader = shader;
            _code = code;
            _textures = textures;
            _r = Enumerable.Range(0, shader.Temps).Select(i => Expression.Variable(typeof(Vector4), $"r{i}")).ToArray();
            _o = Enumerable.Range(0, 8).Select(i => Expression.Variable(typeof(Vector4), $"o{i}")).ToArray();
        }

        public Segment Compile(int from, int to)
        {
            var body = new List<Expression>();
            for (int i = 0; i < _r.Length; i++)
                body.Add(Expression.Assign(_r[i], Expression.ArrayIndex(_rArray, Expression.Constant(i))));
            for (int i = 0; i < _o.Length; i++)
                body.Add(Expression.Assign(_o[i], Expression.ArrayIndex(_oArray, Expression.Constant(i))));
            for (int i = from; i < to; i++)
                body.Add(Emit(i));
            body.Add(Store());
            body.Add(Expression.Label(_return, Expression.Constant(true)));
            var block = Expression.Block(typeof(bool), _r.Concat(_o), body);
            return Expression.Lambda<Segment>(block, _v, _cb, _rArray, _oArray).Compile();
        }

        // Writes every register back to the arrays.
        private Expression Store()
        {
            var writes = new List<Expression>();
            for (int i = 0; i < _r.Length; i++)
                writes.Add(Expression.Assign(Expression.ArrayAccess(_rArray, Expression.Constant(i)), _r[i]));
            for (int i = 0; i < _o.Length; i++)
                writes.Add(Expression.Assign(Expression.ArrayAccess(_oArray, Expression.Constant(i)), _o[i]));
            return Expression.Block(writes);
        }

        private Expression Emit(int index)
        {
            Instruction ins = _code[index];
            Operand[] o = ins.Operands;
            Expression S(int i) => Read(o[i]);
            Expression Op(string name, params Expression[] args) => Expression.Call(typeof(Ops).GetMethod(name)!, args);
            Expression result;
            switch (ins.Code)
            {
                case OpCode.DiscardNz:
                    return Expression.IfThen(Op(nameof(Ops.NonZeroX), S(0)), Discard());
                case OpCode.DiscardZ:
                    return Expression.IfThen(Expression.Not(Op(nameof(Ops.NonZeroX), S(0))), Discard());
                case OpCode.Sample:
                {
                    Texels texels = _textures[index];
                    int[] s = o[2].Swizzle;
                    result = texels == null
                        ? Expression.Constant(Vector4.Zero)
                        : Op(nameof(Ops.Sample), Expression.Constant(texels), S(1),
                            Expression.Constant(s[0]), Expression.Constant(s[1]), Expression.Constant(s[2]), Expression.Constant(s[3]));
                    break;
                }
                case OpCode.Mov: result = S(1); break;
                case OpCode.Add: result = Op(nameof(Ops.Add), S(1), S(2)); break;
                case OpCode.Mul: result = Op(nameof(Ops.Mul), S(1), S(2)); break;
                case OpCode.Mad: result = Op(nameof(Ops.Mad), S(1), S(2), S(3)); break;
                case OpCode.Div: result = Op(nameof(Ops.Div), S(1), S(2)); break;
                case OpCode.Min: result = Op(nameof(Ops.Min), S(1), S(2)); break;
                case OpCode.Max: result = Op(nameof(Ops.Max), S(1), S(2)); break;
                case OpCode.Sqrt: result = Op(nameof(Ops.Sqrt), S(1)); break;
                case OpCode.Rsq: result = Op(nameof(Ops.Rsq), S(1)); break;
                case OpCode.Log: result = Op(nameof(Ops.Log), S(1)); break;
                case OpCode.Exp: result = Op(nameof(Ops.Exp), S(1)); break;
                case OpCode.Frc: result = Op(nameof(Ops.Frc), S(1)); break;
                case OpCode.RoundNe: result = Op(nameof(Ops.RoundNe), S(1)); break;
                case OpCode.RoundNi: result = Op(nameof(Ops.RoundNi), S(1)); break;
                case OpCode.RoundPi: result = Op(nameof(Ops.RoundPi), S(1)); break;
                case OpCode.RoundZ: result = Op(nameof(Ops.RoundZ), S(1)); break;
                case OpCode.Dp2: result = Op(nameof(Ops.Dp2), S(1), S(2)); break;
                case OpCode.Dp3: result = Op(nameof(Ops.Dp3), S(1), S(2)); break;
                case OpCode.Dp4: result = Op(nameof(Ops.Dp4), S(1), S(2)); break;
                case OpCode.Lt: result = Op(nameof(Ops.Lt), S(1), S(2)); break;
                case OpCode.Ge: result = Op(nameof(Ops.Ge), S(1), S(2)); break;
                case OpCode.Eq: result = Op(nameof(Ops.Eq), S(1), S(2)); break;
                case OpCode.Ne: result = Op(nameof(Ops.Ne), S(1), S(2)); break;
                case OpCode.Movc: result = Op(nameof(Ops.Movc), S(1), S(2), S(3)); break;
                case OpCode.And: result = Op(nameof(Ops.And), S(1), S(2)); break;
                case OpCode.Or: result = Op(nameof(Ops.Or), S(1), S(2)); break;
                case OpCode.Sincos:
                {
                    // Both results come from the source as it was before either write.
                    ParameterExpression x = Expression.Variable(typeof(Vector4), "x");
                    var steps = new List<Expression> { Expression.Assign(x, S(2)) };
                    if (o[0].Kind != Kind.Null)
                        steps.Add(Write(o[0], Op(nameof(Ops.Sin), x), ins.Saturate));
                    if (o[1].Kind != Kind.Null)
                        steps.Add(Write(o[1], Op(nameof(Ops.Cos), x), ins.Saturate));
                    steps.Add(Expression.Empty());
                    return Expression.Block(new[] { x }, steps);
                }
                case OpCode.Deriv:
                    result = Expression.Constant(Vector4.Zero);
                    break;
                default:
                    // Reached only when the texel gets this far, as with the interpreter.
                    return Expression.Throw(Expression.New(typeof(NotSupportedException).GetConstructor([typeof(string)])!,
                        Expression.Constant($"instruction {ins.Op}")));
            }
            return Write(o[0], result, ins.Saturate);
        }

        private Expression Discard() => Expression.Block(Store(), Expression.Return(_return, Expression.Constant(false)));

        private Expression Read(Operand op)
        {
            Expression value = op.Kind switch
            {
                Kind.Temp => _r[op.Index],
                Kind.Output => _o[op.Index],
                Kind.Input => op.Index < _shader.Inputs ? Expression.ArrayIndex(_v, Expression.Constant(op.Index)) : Expression.Constant(Vector4.Zero),
                Kind.Constant => op.Index < _shader.ConstantCount ? Expression.ArrayIndex(_cb, Expression.Constant(op.Index)) : Expression.Constant(Vector4.Zero),
                Kind.Literal => Expression.Constant(op.Value),
                _ => Expression.Constant(Vector4.Zero),
            };
            int[] s = op.Swizzle;
            if (op.Kind != Kind.Literal && !op.Identity)
                value = Expression.Call(typeof(Ops).GetMethod(nameof(Ops.Swizzle))!, value,
                    Expression.Constant(s[0]), Expression.Constant(s[1]), Expression.Constant(s[2]), Expression.Constant(s[3]));
            if (op.Absolute)
                value = Expression.Call(typeof(Vector4).GetMethod(nameof(Vector4.Abs), [typeof(Vector4)])!, value);
            if (op.Negate)
                value = Expression.Call(typeof(Vector4).GetMethod(nameof(Vector4.Negate), [typeof(Vector4)])!, value);
            return value;
        }

        // Destination swizzles are write masks; a source's components line up with them.
        private Expression Write(Operand op, Expression value, bool saturate)
        {
            ParameterExpression bank = op.Kind == Kind.Temp ? _r[op.Index] : op.Kind == Kind.Output ? _o[op.Index] : null;
            if (bank == null)
                return Expression.Empty();
            if (saturate)
                value = Expression.Call(typeof(Ops).GetMethod(nameof(Ops.Saturate))!, value);
            if (!op.FullMask)
                value = Expression.Call(typeof(Ops).GetMethod(nameof(Ops.Masked))!, bank, value,
                    Expression.Constant(op.Mask[0]), Expression.Constant(op.Mask[1]), Expression.Constant(op.Mask[2]), Expression.Constant(op.Mask[3]));
            return Expression.Assign(bank, value);
        }
    }

    // The instructions' math, called from the compiled shaders.
    private static class Ops
    {
        public static bool NonZeroX(Vector4 v) => Bits(v.X) != 0;
        public static Vector4 Add(Vector4 a, Vector4 b) => a + b;
        public static Vector4 Mul(Vector4 a, Vector4 b) => a * b;
        public static Vector4 Mad(Vector4 a, Vector4 b, Vector4 c) => a * b + c;
        public static Vector4 Div(Vector4 a, Vector4 b) => a / b;
        public static Vector4 Min(Vector4 a, Vector4 b) => Vector4.Min(a, b);
        public static Vector4 Max(Vector4 a, Vector4 b) => Vector4.Max(a, b);
        public static Vector4 Sqrt(Vector4 v) => new(MathF.Sqrt(v.X), MathF.Sqrt(v.Y), MathF.Sqrt(v.Z), MathF.Sqrt(v.W));
        public static Vector4 Rsq(Vector4 v) => new(1f / MathF.Sqrt(v.X), 1f / MathF.Sqrt(v.Y), 1f / MathF.Sqrt(v.Z), 1f / MathF.Sqrt(v.W));
        public static Vector4 Log(Vector4 v) => new(MathF.Log2(v.X), MathF.Log2(v.Y), MathF.Log2(v.Z), MathF.Log2(v.W));
        public static Vector4 Exp(Vector4 v) => new(MathF.Pow(2f, v.X), MathF.Pow(2f, v.Y), MathF.Pow(2f, v.Z), MathF.Pow(2f, v.W));
        public static Vector4 Frc(Vector4 v) => new(v.X - MathF.Floor(v.X), v.Y - MathF.Floor(v.Y), v.Z - MathF.Floor(v.Z), v.W - MathF.Floor(v.W));
        public static Vector4 RoundNe(Vector4 v) => new(MathF.Round(v.X, MidpointRounding.ToEven), MathF.Round(v.Y, MidpointRounding.ToEven),
            MathF.Round(v.Z, MidpointRounding.ToEven), MathF.Round(v.W, MidpointRounding.ToEven));
        public static Vector4 RoundNi(Vector4 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z), MathF.Floor(v.W));
        public static Vector4 RoundPi(Vector4 v) => new(MathF.Ceiling(v.X), MathF.Ceiling(v.Y), MathF.Ceiling(v.Z), MathF.Ceiling(v.W));
        public static Vector4 RoundZ(Vector4 v) => new(MathF.Truncate(v.X), MathF.Truncate(v.Y), MathF.Truncate(v.Z), MathF.Truncate(v.W));
        public static Vector4 Sin(Vector4 v) => new(MathF.Sin(v.X), MathF.Sin(v.Y), MathF.Sin(v.Z), MathF.Sin(v.W));
        public static Vector4 Cos(Vector4 v) => new(MathF.Cos(v.X), MathF.Cos(v.Y), MathF.Cos(v.Z), MathF.Cos(v.W));
        public static Vector4 Dp2(Vector4 a, Vector4 b) => new(a.X * b.X + a.Y * b.Y);
        public static Vector4 Dp3(Vector4 a, Vector4 b) => new(a.X * b.X + a.Y * b.Y + a.Z * b.Z);
        public static Vector4 Dp4(Vector4 a, Vector4 b) => new(Vector4.Dot(a, b));
        public static Vector4 Lt(Vector4 a, Vector4 b) => new(Mask(a.X < b.X), Mask(a.Y < b.Y), Mask(a.Z < b.Z), Mask(a.W < b.W));
        public static Vector4 Ge(Vector4 a, Vector4 b) => new(Mask(a.X >= b.X), Mask(a.Y >= b.Y), Mask(a.Z >= b.Z), Mask(a.W >= b.W));
        public static Vector4 Eq(Vector4 a, Vector4 b) => new(Mask(a.X == b.X), Mask(a.Y == b.Y), Mask(a.Z == b.Z), Mask(a.W == b.W));
        public static Vector4 Ne(Vector4 a, Vector4 b) => new(Mask(a.X != b.X), Mask(a.Y != b.Y), Mask(a.Z != b.Z), Mask(a.W != b.W));
        public static Vector4 Movc(Vector4 c, Vector4 a, Vector4 b) =>
            new(Bits(c.X) != 0 ? a.X : b.X, Bits(c.Y) != 0 ? a.Y : b.Y, Bits(c.Z) != 0 ? a.Z : b.Z, Bits(c.W) != 0 ? a.W : b.W);
        public static Vector4 And(Vector4 a, Vector4 b) =>
            new(Float(Bits(a.X) & Bits(b.X)), Float(Bits(a.Y) & Bits(b.Y)), Float(Bits(a.Z) & Bits(b.Z)), Float(Bits(a.W) & Bits(b.W)));
        public static Vector4 Or(Vector4 a, Vector4 b) =>
            new(Float(Bits(a.X) | Bits(b.X)), Float(Bits(a.Y) | Bits(b.Y)), Float(Bits(a.Z) | Bits(b.Z)), Float(Bits(a.W) | Bits(b.W)));
        public static Vector4 Saturate(Vector4 v) => Vector4.Clamp(v, Vector4.Zero, Vector4.One);
        public static Vector4 Masked(Vector4 current, Vector4 value, bool x, bool y, bool z, bool w) =>
            new(x ? value.X : current.X, y ? value.Y : current.Y, z ? value.Z : current.Z, w ? value.W : current.W);
        public static Vector4 Swizzle(Vector4 v, int x, int y, int z, int w) => new(Get(v, x), Get(v, y), Get(v, z), Get(v, w));

        public static Vector4 Sample(Texels texels, Vector4 coord, int x, int y, int z, int w) =>
            Swizzle(texels.Sample(coord.X, coord.Y), x, y, z, w);

        private static float Get(Vector4 v, int c) => c switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };
        private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
        private static float Float(uint u) => BitConverter.UInt32BitsToSingle(u);
        private static float Mask(bool b) => Float(b ? 0xFFFFFFFF : 0u);
    }

    private sealed class Machine
    {
        private readonly CompiledShader _shader;
        private readonly Vector4[] _v, _cb, _r, _o, _savedR, _savedO;

        public Machine(CompiledShader shader, float[] constants)
        {
            _shader = shader;
            _v = (Vector4[])shader.InputValues.Clone();
            _cb = new Vector4[shader.ConstantCount];
            for (int i = 0; i < _cb.Length; i++)
                _cb[i] = new Vector4(constants[i * 4], constants[i * 4 + 1], constants[i * 4 + 2], constants[i * 4 + 3]);
            _r = new Vector4[shader.Temps];
            _o = new Vector4[8];
            _savedR = new Vector4[_r.Length];
            _savedO = new Vector4[_o.Length];
        }

        // Runs the shader at one UV with ambient at 0 (emit) and at 1 (lit, and the normal);
        // false when the ambient-0 run discards the pixel.
        public bool Run(Vector2 uv, out Vector4 emit, out Vector4 lit, out Vector4 normal)
        {
            Array.Clear(_r);
            Array.Clear(_o);
            foreach (int register in _shader.UvRegisters)
                _v[register] = new Vector4(uv, 0f, 0f);

            // A discard before the split, or no ambient at all: both runs end the same way.
            bool prefixKept = _shader.Prefix(_v, _cb, _r, _o);
            if (!prefixKept || _shader.Split == _shader.Length)
            {
                emit = lit = _o[0];
                normal = _o[2];
                return prefixKept;
            }

            Array.Copy(_r, _savedR, _r.Length);
            Array.Copy(_o, _savedO, _o.Length);
            _cb[_shader.AmbientRegister] = Vector4.Zero;
            bool kept = _shader.Suffix(_v, _cb, _r, _o);
            emit = _o[0];

            Array.Copy(_savedR, _r, _r.Length);
            Array.Copy(_savedO, _o, _o.Length);
            _cb[_shader.AmbientRegister] = Vector4.One;
            _shader.Suffix(_v, _cb, _r, _o);
            lit = _o[0];
            normal = _o[2];
            return kept;
        }

        // Runs the whole shader once at one UV, lit from `light` and seen from `camera` (both in
        // tangent space), with the constants as they are.
        public bool RunLit(Vector2 uv, Vector3 light, Vector3 camera, out Vector4 color)
        {
            Array.Clear(_r);
            Array.Clear(_o);
            foreach (int register in _shader.UvRegisters)
                _v[register] = new Vector4(uv, 0f, 0f);
            foreach (int register in _shader.LightRegisters)
                _v[register] = new Vector4(light, 0f);
            foreach (int register in _shader.CameraRegisters)
                _v[register] = new Vector4(camera, 0f);
            bool kept = _shader.Prefix(_v, _cb, _r, _o) && _shader.Suffix(_v, _cb, _r, _o);
            color = _o[0];
            return kept;
        }
    }
}
