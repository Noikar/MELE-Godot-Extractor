using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.Classes;
using Texture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;
using SharpGLTF.Materials;
using SharpGLTF.Memory;

namespace LexExport;

// Builds glTF materials from Unreal material instances: reads the instance's texture and color
// parameters and embeds a base-color texture, either the diffuse as-is or one baked from tint
// masks (MaterialBaker). Each texture is decoded once.
public class TextureCache
{
    private readonly IMEPackage _pcc;
    // Keyed by full path, so the caches of several packages (a level's sublevels, which each
    // carry their own copies of shared assets) can share one set.
    private readonly Dictionary<string, MemoryImage?> _images;
    private readonly Dictionary<string, MaterialBuilder> _materials;

    // Largest texture side to embed (0 = full size); a smaller mip is picked to fit.
    public int MaxTextureSize { get; init; }

    // When a material has no "Diff" parameter, pick its diffuse by texture name instead.
    // Level materials name their parameters freely; characters keep the strict rule.
    public bool GuessDiffuse { get; init; }

    // Level surfaces: unlit materials stay unlit (translucent ones are baked from their
    // compiled shader), and lit ones get their normal map and a roughness map from their
    // specular map.
    public bool SurfaceShading { get; init; }

    // Level surfaces: when set, lit materials also carry their original shader, translated
    // (AddTranslatedShader), and the textures it samples are collected here by key for the
    // level to embed. Shared across packages, like the images.
    public Dictionary<string, MemoryImage?> CarriedTextures { get; init; }

    // Characters: bake each material's color and normal map from its compiled shader
    // (ShaderBaker), falling back to MaterialBaker's recipes and the plain diffuse.
    public bool BakeShaders { get; init; }

    // Skeletal placeables in a level: unlit materials (a door's glass) come from this cache,
    // the level's own, so they're the level's materials with their translated shaders (the
    // railings' scanline glass), not a skinned bake, which has nothing to bake for them.
    public TextureCache UnlitSource { get; init; }

    // Effects from ME2's and ME3's seek-free packages, which hold no shaders: translate from
    // the game's RefShaderCache instead (ShaderAnalysis.CacheWithShared), along with their
    // distortion pass.
    public bool SharedShaders { get; init; }

    private ShaderTranslator.Policy[] UnlitPolicies => SharedShaders
        ? [ShaderTranslator.Policy.Unlit, ShaderTranslator.Policy.Distortion]
        : [ShaderTranslator.Policy.Unlit];

    // A character's own appearance (a BioMaterialOverride: skin tone, hair color, face
    // textures), which wins over every material instance's defaults.
    private readonly Dictionary<string, ExportEntry> _textureOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> _colorOverrides = new(StringComparer.OrdinalIgnoreCase);

    // Material-name substrings whose sections are left out of the mesh.
    private readonly List<string> _hidden;

    // The package whose material references this resolves.
    public IMEPackage Package => _pcc;

    public TextureCache(IMEPackage pcc, List<string> hidden,
        Dictionary<string, MaterialBuilder> sharedMaterials = null, Dictionary<string, MemoryImage?> sharedImages = null)
    {
        _pcc = pcc;
        _hidden = hidden;
        _materials = sharedMaterials ?? new();
        _images = sharedImages ?? new();
    }

    // Matched against the material and its parents: a level pawn's own instances have generic
    // names (BioMaterialInstanceConstant_3528) under a parent that says what they are.
    public bool IsHidden(int materialUIndex)
    {
        IEntry entry = materialUIndex != 0 ? _pcc.GetEntry(materialUIndex) : null;
        for (int depth = 0; entry != null && depth < 8; depth++)
        {
            string name = entry.ObjectName.Instanced;
            if (_hidden.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                return true;
            entry = entry is ExportEntry export && export.GetProperties().GetProp<ObjectProperty>("Parent") is ObjectProperty parent
                ? _pcc.GetEntry(parent.Value) : null;
        }
        return false;
    }

    // keepSkinTone leaves an earlier SkinTone in place. An ME1 pawn's override tints its
    // clothes, but its head takes skin from the morph face, and some pawns' overrides carry
    // a human skin tone that would turn an asari pink.
    public void AddOverrides(ExportEntry materialOverride, bool keepSkinTone = false)
    {
        var props = materialOverride.GetProperties();
        foreach (StructProperty p in props.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides") ?? Enumerable.Empty<StructProperty>())
        {
            string name = p.GetProp<NameProperty>("nName")?.Value.Instanced;
            int uIndex = p.GetProp<ObjectProperty>("m_pTexture")?.Value ?? 0;
            // The override may come from another package than the meshes.
            if (name != null && name != "None" && materialOverride.FileRef.TryGetUExport(uIndex, out ExportEntry tex))
                _textureOverrides[name] = tex;
        }
        foreach (StructProperty p in props.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides") ?? Enumerable.Empty<StructProperty>())
        {
            string name = p.GetProp<NameProperty>("nName")?.Value.Instanced;
            if (name == null || name == "None" || p.GetProp<StructProperty>("cValue") is not StructProperty c)
                continue;
            if (keepSkinTone && name.Equals("SkinTone", StringComparison.OrdinalIgnoreCase) && _colorOverrides.ContainsKey(name))
                continue;
            _colorOverrides[name] = LinearColor(c);
        }
        Console.WriteLine($"  appearance {materialOverride.ObjectName.Instanced}: {_textureOverrides.Count} texture(s), {_colorOverrides.Count} color(s)");
    }

    // materialUIndex is the SkeletalMesh's material reference for a section; fallbackName names
    // the material when that reference can't be resolved (e.g. it points at another package).
    public MaterialBuilder MaterialFor(int materialUIndex, string fallbackName) =>
        MaterialFor(materialUIndex != 0 && _pcc.TryGetUExport(materialUIndex, out ExportEntry e) ? e : null, fallbackName);

    // The same for a material export of any package (an effect's, imported from SFXGame).
    public MaterialBuilder MaterialFor(ExportEntry material, string fallbackName)
    {
        string key = material?.InstancedFullPath ?? fallbackName;
        if (_materials.TryGetValue(key, out MaterialBuilder cached))
            return cached;

        var builder = new MaterialBuilder(material?.ObjectName.Instanced ?? fallbackName)
            .WithMetallicRoughnessShader()
            .WithMetallicRoughness(0f, 0.7f);

        var textures = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        var colors = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Vector parameters' alpha, which the translated shaders read (colors keep RGB only).
        var alphas = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (material != null)
            ReadParameters(material, textures, colors, scalars, alphas);

        ExportEntry baseMaterial = material != null ? BaseMaterial(material) : null;
        if (UnlitSource != null && baseMaterial?.GetProperties().GetProp<EnumProperty>("LightingModel")?.Value.Instanced == "MLM_Unlit")
        {
            _materials[key] = UnlitSource.MaterialFor(material, fallbackName);
            return _materials[key];
        }
        // BioWare's volumetric light shafts and glow puffs: no texture to bake, only shader
        // parameters, which ride along in the extras for the game's own glow shader. In
        // ORIGINAL mode their translated shader draws them instead.
        if (baseMaterial != null && baseMaterial.ObjectName.Instanced.StartsWith("Volumetric", StringComparison.OrdinalIgnoreCase))
        {
            string translated = CarriedTextures != null
                ? AddTranslatedShader(builder, baseMaterial, material, textures, colors, scalars, alphas, ShaderTranslator.Policy.Unlit)
                : "";
            _materials[key] = VolumetricMaterial(builder, colors, scalars, translated);
            return _materials[key];
        }

        // A texture override only replaces a parameter the material actually has (the face
        // diffuse must not leak into the eyes); colors are looked up by name, so they all apply.
        foreach (var (name, tex) in _textureOverrides)
        {
            if (textures.ContainsKey(name))
                textures[name] = tex;
        }
        foreach (var (name, color) in _colorOverrides)
            colors[name] = color;

        MemoryImage? image = null;
        bool alphaMask = false;
        string recipe = null, shaderNote = "";
        MemoryImage? shaderNormal = null;
        if (BakeShaders && material != null)
        {
            ShaderBaker.Result shaded = ShaderBaker.Bake(_pcc, material, textures, colors, scalars, MaxTextureSize > 0 ? MaxTextureSize : 2048, out string failure);
            if (shaded != null)
            {
                image = shaded.Albedo;
                alphaMask = shaded.Cutout;
                shaderNormal = shaded.Normal;
                if (shaded.Emissive.HasValue)
                    builder.WithChannelImage(KnownChannel.Emissive, shaded.Emissive.Value).WithEmissive(Vector3.One);
                // KHR_materials_specular's color texture, with the power in its alpha (our own
                // convention; see ShaderBaker) and the scale as its color factor.
                if (shaded.Specular.HasValue)
                {
                    builder.WithChannelImage(KnownChannel.SpecularColor, shaded.Specular.Value);
                    builder.WithChannelParam(KnownChannel.SpecularColor, KnownProperty.RGB, new Vector3(shaded.SpecularScale));
                }
                recipe = "shader";
                shaderNote = ", " + shaded.Note;
            }
            else
                shaderNote = $", shader not baked ({failure})";
        }
        if (image == null)
            image = MaterialBaker.Bake(textures, colors, scalars, out alphaMask, out recipe);
        ExportEntry diffuse = null;
        if (image == null)
        {
            diffuse = textures.FirstOrDefault(kv => kv.Key.Contains("Diff", StringComparison.OrdinalIgnoreCase)).Value;
            if (diffuse == null && GuessDiffuse && material != null)
                diffuse = GuessDiffuseTexture(material, textures.Values);
            image = diffuse != null ? Decode(diffuse) : null;
        }

        // The base material decides cutout, two-sidedness (grates, foliage, signs), and lighting.
        var baseProps = baseMaterial?.GetProperties();
        string blendMode = baseProps?.GetProp<EnumProperty>("BlendMode")?.Value.Instanced ?? "BLEND_Opaque";
        // A shader bake knows its own cutout (and the effects user isn't a Material).
        if (blendMode == "BLEND_Masked" && recipe != "shader")
            alphaMask = true;
        if (baseProps?.GetProp<BoolProperty>("TwoSided")?.Value == true)
            builder.WithDoubleSide(true);
        bool unlit = SurfaceShading && baseProps?.GetProp<EnumProperty>("LightingModel")?.Value.Instanced == "MLM_Unlit";
        if (unlit)
            builder.WithUnlitShader();

        // ME's glass, holo signs, light shafts, and sky traffic are unlit translucent: alpha
        // blends whose color and opacity come from their shader. When the shader can't be
        // read, the diffuse stands in with its alpha as opacity.
        string shading = "";
        bool translucent = unlit && blendMode == "BLEND_Translucent";
        bool baked = false;
        if (translucent)
        {
            builder.WithAlpha(AlphaMode.BLEND);
            baked = TranslucentSurface(builder, baseMaterial, textures, colors, scalars, out shading);
            // The original shader draws it in ORIGINAL mode, animated; the bake is the fallback.
            if (CarriedTextures != null && baseMaterial != null)
                shading += AddTranslatedShader(builder, baseMaterial, material, textures, colors, scalars, alphas, UnlitPolicies);
        }

        if (baked)
        {
            // TranslucentSurface set the color.
        }
        else if (image.HasValue)
            builder.WithChannelImage(KnownChannel.BaseColor, image.Value);
        else if (colors.TryGetValue("VSR_Colour_01", out Vector3 visor))
            builder.WithBaseColor(new Vector4(visor, 1f));
        else
            builder.WithBaseColor(new Vector4(0.6f, 0.6f, 0.6f, 1f));
        if (alphaMask)
            builder.WithAlpha(AlphaMode.MASK, 0.5f).WithDoubleSide(true);

        if (translucent)
        {
            // Noted by TranslucentSurface.
        }
        else if (unlit && blendMode == "BLEND_Additive")
        {
            // Level meshes drop additive sections, but particles (holograms) draw with them:
            // added over what's behind them, with the original shader in ORIGINAL mode.
            builder.WithAlpha(AlphaMode.BLEND);
            builder.Extras = new JsonObject { ["me_shader"] = "additive" };
            shading = ", unlit additive";
            if (CarriedTextures != null && baseMaterial != null)
                shading += AddTranslatedShader(builder, baseMaterial, material, textures, colors, scalars, alphas, UnlitPolicies);
        }
        else if (unlit)
            shading = ", unlit";
        else if (BakeShaders && material != null)
        {
            // The shader's own normal, else the material's normal map by name; roughness
            // from a specular map when it has one.
            shading = shaderNote;
            if (shaderNormal.HasValue)
            {
                builder.WithChannelImage(KnownChannel.Normal, shaderNormal.Value);
                shading += ", shader normal";
            }
            shading += AddSurfaceMaps(builder, material, textures.Values, diffuse, normals: !shaderNormal.HasValue);
        }
        else if (SurfaceShading && material != null)
        {
            shading = AddSurfaceMaps(builder, material, textures.Values, diffuse);
            if (CarriedTextures != null && baseMaterial != null)
                shading += AddTranslatedShader(builder, baseMaterial, material, textures, colors, scalars, alphas,
                    ShaderTranslator.Policy.TextureLightmap, ShaderTranslator.Policy.VertexLightmap, ShaderTranslator.Policy.Unlit,
                    ShaderTranslator.Policy.LightEnvironment);
        }

        string source = recipe != null ? $"baked ({recipe})"
            : diffuse != null ? "diffuse " + diffuse.ObjectName.Instanced
            : "no diffuse found";
        Console.WriteLine($"  material {builder.Name}: {source}" + (blendMode != "BLEND_Opaque" ? $" ({blendMode})" : "") + shading
            + (textures.Count > 0 ? $"  [params: {string.Join(", ", textures.Keys)}]" : ""));
        _materials[key] = builder;
        return builder;
    }

    // An unlit, alpha-blended stand-in (so the shape still reads as faint light anywhere the
    // extras are ignored) carrying Volumetric_1SIDED_MM's parameters: Color (linear, can exceed
    // 1 for a bloom), Opacity, Falloff (how sharply it fades toward its silhouette), and
    // FadeNear/FadeFar (fades near the camera and where it meets geometry). translated is
    // AddTranslatedShader's note, for the log.
    private static MaterialBuilder VolumetricMaterial(MaterialBuilder builder, Dictionary<string, Vector3> colors, Dictionary<string, float> scalars,
        string translated)
    {
        Vector3 color = colors.GetValueOrDefault("Color", Vector3.One);
        float Scalar(string name, float fallback) => scalars.TryGetValue(name, out float v) ? v : fallback;
        float opacity = Scalar("Opacity", 1f);

        builder.WithUnlitShader()
            .WithBaseColor(new Vector4(Vector3.Clamp(color, Vector3.Zero, Vector3.One), Math.Clamp(opacity, 0f, 1f) * 0.25f))
            .WithAlpha(AlphaMode.BLEND);
        // Kept beside the translation AddTranslatedShader may already have stored.
        var extras = builder.Extras as JsonObject ?? new JsonObject();
        extras["me_shader"] = "volumetric";
        extras["color"] = new JsonArray(color.X, color.Y, color.Z);
        extras["opacity"] = opacity;
        extras["falloff"] = Scalar("Falloff", 4f);
        extras["fade_near"] = Scalar("FadeNear", 1f);
        extras["fade_far"] = Scalar("FadeFar", 1f);
        builder.Extras = extras;
        Console.WriteLine($"  material {builder.Name}: volumetric, color {color}, opacity {opacity}{translated}");
        return builder;
    }

    // Parameter name -> texture export, -> color, and -> scalar, following the material-instance
    // parent chain so values inherited from a parent instance are found too.
    private void ReadParameters(ExportEntry material, Dictionary<string, ExportEntry> textures, Dictionary<string, Vector3> colors,
        Dictionary<string, float> scalars, Dictionary<string, float> alphas = null)
    {
        for (ExportEntry current = material; current != null;)
        {
            var props = current.GetProperties();
            foreach (StructProperty p in props.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") ?? Enumerable.Empty<StructProperty>())
            {
                string name = p.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                int uIndex = p.GetProp<ObjectProperty>("ParameterValue")?.Value ?? 0;
                if (name != null && !textures.ContainsKey(name) && Parent(current, uIndex) is ExportEntry tex)
                    textures[name] = tex;
            }
            foreach (StructProperty p in props.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") ?? Enumerable.Empty<StructProperty>())
            {
                string name = p.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                if (name != null && !colors.ContainsKey(name) && p.GetProp<StructProperty>("ParameterValue") is StructProperty c)
                {
                    colors[name] = LinearColor(c);
                    if (alphas != null)
                        alphas[name] = c.GetProp<FloatProperty>("A")?.Value ?? 1f;
                }
            }
            foreach (StructProperty p in props.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues") ?? Enumerable.Empty<StructProperty>())
            {
                string name = p.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                if (name != null && !scalars.ContainsKey(name) && p.GetProp<FloatProperty>("ParameterValue") is FloatProperty f)
                    scalars[name] = f.Value;
            }
            current = Parent(current, props.GetProp<ObjectProperty>("Parent")?.Value ?? 0);
        }
    }

    // What an entry's property references: an export of its own package, or with SharedShaders
    // (effects, whose instances parent to materials in SFXGame), the shared package's export an
    // import names. Levels keep to their own package.
    private ExportEntry Parent(ExportEntry from, int uIndex) =>
        SharedShaders ? Resolve(from.FileRef, uIndex)
        : from.FileRef.TryGetUExport(uIndex, out ExportEntry export) ? export : null;

    // Bakes an unlit translucent material from its compiled pixel shader (ShaderAnalysis):
    // color and opacity as a constant plus weighted texture channels. Samples at the surface's
    // main UVs are baked per pixel; other samples (view-dependent fake reflections, other
    // tilings) count at their texture's average. Cubemaps, the scene color, realtime
    // reflections, and other view-dependent math are left out. Colors above 1 (glows) ride
    // along as an "emission_scale" extra. Returns false when the shader or its opacity can't
    // be read; the material is then tagged "additive" (the old stand-in, which suits the
    // light shafts that end up there).
    private bool TranslucentSurface(MaterialBuilder builder, ExportEntry baseMaterial, Dictionary<string, ExportEntry> textures,
        Dictionary<string, Vector3> colors, Dictionary<string, float> scalars, out string note)
    {
        double[] Parameter(string name) =>
            colors.TryGetValue(name, out Vector3 c) ? [c.X, c.Y, c.Z, 1]
            : scalars.TryGetValue(name, out float f) ? [f]
            : null;
        double? Mean(MaterialUniformExpressionTexture slot, int channel) =>
            TextureFor(slot, baseMaterial, textures) is ExportEntry t ? MaterialBaker.ChannelMean(t, channel) : null;

        string failure = "no base material";
        ShaderAnalysis.Result shader = baseMaterial != null
            ? ShaderAnalysis.Analyze(_pcc, baseMaterial, Parameter, Mean, out failure, SharedShaders)
            : null;
        if (shader == null)
            return Additive(builder, $"shader not read: {failure}", out note);

        ShaderAnalysis.Linear[] outputs = [.. shader.Color, shader.Opacity];
        Vector4 Weights(string term) => new(
            (float)outputs[0].Terms.GetValueOrDefault(term), (float)outputs[1].Terms.GetValueOrDefault(term),
            (float)outputs[2].Terms.GetValueOrDefault(term), (float)outputs[3].Terms.GetValueOrDefault(term));

        // The main UVs: the first-UV-set tiling that carries the most weight.
        var main = shader.Samples
            .Where(s => s.Value.Uv0)
            .GroupBy(s => (s.Value.ScaleU, s.Value.ScaleV))
            .OrderByDescending(g => g.Sum(s => Weights(s.Key).Length()))
            .FirstOrDefault();

        var constant = new Vector4((float)outputs[0].Constant, (float)outputs[1].Constant, (float)outputs[2].Constant, (float)outputs[3].Constant);
        var terms = new List<MaterialBaker.ChannelTerm>();
        var used = new HashSet<string>();
        var averaged = new List<string>();
        foreach (var (name, sample) in shader.Samples)
        {
            ExportEntry texture = sample.Slot < (shader.Textures?.Length ?? 0) ? TextureFor(shader.Textures[sample.Slot], baseMaterial, textures) : null;
            if (texture == null)
                continue;
            used.Add(name);
            if (main != null && main.Any(m => m.Key == name))
                terms.Add(new MaterialBaker.ChannelTerm(texture, sample.Channel, Weights(name)));
            else
            {
                constant += Weights(name) * MaterialBaker.ChannelMean(texture, sample.Channel);
                averaged.Add(name);
            }
        }
        var dropped = outputs.SelectMany(o => o.Terms.Keys).Distinct().Where(t => !used.Contains(t)).ToList();
        string summary = $"color ({outputs[0]} | {outputs[1]} | {outputs[2]}), opacity {outputs[3]}";

        // Opacity made only of things left out (scene depth fades, view angles) would bake as
        // fully transparent.
        if (constant.W <= 0.001f && !terms.Any(t => t.Weights.W != 0f))
            return Additive(builder, "opacity not read: " + summary, out note);

        float scale = 1f;
        MemoryImage? image = terms.Count > 0 ? MaterialBaker.BakeTranslucent(terms, constant, out scale) : null;
        if (image.HasValue)
        {
            builder.WithChannelImage(KnownChannel.BaseColor, image.Value);
            var (u, v) = main.Key;
            if (u != 1f || v != 1f)
                builder.UseChannel(KnownChannel.BaseColor).Texture.WithTransform(Vector2.Zero, new Vector2(u, v));
        }
        else
        {
            Vector3 color = Vector3.Max(new Vector3(constant.X, constant.Y, constant.Z), Vector3.Zero);
            scale = MathF.Max(1f, MathF.Max(color.X, MathF.Max(color.Y, color.Z)));
            builder.WithBaseColor(new Vector4(color / scale, Math.Clamp(constant.W, 0f, 1f)));
        }
        if (scale > 1f)
            builder.Extras = new JsonObject { ["me_shader"] = "translucent", ["emission_scale"] = scale };

        note = ", unlit translucent: " + summary
            + (image.HasValue && (main.Key.ScaleU != 1f || main.Key.ScaleV != 1f) ? $", tiling {main.Key.ScaleU}x{main.Key.ScaleV}" : "")
            + (image.HasValue && main.Any(s => s.Value.Panning) ? ", panning ignored" : "")
            + (scale > 1f ? $", scale {scale:0.##}" : "")
            + (averaged.Count > 0 ? $", averaged {string.Join(" ", averaged)}" : "")
            + (dropped.Count > 0 ? $", dropped {string.Join(" ", dropped)}" : "");
        return true;
    }

    private static bool Additive(MaterialBuilder builder, string reason, out string note)
    {
        builder.Extras = new JsonObject { ["me_shader"] = "additive" };
        note = $", unlit additive ({reason})";
        return false;
    }

    // A shader map's default textures: Indices are entries of Package. A static permutation's
    // list belongs to the instance's package, not its base material's; resolved against the
    // base's (BIOC_Materials for the APL master material), the props' OpacityMask default
    // came back empty and sampled black, and their cutouts discarded every pixel.
    private sealed record Defaults(int[] Indices, IMEPackage Package)
    {
        public static Defaults Of(ExportEntry baseMaterial) =>
            new(ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource.UniformExpressionTextures, baseMaterial.FileRef);

        // The entry at a texture index, as the package names it (an import stays an import).
        public IEntry Entry(int index) => index < Indices.Length ? Package.GetEntry(Indices[index]) : null;

        // The texture at a texture index: the package's own, or the shared package's an import names.
        public ExportEntry Texture(int index) => index < Indices.Length ? TextureCache.Resolve(Package, Indices[index]) : null;
    }

    // The texture behind a shader texture slot: a material instance's value for a texture
    // parameter, or else the material's own texture (from defaults, the shader map's list, when
    // given). Null for render targets and textures stored in another package.
    private ExportEntry TextureFor(MaterialUniformExpressionTexture expression, ExportEntry baseMaterial, Dictionary<string, ExportEntry> textures,
        Defaults defaults = null)
    {
        if (expression is MaterialUniformExpressionTextureParameter parameter
            && textures.TryGetValue(parameter.ParameterName.Instanced, out ExportEntry overridden))
            return overridden;
        defaults ??= Defaults.Of(baseMaterial);
        return defaults.Texture(expression.TextureIndex) is ExportEntry texture
            && texture.ClassName is "Texture2D" or "TextureFlipBook" ? texture : null;
    }

    // Packages the game always has loaded, which hold shared effect textures that other
    // packages import: ME1's BIOC_Materials (the holograms', distortion maps); ME3's SFXGame
    // and Startup (BioVFX_Z_TEXTURES: the powers' gradients and noise), and ME2's SFXGame.
    private static readonly Dictionary<MEGame, string[]> SharedPackages = new()
    {
        [MEGame.LE1] = ["BIOC_Materials.pcc"],
        [MEGame.LE2] = ["SFXGame.pcc"],
        [MEGame.LE3] = ["SFXGame.pcc", "Startup.pcc"],
    };

    // An export of the package, or the shared package export that an import names.
    internal static ExportEntry Resolve(IMEPackage pcc, int uIndex)
    {
        if (pcc.TryGetUExport(uIndex, out ExportEntry export))
            return export;
        if (pcc.GetEntry(uIndex) is not ImportEntry import || pcc.FilePath == null)
            return null;
        foreach (string file in SharedPackages.GetValueOrDefault(pcc.Game, ["BIOC_Materials.pcc"]))
        {
            string path = Path.Combine(Path.GetDirectoryName(pcc.FilePath)!, file);
            if (EntryScope.OpenShared(path)?.FindExport(import.InstancedFullPath) is ExportEntry found)
                return found;
        }
        return null;
    }

    // Adds the material's normal map and a roughness map baked from its specular map, picked
    // by texture name like the diffuse. Returns a note for the export log.
    private string AddSurfaceMaps(MaterialBuilder builder, ExportEntry material, IEnumerable<ExportEntry> parameterTextures,
        ExportEntry diffuse, bool normals = true)
    {
        List<ExportEntry> candidates = CandidateTextures(material, parameterTextures);
        string note = "";

        ExportEntry normal = normals ? PickMap(candidates, NormalName, diffuse) : null;
        MemoryImage? normalImage = normal != null ? BakedMap("normal", normal, pixels => MaterialBaker.BakeNormal(pixels)) : null;
        if (normalImage.HasValue)
        {
            builder.WithChannelImage(KnownChannel.Normal, normalImage.Value);
            note += ", normal " + normal.ObjectName.Instanced;
        }

        ExportEntry specular = PickMap(candidates, SpecularName, diffuse);
        float roughness = 0f;
        MemoryImage? roughnessImage = specular != null
            ? BakedMap("roughness", specular, pixels => MaterialBaker.BakeRoughness(pixels, out roughness))
            : null;
        if (roughnessImage.HasValue)
        {
            // The texture holds the values; the factors only scale them.
            builder.WithChannelImage(KnownChannel.MetallicRoughness, roughnessImage.Value);
            builder.WithMetallicRoughness(0f, 1f);
            note += $", roughness from {specular.ObjectName.Instanced}" + (roughness > 0f ? $" (avg {roughness:0.00})" : "");
        }
        return note;
    }

    // Translates a lit material's original base-pass shaders, one per lightmap policy
    // (ShaderTranslator), into the material's "me_material" extras, along with what they read:
    //   texture_<n>: the key of a carried texture (CarriedTextures), decoded as-is
    //   cube_<n>:    the key of a carried cubemap, whose faces are carried as "<key>#<face>"
    //   pixel_vector_<n> / pixel_scalar_<n>: the material's uniform values
    //   animated: uniforms that change over time (panners, flicker), by the same names, as
    //             Godot shading language over TIME, which the loader uses instead
    // Lit materials translate their two lightmap policies, unlit ones the unlit policy. The
    // extras join any the material already has. Returns a note for the export log.
    private string AddTranslatedShader(MaterialBuilder builder, ExportEntry baseMaterial, ExportEntry material, Dictionary<string, ExportEntry> textures,
        Dictionary<string, Vector3> colors, Dictionary<string, float> scalars, Dictionary<string, float> alphas, params ShaderTranslator.Policy[] policies)
    {
        ShaderCache cache = SharedShaders ? ShaderAnalysis.CacheWithShared(_pcc) : ShaderAnalysis.CacheOf(_pcc);
        string staticNote = null;
        Defaults defaults = null;
        MaterialShaderMap map = null;
        if (cache != null)
        {
            map = ShaderAnalysis.ShaderMapOf(cache, material, baseMaterial, out int[] indices, out IMEPackage package, out staticNote);
            defaults = new Defaults(indices, package);
        }
        if (map == null)
            return ", no shader to translate";

        var slotTextures = new Dictionary<int, ExportEntry>();
        ExportEntry Slot(int slot) =>
            slotTextures.TryGetValue(slot, out ExportEntry t) ? t
            : slotTextures[slot] = slot < (map.Uniform2DTextureExpressions?.Length ?? 0) ? TextureFor(map.Uniform2DTextureExpressions[slot], baseMaterial, textures, defaults) : null;
        var cubeTextures = new Dictionary<int, ExportEntry>();
        ExportEntry Cube(int slot) =>
            cubeTextures.TryGetValue(slot, out ExportEntry t) ? t
            : cubeTextures[slot] = slot < (map.UniformCubeTextureExpressions?.Length ?? 0) ? CubeFor(map.UniformCubeTextureExpressions[slot], baseMaterial, textures, defaults) : null;
        // Textures that aren't in the package (another package's, or a realtime reflection's
        // render target) sample black, as the translucent bake leaves them out.
        ShaderTranslator.TextureInfo SlotInfo(int slot) =>
            Slot(slot) is ExportEntry texture ? Info(texture)
            : IsRenderTarget(map.Uniform2DTextureExpressions[slot], baseMaterial, textures, defaults) ? new ShaderTranslator.TextureInfo(false, false, true)
            : Info(null) with { Black = true };
        ShaderTranslator.TextureInfo Info(ExportEntry texture)
        {
            var props = texture?.GetProperties();
            return new ShaderTranslator.TextureInfo(
                props?.GetProp<BoolProperty>("SRGB")?.Value ?? true,
                props?.GetProp<EnumProperty>("AddressX")?.Value.Instanced is not ("TA_Clamp"));
        }
        ShaderTranslator.TextureInfo CubeInfo(ExportEntry cube) => Info(CubeFaces(cube)?.FirstOrDefault(f => f != null));

        var extras = new JsonObject();
        var used = new { Textures = new SortedSet<int>(), Cubes = new SortedSet<int>(), Vectors = new SortedSet<int>(), Scalars = new SortedSet<int>(),
            Cutouts = new SortedSet<int>() };
        var notes = new List<string>();
        if (staticNote != null)
            notes.Add(staticNote);
        foreach (ShaderTranslator.Policy policy in policies)
        {
            string type = ShaderTranslator.ShaderType(policy);
            var reference = map.MeshShaderMaps
                .Where(m => m.VertexFactoryType.Instanced == "FLocalVertexFactory")
                .SelectMany(m => m.Shaders)
                .FirstOrDefault(s => s.Key.Instanced == type).Value;
            string key = ShaderTranslator.Key(policy);
            if (reference == null || !cache.Shaders.TryGetValue(reference.Id, out Shader shader))
            {
                // A lit material's unlit translation is only for particles, and its light
                // environment one only for meshes ME1 didn't lightmap; most have neither. Only
                // materials that distort have a distortion pass.
                if (!(policy is ShaderTranslator.Policy.Unlit or ShaderTranslator.Policy.LightEnvironment
                        && policies.Contains(ShaderTranslator.Policy.TextureLightmap))
                    && policy != ShaderTranslator.Policy.Distortion)
                    notes.Add($"{key}: no shader");
                continue;
            }
            string asm = ShaderAnalysis.Disassemble(shader.ShaderByteCode);
            string failure = "no disassembly";
            ShaderTranslator.Translation translation = asm != null
                ? ShaderTranslator.Translate(asm, policy, SlotInfo, slot => CubeInfo(Cube(slot)), out failure)
                : null;
            if (translation == null)
            {
                notes.Add($"{key}: not translated ({failure})");
                continue;
            }
            extras[key] = new JsonObject { ["uniforms"] = translation.Uniforms, ["code"] = translation.Code };
            used.Textures.UnionWith(translation.Textures);
            used.Cubes.UnionWith(translation.Cubes);
            used.Vectors.UnionWith(translation.Vectors);
            used.Scalars.UnionWith(translation.Scalars);
            used.Cutouts.UnionWith(translation.CutoutTextures);
        }
        string[] translatedKeys = policies.Select(ShaderTranslator.Key).Where(extras.ContainsKey).ToArray();
        if (translatedKeys.Length == 0)
            return ", shader not translated (" + string.Join("; ", notes) + ")";

        var parameters = new JsonObject();
        foreach (int slot in used.Textures)
        {
            ExportEntry texture = Slot(slot);
            if (texture != null && Carry(texture.InstancedFullPath, texture))
                parameters[$"texture_{slot}"] = texture.InstancedFullPath;
            else if (texture == null && IsRenderTarget(map.Uniform2DTextureExpressions[slot], baseMaterial, textures, defaults))
                notes.Add($"texture {slot} is a realtime reflection (black)");
            else
                notes.Add($"texture {slot} missing" + (texture == null ? " (black)" : ""));
        }
        foreach (int slot in used.Cubes)
        {
            ExportEntry cube = Cube(slot);
            ExportEntry[] faces = CubeFaces(cube);
            bool carried = faces != null && faces.All(f => f != null);
            for (int face = 0; carried && face < 6; face++)
                carried = Carry($"{cube.InstancedFullPath}#{face}", faces[face]);
            if (carried)
                parameters[$"cube_{slot}"] = cube.InstancedFullPath;
            else
                notes.Add($"cubemap {slot} missing");
        }

        double[] Parameter(string name) =>
            colors.TryGetValue(name, out Vector3 c) ? [c.X, c.Y, c.Z, alphas.GetValueOrDefault(name, 1f)]
            : scalars.TryGetValue(name, out float f) ? [f]
            : null;
        // Values at time 0, and the GLSL of those that change.
        var animated = new JsonObject();
        bool frozen = false;
        void Uniform(string name, MaterialUniformExpression[] expressions, int slot, bool vector)
        {
            MaterialUniformExpression expression = slot < (expressions?.Length ?? 0) ? expressions[slot] : null;
            // A mesh particle's color reaches its material through this parameter (white on
            // anything else), which the Godot side reads as in_color.
            if (expression is MaterialUniformExpressionVectorParameter { ParameterName.Instanced: "MeshEmitterVertexColor" })
            {
                parameters[name] = new JsonArray(1, 1, 1, 1);
                animated[name] = "in_color";
                return;
            }
            // A flipbook's frame offset (the shader scales the UVs to one frame itself).
            if (vector && expression is MaterialUniformExpressionTexture flipBook && expression is not MaterialUniformExpressionTextureParameter
                && FlipBookOffset(flipBook, baseMaterial, defaults) is string offset)
            {
                parameters[name] = new JsonArray(0, 0, 0, 0);
                animated[name] = offset;
                return;
            }
            bool varies = false;
            double[] value = expression != null ? ShaderAnalysis.Evaluate(expression, Parameter, ref varies) : null;
            if (value == null && expression != null)
                notes.Add($"{name} unknown ({expression.GetType().Name}, 0)");
            value ??= [0, 0, 0, 0];
            string Component(string[] values, int i) => values[Math.Min(i, values.Length - 1)];
            if (vector)
                parameters[name] = new JsonArray(Enumerable.Range(0, 4).Select(i => (JsonNode)value[Math.Min(i, value.Length - 1)]).ToArray());
            else
                parameters[name] = value[0];
            if (!varies)
                return;
            string[] glsl = ShaderAnalysis.Glsl(expression, Parameter);
            if (glsl == null)
                frozen = true;
            else
                animated[name] = vector ? $"vec4({string.Join(", ", Enumerable.Range(0, 4).Select(i => Component(glsl, i)))})" : glsl[0];
        }
        foreach (int slot in used.Vectors)
            Uniform($"pixel_vector_{slot}", map.UniformPixelVectorExpressions, slot, true);
        foreach (int slot in used.Scalars)
            Uniform($"pixel_scalar_{slot}", map.UniformPixelScalarExpressions, slot, false);
        if (animated.Count > 0)
        {
            extras["animated"] = animated;
            notes.Add($"{animated.Count} animated");
        }
        if (frozen)
            notes.Add("some animation ignored");
        extras["parameters"] = parameters;
        // A masked material's opacity mask: the carried textures whose alpha its discards test,
        // and UE3's clip value, so the game can rebuild their mips with that threshold to keep
        // the cutout's coverage.
        string[] cutouts = used.Cutouts.Select(slot => $"texture_{slot}").Where(parameters.ContainsKey).ToArray();
        if (cutouts.Length > 0)
        {
            float clip = baseMaterial.GetProperties().GetProp<FloatProperty>("OpacityMaskClipValue")?.Value ?? 0.3333f;
            extras["cutout"] = new JsonObject
            {
                ["textures"] = new JsonArray(cutouts.Select(c => (JsonNode)c).ToArray()),
                ["threshold"] = clip,
            };
            notes.Add($"cutout {string.Join(", ", cutouts)} at {clip:0.####}");
        }
        // A lit material's unlit translation is only for particles, even when its lightmap
        // translations failed (the game tells them apart by this flag).
        if (policies.Any(p => p is not (ShaderTranslator.Policy.Unlit or ShaderTranslator.Policy.Distortion)))
            extras["lit"] = true;
        // How the unlit translation blends: BLEND_Translucent (alpha) or BLEND_Additive.
        extras["blend"] = baseMaterial.GetProperties().GetProp<EnumProperty>("BlendMode")?.Value.Instanced ?? "BLEND_Opaque";
        var all = builder.Extras as JsonObject ?? new JsonObject();
        all["me_material"] = extras;
        builder.Extras = all;

        return $", translated ({string.Join(", ", translatedKeys)}"
            + (used.Cubes.Count > 0 ? $", {used.Cubes.Count} cubemap(s)" : "")
            + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + ")";
    }

    // A TextureFlipBook's current frame offset as GLSL over TIME, as UE3's UTextureFlipBook
    // ticks it: FrameRate frames a second through HorizontalImages x VerticalImages, looping, in
    // the order FBMethod gives (rows or columns, from one of the four corners). Null when the
    // slot's texture isn't a flipbook.
    private string FlipBookOffset(MaterialUniformExpressionTexture expression, ExportEntry baseMaterial, Defaults defaults)
    {
        defaults ??= Defaults.Of(baseMaterial);
        if (defaults.Texture(expression.TextureIndex) is not ExportEntry book || book.ClassName != "TextureFlipBook")
            return null;
        var props = book.GetProperties();
        int columns = Math.Max(props.GetProp<IntProperty>("HorizontalImages")?.Value ?? 1, 1);
        int rows = Math.Max(props.GetProp<IntProperty>("VerticalImages")?.Value ?? 1, 1);
        float rate = props.GetProp<FloatProperty>("FrameRate")?.Value ?? 4f;
        string method = props.GetProp<EnumProperty>("FBMethod")?.Value.Instanced ?? "TFBM_UL_ROW";
        string F(double v) => v.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
        // The frame, then its position along and across the reading direction.
        string frame = $"mod(floor(TIME * {F(rate)}), {F(columns * rows)})";
        bool byRow = !method.EndsWith("_COL");
        string along = byRow ? $"mod({frame}, {F(columns)})" : $"mod({frame}, {F(rows)})";
        string across = byRow ? $"floor({frame} / {F(columns)})" : $"floor({frame} / {F(rows)})";
        string column = byRow ? along : across, row = byRow ? across : along;
        // Starting from the right or the bottom counts that axis backwards.
        if (method.StartsWith("TFBM_UR") || method.StartsWith("TFBM_LR"))
            column = $"({F(columns - 1)} - {column})";
        if (method.StartsWith("TFBM_LL") || method.StartsWith("TFBM_LR"))
            row = $"({F(rows - 1)} - {row})";
        return $"vec4({column} / {F(columns)}, {row} / {F(rows)}, 0.0, 0.0)";
    }

    // Decodes a texture into CarriedTextures under the key, once. False when it can't be read.
    private bool Carry(string key, ExportEntry texture)
    {
        if (CarriedTextures.TryGetValue(key, out MemoryImage? carried))
            return carried.HasValue;
        MemoryImage? image = Decode(texture);
        CarriedTextures[key] = image;
        return image.HasValue;
    }

    // Whether a shader texture slot holds a render target (a realtime reflection's).
    private bool IsRenderTarget(MaterialUniformExpressionTexture expression, ExportEntry baseMaterial, Dictionary<string, ExportEntry> textures,
        Defaults defaults = null)
    {
        if (expression is MaterialUniformExpressionTextureParameter parameter
            && textures.TryGetValue(parameter.ParameterName.Instanced, out ExportEntry overridden))
            return overridden.ClassName.Contains("RenderTarget");
        defaults ??= Defaults.Of(baseMaterial);
        return defaults.Entry(expression.TextureIndex) is IEntry texture && texture.ClassName.Contains("RenderTarget");
    }

    // The cubemap behind a shader cube slot, like TextureFor.
    private ExportEntry CubeFor(MaterialUniformExpressionTexture expression, ExportEntry baseMaterial, Dictionary<string, ExportEntry> textures,
        Defaults defaults = null)
    {
        if (expression is MaterialUniformExpressionTextureParameter parameter
            && textures.TryGetValue(parameter.ParameterName.Instanced, out ExportEntry overridden))
            return overridden.ClassName == "TextureCube" ? overridden : null;
        defaults ??= Defaults.Of(baseMaterial);
        return defaults.Texture(expression.TextureIndex) is ExportEntry texture && texture.ClassName == "TextureCube" ? texture : null;
    }

    // A cubemap's six faces in Direct3D order (+X, -X, +Y, -Y, +Z, -Z), or null.
    private ExportEntry[] CubeFaces(ExportEntry cube)
    {
        if (cube == null)
            return null;
        var props = cube.GetProperties();
        return new[] { "FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ" }
            .Select(name => _pcc.TryGetUExport(props.GetProp<ObjectProperty>(name)?.Value ?? 0, out ExportEntry face) ? face : null)
            .ToArray();
    }

    // A map baked from a texture (decoded at the capped mip), once per texture and kind.
    private MemoryImage? BakedMap(string kind, ExportEntry texture, Func<MaterialBaker.Pixels, MemoryImage?> bake)
    {
        string key = kind + ":" + texture.InstancedFullPath;
        if (_images.TryGetValue(key, out MemoryImage? cached))
            return cached;
        MaterialBaker.Pixels source = DecodePixels(texture);
        MemoryImage? baked = source != null ? bake(source) : null;
        _images[key] = baked;
        return baked;
    }

    private static readonly Regex NormalName = new(@"(norm|_n$|_nrm)", RegexOptions.IgnoreCase);
    private static readonly Regex SpecularName = new(@"(spec|_s$)", RegexOptions.IgnoreCase);
    private static readonly Regex NotSurfaceMapName = new(@"(mask|msk|overlay|detail)", RegexOptions.IgnoreCase);

    // The likeliest map of a kind, preferring one named like the diffuse (STA60_FLOOR01_Diff
    // -> STA60_FLOOR01_Norm) over shared detail and overlay maps.
    private static ExportEntry PickMap(List<ExportEntry> candidates, Regex kind, ExportEntry diffuse)
    {
        var maps = candidates.Where(t => kind.IsMatch(t.ObjectName.Instanced) && !NotSurfaceMapName.IsMatch(t.ObjectName.Instanced)).ToList();
        string diffuseName = diffuse?.ObjectName.Instanced;
        int cut = diffuseName?.LastIndexOf('_') ?? -1;
        if (cut > 0)
        {
            string prefix = diffuseName[..cut];
            ExportEntry matching = maps.FirstOrDefault(t => t.ObjectName.Instanced.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
                return matching;
        }
        return maps.FirstOrDefault();
    }

    // The blend mode of a section's material ("BLEND_Opaque" when unset or unresolvable).
    public string MaterialName(int materialUIndex) =>
        materialUIndex != 0 ? _pcc.GetEntry(materialUIndex)?.ObjectName.Instanced : null;

    public string LightingModel(int materialUIndex)
    {
        if (materialUIndex == 0 || !_pcc.TryGetUExport(materialUIndex, out ExportEntry material))
            return "MLM_Phong";
        return BaseMaterial(material)?.GetProperties().GetProp<EnumProperty>("LightingModel")?.Value.Instanced ?? "MLM_Phong";
    }

    public string BlendMode(int materialUIndex)
    {
        if (materialUIndex == 0 || !_pcc.TryGetUExport(materialUIndex, out ExportEntry material))
            return "BLEND_Opaque";
        return BaseMaterial(material)?.GetProperties().GetProp<EnumProperty>("BlendMode")?.Value.Instanced ?? "BLEND_Opaque";
    }

    // Follows a material instance's parents up to the Material it derives from.
    private ExportEntry BaseMaterial(ExportEntry material)
    {
        for (ExportEntry current = material; current != null;)
        {
            // DecalMaterial is a Material subclass (the Chora's Den sign).
            if (current.ClassName is "Material" or "DecalMaterial")
                return current;
            current = Parent(current, current.GetProperties().GetProp<ObjectProperty>("Parent")?.Value ?? 0);
        }
        return null;
    }

    private static readonly Regex NotDiffuseName = new(@"(norm|_n$|_nrm|spec|_s$|mask|_m$|emis|_e$|glow|cube|_env|rough|detail|noise)", RegexOptions.IgnoreCase);
    private static readonly Regex DiffuseName = new(@"(diff|_d$|_dif|_col|albedo|base)", RegexOptions.IgnoreCase);

    // The likeliest diffuse among a material's parameter textures and the textures its base
    // material samples directly, judged by name: "_Diff"/"_D"-style names win, and normal,
    // specular, mask, and emissive maps are never picked.
    private ExportEntry GuessDiffuseTexture(ExportEntry material, IEnumerable<ExportEntry> parameterTextures)
    {
        List<ExportEntry> candidates = CandidateTextures(material, parameterTextures);
        var usable = candidates.Where(t => !NotDiffuseName.IsMatch(t.ObjectName.Instanced)).ToList();
        return usable.FirstOrDefault(t => DiffuseName.IsMatch(t.ObjectName.Instanced)) ?? usable.FirstOrDefault();
    }

    // A material's parameter textures plus every texture its base material samples directly.
    private List<ExportEntry> CandidateTextures(ExportEntry material, IEnumerable<ExportEntry> parameterTextures)
    {
        var candidates = new List<ExportEntry>(parameterTextures);
        ExportEntry baseMaterial = BaseMaterial(material);
        if (baseMaterial != null)
        {
            try
            {
                var binary = ObjectBinary.From<Material>(baseMaterial);
                foreach (int uIndex in binary.SM3MaterialResource?.UniformExpressionTextures ?? [])
                {
                    if (_pcc.TryGetUExport(uIndex, out ExportEntry tex) && tex.ClassName == "Texture2D")
                        candidates.Add(tex);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  could not read {baseMaterial.InstancedFullPath}: {ex.Message}");
            }
        }
        return candidates;
    }

    private static Vector3 LinearColor(StructProperty c) => new Vector3(
        c.GetProp<FloatProperty>("R")?.Value ?? 0f,
        c.GetProp<FloatProperty>("G")?.Value ?? 0f,
        c.GetProp<FloatProperty>("B")?.Value ?? 0f);

    // A texture as embedded: ME1's own data and mips from the largest mip within
    // MaxTextureSize, or that mip encoded by us when its format has no DDS mapping. Once per
    // texture.
    private MemoryImage? Decode(ExportEntry texture)
    {
        if (_images.TryGetValue(texture.InstancedFullPath, out MemoryImage? cached))
            return cached;
        MemoryImage? image = null;
        try
        {
            var texture2D = new Texture2D(texture);
            // Mips are listed largest first; some are empty or point at data a smaller mip
            // doesn't need, so the next one down is tried.
            foreach (Texture2DMipInfo mip in EmbeddableMips(texture2D))
            {
                image = DdsTextures.Original(texture, mip.width);
                if (image == null && MaterialBaker.Pixels.MipBgra(texture, texture2D, mip) is byte[] bgra)
                    image = DdsTextures.Encode(bgra, mip.width, mip.height, normalMap: false);
                if (image != null)
                    break;
            }
            if (image == null)
                Console.Error.WriteLine($"  could not decode {texture.InstancedFullPath}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"  could not decode {texture.InstancedFullPath}: {ex.Message}");
        }
        _images[texture.InstancedFullPath] = image;
        return image;
    }

    // A texture's pixels at the mip Decode embeds, for the maps baked from it.
    private MaterialBaker.Pixels DecodePixels(ExportEntry texture)
    {
        try
        {
            var texture2D = new Texture2D(texture);
            foreach (Texture2DMipInfo mip in EmbeddableMips(texture2D))
            {
                if (MaterialBaker.Pixels.FromMip(texture, texture2D, mip) is MaterialBaker.Pixels pixels)
                    return pixels;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"  could not decode {texture.InstancedFullPath}: {ex.Message}");
        }
        return null;
    }

    // The non-empty mips within MaxTextureSize (every one when uncapped), largest first; the top
    // one when none fits (a texture cooked without mips, such as the Wards' 4096 px sky).
    private IEnumerable<Texture2DMipInfo> EmbeddableMips(Texture2D texture2D)
    {
        var mips = texture2D.Mips.Where(m => m.storageType != StorageTypes.empty).ToList();
        var fitting = mips.Where(m => MaxTextureSize <= 0 || (m.width <= MaxTextureSize && m.height <= MaxTextureSize)).ToList();
        return fitting.Count > 0 ? fitting : mips.Take(1);
    }
}
