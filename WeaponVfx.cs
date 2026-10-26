using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// A gun's effects from its BioWeaponVFXAppearance (e.g. BIOG_V_C_Weapons_Z.01_Generic.PST_Generic_VFX
// in BIOC_Materials.pcc). Each effect there is a BioVFXTemplate that spawns a Prefab (emitters,
// a light, lens flares) and plays its oSoundCue:
//   vfxMuzzleFlash / vfxMuzzleFlash2   at the gun's Flash_1 / Flash_2 sockets, every shot
//   vfxTracer                          a beam from the Tracer socket to the hit
//   VisualImpactSet                    a BioVISWeapon: impact effects per physical material,
//                                      with vfxDefaultVisualEffect / vfxDefaultMaterialEffect
//
// So far only the sounds are exported (beside the .glb, through SoundExport): "fire" (the primary
// muzzle flash's cue) and "impact" (the default visual impact's cue). The particle systems are the
// next step (Documentation/handoff-combat-shooting.md).
public static class WeaponVfx
{
    public static JsonObject Export(IMEPackage pcc, string appearancePath, string outDir)
    {
        ExportEntry appearance = pcc.FindExport(appearancePath)
            ?? throw new ArgumentException($"No export '{appearancePath}' in {Path.GetFileName(pcc.FilePath)}.");
        PropertyCollection props = appearance.GetProperties();
        var result = new JsonObject { ["appearance"] = appearance.InstancedFullPath };

        if (TemplateSound(pcc, Object(pcc, props, "vfxMuzzleFlash"), outDir) is JsonObject fire)
            result["fire"] = fire;
        if (Object(pcc, props, "VisualImpactSet") is ExportEntry impacts
            && TemplateSound(pcc, Object(pcc, impacts.GetProperties(), "vfxDefaultVisualEffect"), outDir) is JsonObject impact)
            result["impact"] = impact;
        return result;
    }

    // A BioVFXTemplate's sound, exported, or null when it has none.
    private static JsonObject TemplateSound(IMEPackage pcc, ExportEntry template, string outDir)
    {
        if (template == null || Object(pcc, template.GetProperties(), "oSoundCue") is not ExportEntry cue)
            return null;
        try
        {
            return SoundExport.Export(pcc, cue, outDir);
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"  sound {cue.ObjectName.Instanced} left out: {ex.Message}");
            return null;
        }
    }

    private static ExportEntry Object(IMEPackage pcc, PropertyCollection props, string name) =>
        pcc.TryGetUExport(props.GetProp<ObjectProperty>(name)?.Value ?? 0, out ExportEntry export) ? export : null;
}
