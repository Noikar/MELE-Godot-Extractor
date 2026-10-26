using System.Text;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Writes every base Material's compiled base-pass pixel shaders, disassembled, one text file
// per material: <out>/<Material>.txt. Its header lists the material's blend mode and lighting
// model, its uniform texture slots (with their default textures), uniform vectors and scalars
// (as expression trees), and the material instances in the package that use it, with their
// parameters. Then comes each TBasePassPixelShader variant the package's shader cache holds
// for static meshes (one per light-map policy, with and without sky light), or with --skinned
// for skeletal meshes (characters' GPU-skin variants). --pass <prefix> dumps another shader
// type instead, e.g. TLightPixelShader (the per-light passes, with the material's specular). This is how a
// material's look was read for ShaderAnalysis and ShaderTranslator; start here when a
// translated surface looks wrong.
public static class ShadersCommand
{
    public static int Run(string outDir, string[] args)
    {
        string filter = null;
        string vertexFactory = "FLocalVertexFactory";
        string cachePackage = null;
        string pass = "TBasePassPixelShader";
        var patterns = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length)
                filter = args[++i];
            else if (args[i] == "--cache" && i + 1 < args.Length)
                cachePackage = args[++i];
            else if (args[i] == "--pass" && i + 1 < args.Length)
                pass = args[++i];
            else if (args[i] == "--skinned")
                vertexFactory = "FGPUSkinVertexFactory";
            else
                patterns.Add(args[i]);
        }

        Directory.CreateDirectory(outDir);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in patterns.SelectMany(LevelCommand.ExpandPackages).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using IMEPackage pcc = MEPackageHandler.OpenMEPackage(path);
            ShaderCache cache = ShaderAnalysis.CacheOf(pcc);
            // Materials in shared packages (BIOC_Materials) are compiled into the levels'
            // caches, or the game's RefShaderCache.
            if (cachePackage != null)
                cache = ShaderAnalysis.CacheOfFile(Program.ResolvePackage(cachePackage));
            if (cache == null)
                continue;
            ILookup<string, ExportEntry> instances = InstancesByBase(pcc);

            foreach (ExportEntry material in pcc.Exports.Where(e => e.ClassName is "Material" or "DecalMaterial"))
            {
                string name = material.ObjectName.Instanced;
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !instances[material.InstancedFullPath].Any(m => m.ObjectName.Instanced.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!written.Add(material.InstancedFullPath))
                    continue;

                string text = Describe(pcc, cache, material, instances[material.InstancedFullPath], vertexFactory, pass, out int count);
                File.WriteAllText(Path.Combine(outDir, name + ".txt"), text);
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(path)} {name}: {count} {pass} shader(s)");
            }
        }
        return 0;
    }

    // Base material path -> the material instances whose parent chain ends there.
    private static ILookup<string, ExportEntry> InstancesByBase(IMEPackage pcc)
    {
        var pairs = new List<(string Base, ExportEntry Instance)>();
        foreach (ExportEntry instance in pcc.Exports.Where(e => e.ClassName == "MaterialInstanceConstant"))
        {
            IEntry current = instance;
            while (current is ExportEntry e && e.ClassName is not ("Material" or "DecalMaterial"))
            {
                var parent = e.GetProperties().GetProp<ObjectProperty>("Parent");
                current = parent != null ? pcc.GetEntry(parent.Value) : null;
            }
            if (current is ExportEntry baseMaterial)
                pairs.Add((baseMaterial.InstancedFullPath, instance));
        }
        return pairs.ToLookup(p => p.Base, p => p.Instance);
    }

    private static string Describe(IMEPackage pcc, ShaderCache cache, ExportEntry material, IEnumerable<ExportEntry> instances,
        string vertexFactory, string pass, out int count)
    {
        var text = new StringBuilder();
        var props = material.GetProperties();
        string lighting = props.GetProp<EnumProperty>("LightingModel")?.Value.Instanced ?? "MLM_Phong";
        string blend = props.GetProp<EnumProperty>("BlendMode")?.Value.Instanced ?? "BLEND_Opaque";
        text.AppendLine($"// {material.InstancedFullPath} ({pcc.FileNameNoExtension}): {lighting}, {blend}"
            + (props.GetProp<BoolProperty>("TwoSided")?.Value == true ? ", two-sided" : ""));
        foreach (ExportEntry instance in instances)
            text.AppendLine($"// instance {instance.InstancedFullPath}:{Parameters(pcc, instance)}");

        var resource = ObjectBinary.From<Material>(material).SM3MaterialResource;
        int[] defaults = resource.UniformExpressionTextures;
        string Default(MaterialUniformExpressionTexture t) => t.TextureIndex < defaults.Length ? pcc.GetEntry(defaults[t.TextureIndex])?.InstancedFullPath : "?";

        count = 0;
        // One shader map per set of static parameters the material was cooked with.
        var maps = cache.MaterialShaderMaps.Values.Where(m => m.StaticParameters.BaseMaterialId == resource.ID).ToList();
        for (int m = 0; m < maps.Count; m++)
        {
            MaterialShaderMap map = maps[m];
            text.AppendLine($"//\n// ==== shader map {m}");
            for (int i = 0; i < (map.Uniform2DTextureExpressions?.Length ?? 0); i++)
                text.AppendLine($"// Texture2D_{i}: {Expression(map.Uniform2DTextureExpressions[i])} = {Default(map.Uniform2DTextureExpressions[i])}");
            for (int i = 0; i < (map.UniformCubeTextureExpressions?.Length ?? 0); i++)
                text.AppendLine($"// TextureCube_{i}: {Expression(map.UniformCubeTextureExpressions[i])} = {Default(map.UniformCubeTextureExpressions[i])}");
            for (int i = 0; i < (map.UniformPixelVectorExpressions?.Length ?? 0); i++)
                text.AppendLine($"// UniformPixelVector_{i}: {Expression(map.UniformPixelVectorExpressions[i])}");
            for (int i = 0; i < (map.UniformPixelScalarExpressions?.Length ?? 0); i++)
                text.AppendLine($"// UniformPixelScalar_{i}: {Expression(map.UniformPixelScalarExpressions[i])}");

            var shaders = map.MeshShaderMaps
                .Where(s => s.VertexFactoryType.Instanced == vertexFactory)
                .SelectMany(s => s.Shaders)
                .Where(s => s.Key.Instanced.StartsWith(pass))
                .OrderBy(s => s.Key.Instanced, StringComparer.Ordinal);
            foreach (var (type, reference) in shaders)
            {
                if (!cache.Shaders.TryGetValue(reference.Id, out Shader shader))
                    continue;
                text.AppendLine($"\n// ==== {type.Instanced}");
                // The disassembly blob ends in a NUL, which would make the file read as binary.
                text.AppendLine(ShaderAnalysis.Disassemble(shader.ShaderByteCode)?.TrimEnd('\0') ?? "// (could not disassemble)");
                count++;
            }
        }
        return text.ToString();
    }

    // An instance's own scalar, vector, and texture parameters.
    private static string Parameters(IMEPackage pcc, ExportEntry instance)
    {
        var text = new StringBuilder();
        var props = instance.GetProperties();
        foreach (StructProperty s in props.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues") ?? Enumerable.Empty<StructProperty>())
            text.Append($" {s.GetProp<NameProperty>("ParameterName")?.Value.Instanced}={s.GetProp<FloatProperty>("ParameterValue")?.Value}");
        foreach (StructProperty s in props.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") ?? Enumerable.Empty<StructProperty>())
        {
            StructProperty c = s.GetProp<StructProperty>("ParameterValue");
            float F(string channel) => c?.GetProp<FloatProperty>(channel)?.Value ?? 0f;
            text.Append($" {s.GetProp<NameProperty>("ParameterName")?.Value.Instanced}=({F("R")}, {F("G")}, {F("B")}, {F("A")})");
        }
        foreach (StructProperty s in props.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") ?? Enumerable.Empty<StructProperty>())
            text.Append($" {s.GetProp<NameProperty>("ParameterName")?.Value.Instanced}={pcc.GetEntry(s.GetProp<ObjectProperty>("ParameterValue")?.Value ?? 0)?.ObjectName.Instanced}");
        return text.ToString();
    }

    private static string Expression(MaterialUniformExpression x) => x switch
    {
        MaterialUniformExpressionTextureParameter t => $"TextureParam {t.ParameterName.Instanced}",
        MaterialUniformExpressionVectorParameter v => $"VectorParam {v.ParameterName.Instanced} default ({v.DefaultR}, {v.DefaultG}, {v.DefaultB}, {v.DefaultA})",
        MaterialUniformExpressionScalarParameter s => $"ScalarParam {s.ParameterName.Instanced} default {s.DefaultValue}",
        MaterialUniformExpressionConstant c => $"Const ({c.R}, {c.G}, {c.B}, {c.A})",
        MaterialUniformExpressionFoldedMath f => $"{f.Op} [{Expression(f.A)}] [{Expression(f.B)}]",
        _ => x.ExpressionType.Instanced,
    };
}
