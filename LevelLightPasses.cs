using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Scenes;

namespace LexExport;

// Lights a level draws live instead of baking into its lightmaps (--light-passes; LE3's MP
// maps: Firebase White's sun and floodlights, without which every surface they reach is black).
//
// UE3 bakes a static light's direct light into the lightmaps of the primitives it reaches (the
// lightmap's LightGuids list it by its LightmapGuid). Any other light that reaches a lightmapped
// primitive is drawn per frame, in an additive pass of its own over the base pass
// (TLightPixelShader<light policy, shadowing policy>): with a static shadow map where Lightmass
// found something in its way (a ShadowMap2D on the component, named by the light's LightGuid:
// FShadowTexturePolicy), and unshadowed where it found nothing (FNoStaticShadowingPolicy).
// Lights it found entirely blocked go in the component's IrrelevantLights. A light reaches a
// primitive when their lighting channels share one and the primitive's bounds are within the
// light's reach (its radius and, for a spot, its cone).
//
// The exporter settles that for every lightmapped instance once all packages are read
// (AssignLightPasses), and writes it as the instance's "me_light_passes": one entry per light,
// { light: its LightGuid, page: the shadow map's page (-1: unshadowed), scale_bias }. The shadow
// maps' G8 atlases ride along like the lightmap pages (LightmapPages/Page_<n>_shadow), and each
// light carries its GUID, linear color, and cone (AddLight). Lit materials carry the passes'
// translations (TextureCache.LightPasses). Vertex shadow maps (ShadowMap1D, a shadow factor per
// vertex, FShadowVertexBufferPolicy) aren't carried yet; their lights are left out of those
// instances and counted in the log.
public partial class LevelBuilder
{
    // A light that can be drawn live: its GUIDs, the pass key's light kind, channels, and reach in
    // glTF space (meters; a directional light reaches everything).
    private sealed record LiveLight(string Guid, string LightmapGuid, string Kind, HashSet<string> Channels,
        Vector3 Position, float Radius, Vector3 Direction, float CosOuter);

    // A lightmapped instance's node, and what decides which lights it draws live.
    private sealed record LitInstance(NodeBuilder Node, HashSet<string> Channels, Vector3 Center, float Radius,
        HashSet<string> Skip, Dictionary<string, JsonObject> ShadowMaps, HashSet<string> VertexShadowed);

    private readonly List<LiveLight> _liveLights = new();
    private readonly List<LitInstance> _litInstances = new();
    // Shadow map atlas pages by package and texture (numbered on their own beside the lightmaps').
    private readonly Dictionary<string, int> _shadowPages = new(StringComparer.OrdinalIgnoreCase);
    private int _shadowPageCount, _shadowedPasses, _unshadowedPasses, _vertexShadowedLeftOut, _passInstances;

    // A light from AddLight (position and the direction it shines in glTF space, radius in Unreal units).
    private void RecordLiveLight(ExportEntry component, PropertyCollection props, Vector3 position, Vector3 forward,
        float radius, float brightness)
    {
        string kind = component.ClassName switch
        {
            "DirectionalLightComponent" or "DominantDirectionalLightComponent" => "directional",
            "SpotLightComponent" or "DominantSpotLightComponent" => "spot",
            "PointLightComponent" or "DominantPointLightComponent" => "point",
            _ => null,
        };
        string guid = GuidText(props.GetProp<StructProperty>("LightGuid"));
        if (kind == null || guid == null || brightness <= 0f)
            return;
        // A spot's cone, for the reach test: where it shines (glTF axes) and its outer angle.
        float outer = Math.Clamp(props.GetProp<FloatProperty>("OuterConeAngle")?.Value ?? 44f, 0.001f, 89f) * MathF.PI / 180f;
        _liveLights.Add(new LiveLight(guid, GuidText(props.GetProp<StructProperty>("LightmapGuid")), kind,
            ChannelSet(component, LightDefaultChannels), position, radius * UnrealSpace.UnitsToMeters,
            forward, MathF.Cos(outer)));
    }

    // A lightmapped instance, with what decides its live lights: its channels, bounds, the lights
    // its lightmap holds or Lightmass found blocked, and its shadow maps by light.
    private void RecordLitInstance(IMEPackage pcc, ExportEntry component, NodeBuilder node, MeshGeometry geometry,
        Matrix4x4 world, LightMap lightMap)
    {
        BoxSphereBounds bounds = geometry.Bounds.TransformBy(world);
        HashSet<string> skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid baked in LightGuidsOf(lightMap))
            skip.Add(GuidText(baked));
        PropertyCollection props = component.GetProperties();
        foreach (StructProperty irrelevant in props.GetProp<ArrayProperty<StructProperty>>("IrrelevantLights") ?? Enumerable.Empty<StructProperty>())
        {
            if (GuidText(irrelevant) is string guid)
                skip.Add(guid);
        }

        Dictionary<string, JsonObject> shadowMaps = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> vertexShadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        StaticMeshComponentLODInfo lod = null;
        try
        {
            StaticMeshComponent binary = ObjectBinary.From<StaticMeshComponent>(component);
            lod = binary.LODData.Length > 0 ? binary.LODData[0] : null;
        }
        catch (Exception)
        {
            // No readable LOD data: no shadow maps.
        }
        foreach (int uIndex in lod?.ShadowMaps ?? [])
        {
            if (!pcc.TryGetUExport(uIndex, out ExportEntry export) || export.ClassName != "ShadowMap2D")
                continue;
            PropertyCollection map = export.GetProperties();
            // A distance-field shadow map needs its own pass (FSignedDistanceFieldShadowTexturePolicy).
            if (map.GetProp<BoolProperty>("bIsShadowFactorTexture")?.Value == false)
                continue;
            string light = GuidText(map.GetProp<StructProperty>("LightGuid"));
            int texture = map.GetProp<ObjectProperty>("Texture")?.Value ?? 0;
            int page = texture != 0 && light != null ? ShadowPage(pcc, texture) : -1;
            if (page < 0)
                continue;
            Vector2 scale = Vector2Prop(map.GetProp<StructProperty>("CoordinateScale"));
            Vector2 bias = Vector2Prop(map.GetProp<StructProperty>("CoordinateBias"));
            shadowMaps[light] = new JsonObject
            {
                ["light"] = light,
                ["page"] = page,
                ["scale_bias"] = new JsonArray(scale.X, scale.Y, bias.X, bias.Y),
            };
        }
        foreach (int uIndex in lod?.ShadowVertexBuffers ?? [])
        {
            if (pcc.TryGetUExport(uIndex, out ExportEntry export) && export.ClassName == "ShadowMap1D")
            {
                try
                {
                    vertexShadowed.Add(GuidText(ObjectBinary.From<ShadowMap1D>(export).LightGuid));
                }
                catch (Exception)
                {
                    // Unreadable: its light is simply drawn unshadowed.
                }
            }
        }
        _litInstances.Add(new LitInstance(node, PrimitiveChannels(component, staticLighting: true).Select(c => c.GetValue<string>()).ToHashSet(),
            UnrealSpace.Position(bounds.Origin), bounds.SphereRadius * UnrealSpace.UnitsToMeters, skip, shadowMaps, vertexShadowed));
    }

    private static IEnumerable<Guid> LightGuidsOf(LightMap lightMap) => lightMap switch
    {
        LightMap_2D two => two.LightGuids ?? [],
        LightMap_1D one => one.LightGuids ?? [],
        _ => [],
    };

    // Which live lights each lightmapped instance draws, written into its node's extras.
    private void AssignLightPasses()
    {
        // LEXEXPORT_TRACE_LIGHTS=<node name part>: why each light reaches those instances or not.
        string trace = Environment.GetEnvironmentVariable("LEXEXPORT_TRACE_LIGHTS");
        foreach (LitInstance instance in _litInstances)
        {
            JsonArray passes = new JsonArray();
            bool traced = !string.IsNullOrEmpty(trace) && instance.Node.Name.Contains(trace, StringComparison.OrdinalIgnoreCase);
            if (traced)
                Console.WriteLine($"  trace {instance.Node.Name}: channels {string.Join(",", instance.Channels)}, center {instance.Center}, "
                    + $"radius {instance.Radius:0.0} m, skip {string.Join(",", instance.Skip)}, shadow maps {string.Join(",", instance.ShadowMaps.Keys)}");
            foreach (LiveLight light in _liveLights)
            {
                if (traced)
                    Console.WriteLine($"    {light.Kind} {light.Guid}: skipped {instance.Skip.Contains(light.Guid)}/{(light.LightmapGuid != null && instance.Skip.Contains(light.LightmapGuid))}, "
                        + $"channels {string.Join(",", light.Channels)} overlap {light.Channels.Overlaps(instance.Channels)}, reaches {Reaches(light, instance)}");
                if (instance.Skip.Contains(light.Guid) || (light.LightmapGuid != null && instance.Skip.Contains(light.LightmapGuid))
                    || !light.Channels.Overlaps(instance.Channels) || !Reaches(light, instance))
                    continue;
                if (instance.ShadowMaps.TryGetValue(light.Guid, out JsonObject shadowed))
                {
                    passes.Add(shadowed.DeepClone());
                    _shadowedPasses++;
                }
                else if (instance.VertexShadowed.Contains(light.Guid))
                {
                    // Its shadow is per vertex, which isn't carried: leaving the light out keeps
                    // its shadowed parts dark rather than lit through walls.
                    _vertexShadowedLeftOut++;
                }
                else
                {
                    passes.Add(new JsonObject { ["light"] = light.Guid, ["page"] = -1 });
                    _unshadowedPasses++;
                }
            }
            if (passes.Count == 0)
                continue;
            JsonObject extras = instance.Node.Extras as JsonObject ?? new JsonObject();
            extras["me_light_passes"] = passes;
            instance.Node.Extras = extras;
            _passInstances++;
        }
    }

    // Whether a light's reach takes in the instance's bounding sphere (UE3's AffectsBounds).
    private static bool Reaches(LiveLight light, LitInstance instance)
    {
        if (light.Kind == "directional")
            return true;
        Vector3 toInstance = instance.Center - light.Position;
        float distance = toInstance.Length();
        if (distance > light.Radius + instance.Radius)
            return false;
        if (light.Kind != "spot" || distance <= instance.Radius)
            return true;
        // Outside the cone widened by the sphere's angular size: out of reach.
        float spread = MathF.Acos(Math.Clamp(light.CosOuter, -1f, 1f)) + MathF.Asin(Math.Clamp(instance.Radius / distance, 0f, 1f));
        return Vector3.Dot(toInstance / distance, light.Direction) >= MathF.Cos(MathF.Min(spread, MathF.PI));
    }

    private string LightPassSummary() =>
        $"Light passes: {_liveLights.Count} lights can be drawn live; {_passInstances} of {_litInstances.Count} lightmapped instances "
        + $"draw {_shadowedPasses} passes over shadow maps ({_shadowPageCount} pages) and {_unshadowedPasses} unshadowed; "
        + $"{_vertexShadowedLeftOut} vertex-shadowed lights left out.";

    private static Vector2 Vector2Prop(StructProperty s) => s == null ? Vector2.Zero
        : new Vector2(s.GetProp<FloatProperty>("X")?.Value ?? 0f, s.GetProp<FloatProperty>("Y")?.Value ?? 0f);

    // A Guid struct property (A, B, C, D) as 32 hex digits, the same for a light and what names it.
    private static string GuidText(StructProperty guid)
    {
        if (guid == null)
            return null;
        uint Part(string name) => unchecked((uint)(guid.GetProp<IntProperty>(name)?.Value ?? 0));
        return $"{Part("A"):X8}{Part("B"):X8}{Part("C"):X8}{Part("D"):X8}";
    }

    // The same for a binary Guid (its four little-endian ints are A, B, C, D).
    private static string GuidText(Guid guid)
    {
        byte[] bytes = guid.ToByteArray();
        return $"{BitConverter.ToUInt32(bytes, 0):X8}{BitConverter.ToUInt32(bytes, 4):X8}{BitConverter.ToUInt32(bytes, 8):X8}{BitConverter.ToUInt32(bytes, 12):X8}";
    }

    // A shadow map atlas page, carried once like a lightmap page (LightmapPages/Page_<n>_shadow,
    // "map": "shadow").
    private int ShadowPage(IMEPackage pcc, int texture)
    {
        string key = $"{pcc.FilePath}|{texture}";
        if (_shadowPages.TryGetValue(key, out int page))
            return page;
        SharpGLTF.Memory.MemoryImage? image = DecodeLightmap(pcc, texture);
        page = image != null ? _shadowPageCount++ : -1;
        _shadowPages[key] = page;
        if (page >= 0)
            AddLightmapTexture(page, "shadow", image.Value);
        return page;
    }
}
