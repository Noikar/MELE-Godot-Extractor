using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// UE3's lighting channels and light environments, which decide which lights reach what.
//
// A light reaches a primitive only when their LightingChannels share a channel. A light's
// default channels are BSP, Static, Dynamic, and CompositeDynamic (LightComponent's defaults);
// a primitive's are set when it's first attached, if the level didn't set them (cooked
// levels nearly always have): Static for one with static lighting, Dynamic otherwise. A
// primitive whose light environment is on (BioPawns, most placeables) composites the lights it
// gets into that environment, and its Dynamic channel counts as CompositeDynamic there (UE3's
// Light Environments documentation), so lights with Dynamic but not CompositeDynamic (the
// Presidium's sun lights) skip it. A primitive whose environment is off (InterpActors by
// default, the Tower car's door) is lit by each light directly, per pixel.
//
// That CompositeDynamic rule is for dynamic lights (movable or toggleable ones, and any with
// bForceDynamicLight): a light environment composites a static light whenever their channels
// share one, Dynamic included (most of ME1's static lights are BSP, Static, and Dynamic only).
//
// The game gets them in the extras: lights' "ue_channels" and "ue_dynamic", and on unlightmapped instances,
// skeletal placeables, and pawns, "me_channels" and "me_light_environment".
public partial class LevelBuilder
{
    // The struct's channels, in its field order.
    private static readonly string[] ChannelNames =
    [
        "BSP", "Static", "Dynamic", "CompositeDynamic", "Skybox",
        "Unnamed_1", "Unnamed_2", "Unnamed_3", "Unnamed_4", "Unnamed_5", "Unnamed_6",
        "Cinematic_1", "Cinematic_2", "Cinematic_3", "Cinematic_4", "Cinematic_5",
        "Cinematic_6", "Cinematic_7", "Cinematic_8", "Cinematic_9", "Cinematic_10",
        "Gameplay_1", "Gameplay_2", "Gameplay_3", "Gameplay_4", "Crowd",
    ];

    private static readonly string[] LightDefaultChannels = ["BSP", "Static", "Dynamic", "CompositeDynamic"];

    // A light's channels: LightComponent's defaults, changed by its archetypes' and its own.
    private static JsonArray LightChannels(ExportEntry component)
    {
        HashSet<string> set = ChannelSet(component, LightDefaultChannels);
        return Json(set);
    }

    // Whether a light is dynamic rather than static (static lighting only comes from static
    // actors: the plain PointLight, SpotLight, ..., and StaticLightCollectionActor).
    private static bool DynamicLight(ExportEntry component, ExportEntry actor) =>
        InheritedProp<BoolProperty>(component, "bForceDynamicLight")?.Value == true
        || actor.ClassName.EndsWith("Movable") || actor.ClassName.EndsWith("Toggleable");

    // A primitive's channels (PrimitiveComponent sets none by default), or the ones UE3 gives it
    // on attach when they were never set.
    private static JsonArray PrimitiveChannels(ExportEntry component, bool staticLighting)
    {
        HashSet<string> set = ChannelSet(component, []);
        if (set.Contains("bInitialized"))
            return Json(set);
        return staticLighting ? new JsonArray("Static") : new JsonArray("Dynamic", "CompositeDynamic");
    }

    // A cooked struct keeps only the fields that differ from its archetype's value, so the
    // channels are the class default's, then each archetype's stored fields and the export's own
    // on top, by the fields' instanced names (GetProp misses "Unnamed_1" and the other numbered
    // ones).
    private static HashSet<string> ChannelSet(ExportEntry export, string[] classDefault)
    {
        var chain = new List<ExportEntry>();
        for (ExportEntry e = export; e != null; e = e.Archetype as ExportEntry)
            chain.Add(e);
        var set = classDefault.ToHashSet();
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].GetProperties().GetProp<StructProperty>("LightingChannels") is not StructProperty channels)
                continue;
            foreach (BoolProperty field in channels.Properties.OfType<BoolProperty>())
            {
                if (field.Value)
                    set.Add(field.Name.Instanced);
                else
                    set.Remove(field.Name.Instanced);
            }
        }
        return set;
    }

    private static JsonArray Json(HashSet<string> set) =>
        new(ChannelNames.Where(set.Contains).Select(c => (JsonNode)c).ToArray());

    // Whether a component's light environment is on. `environment` is the environment the
    // actor or component names (0 when the reference is inherited from an engine class default,
    // which cooked components leave out). Without one: InterpActors' own default is off
    // (Default__InterpActor.MyLightEnvironment), StaticMeshActors have none, and BioWare's
    // actors (pawns, placeables) default theirs to on.
    private static bool LightEnvironmentOn(IMEPackage pcc, int environment, bool defaultOn)
    {
        if (pcc.TryGetUExport(environment, out ExportEntry export))
            return InheritedProp<BoolProperty>(export, "bEnabled")?.Value != false;
        return defaultOn;
    }

    // The channel and light-environment extras of a static mesh instance ME1 didn't lightmap.
    private static void AddMeshLighting(IMEPackage pcc, ExportEntry actor, ExportEntry component, JsonObject extras)
    {
        int environment = InheritedProp<ObjectProperty>(component, "LightEnvironment")?.Value
            ?? InheritedProp<ObjectProperty>(actor, "LightEnvironment")?.Value ?? 0;
        // BioWare's placeables (BioInert, BioUseable) name theirs m_pLightEnvComponent.
        if (environment == 0)
            environment = InheritedProp<ObjectProperty>(actor, "m_pLightEnvComponent")?.Value ?? 0;
        bool bioware = actor.ClassName.StartsWith("Bio");
        extras["me_channels"] = PrimitiveChannels(component, staticLighting: actor.ClassName == "StaticMeshActor"
            || actor.ClassName == "StaticMeshCollectionActor");
        extras["me_light_environment"] = LightEnvironmentOn(pcc, environment, defaultOn: bioware);
    }

    // The same for a skeletal placeable or a pawn: a BioWare actor, whose environment defaults on.
    private static void AddActorLighting(IMEPackage pcc, ExportEntry actor, ExportEntry component, JsonObject extras,
        string environmentProperty)
    {
        int environment = InheritedProp<ObjectProperty>(component, "LightEnvironment")?.Value
            ?? InheritedProp<ObjectProperty>(actor, environmentProperty)?.Value ?? 0;
        extras["me_channels"] = PrimitiveChannels(component, staticLighting: false);
        extras["me_light_environment"] = LightEnvironmentOn(pcc, environment, defaultOn: true);
    }
}
