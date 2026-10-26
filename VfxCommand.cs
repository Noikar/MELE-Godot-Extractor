using System.Diagnostics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Inspects a power's visual effects (ME3's RvrClientEffect templates), as the power VFX export
// will read them: the power's class default names its templates (CE_CasterCrustTemplate,
// CE_ProjectileTemplate, CE_ImpactTemplate, CE_TargetCrustTemplate) and its projectile class.
// Each template is a list of modules (Emitter: a particle system; Location: a bone to attach
// to; Lifetime, CameraShake, EffectsMaterial, Sound, ...). For every particle system this
// prints which emitters ParticleExport can read (and why the rest are left out), and for every
// material they draw with, the material log line (translated or not), so it's the progress
// gauge for the extraction.
public static class VfxCommand
{
    public static int Run(string package, string powerClass)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        ExportEntry power = pcc.Exports.FirstOrDefault(e => e.ObjectName.Instanced.StartsWith("Default__")
            && e.ObjectName.Instanced.Contains(powerClass, StringComparison.OrdinalIgnoreCase));
        if (power == null)
        {
            Console.Error.WriteLine($"No class default matching '{powerClass}' in {Path.GetFileName(pcc.FilePath)}.");
            return 1;
        }
        Console.WriteLine($"== {power.InstancedFullPath}");

        var textures = new TextureCache(pcc, new List<string>())
        {
            GuessDiffuse = true,
            SurfaceShading = true,
            CarriedTextures = new(),
            SharedShaders = true,
        };
        var seenMaterials = new HashSet<int>();
        int exported = 0, leftOut = 0;
        var clock = Stopwatch.StartNew();

        foreach (ObjectProperty template in power.GetProperties().OfType<ObjectProperty>()
            .Where(p => p.Name.Instanced.StartsWith("CE_") && p.Name.Instanced.EndsWith("Template")))
        {
            if (!pcc.TryGetUExport(template.Value, out ExportEntry effect))
            {
                Console.WriteLine($"{template.Name.Instanced}: not in this package");
                continue;
            }
            Console.WriteLine($"\n{template.Name.Instanced} = {effect.InstancedFullPath} ({effect.ClassName})");
            foreach (ObjectProperty moduleRef in effect.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("m_lstModules")
                ?? Enumerable.Empty<ObjectProperty>())
            {
                if (!pcc.TryGetUExport(moduleRef.Value, out ExportEntry module))
                    continue;
                PropertyCollection props = module.GetProperties();
                Console.WriteLine($"  {module.ClassName.Replace("RvrCEffectModule", "")}: {Summary(pcc, props)}");
                if (props.GetProp<ObjectProperty>("m_pParticleSystem") is ObjectProperty systemRef
                    && pcc.TryGetUExport(systemRef.Value, out ExportEntry system))
                {
                    var notes = new List<string>();
                    List<ParticleExport.Emitter> emitters = ParticleExport.Read(pcc, system, notes, shared: true);
                    exported += emitters.Count;
                    leftOut += notes.Count;
                    Console.WriteLine($"    {system.ObjectName.Instanced}: {emitters.Count} emitter(s) read");
                    foreach (string note in notes)
                        Console.WriteLine($"      {note}");
                    // Every emitter's material, read or not, so translation can be checked ahead.
                    foreach (ExportEntry material in Materials(pcc, system))
                    {
                        if (seenMaterials.Add(material.UIndex))
                            textures.MaterialFor(material.UIndex, material.ObjectName.Instanced);
                    }
                    foreach (ParticleExport.Emitter emitter in emitters.Where(e => e.Mesh != null))
                        Console.WriteLine($"    mesh {emitter.Mesh.ObjectName.Instanced}" + (emitter.OverrideMaterial ? " (material overridden)" : ""));
                }
            }
        }

        // The projectile's light lives on its class default.
        if (power.GetProperties().GetProp<ObjectProperty>("ProjectileClass") is ObjectProperty projectileClass
            && pcc.GetEntry(projectileClass.Value) is IEntry projectile)
        {
            ExportEntry defaults = pcc.Exports.FirstOrDefault(e => e.ObjectName.Instanced == "Default__" + projectile.ObjectName.Instanced);
            Console.WriteLine($"\nProjectileClass {projectile.ObjectName.Instanced}: " + (defaults != null ? Summary(pcc, defaults.GetProperties()) : "no class default here"));
        }

        Console.WriteLine($"\n{exported} emitter(s) read, {leftOut} left out; {seenMaterials.Count} material(s); {clock.Elapsed.TotalSeconds:0.0} s.");
        return 0;
    }

    // The materials a system's emitters draw with (each LOD's Required module), and mesh
    // emitters' own mesh materials when they don't override them.
    private static IEnumerable<ExportEntry> Materials(IMEPackage pcc, ExportEntry system)
    {
        string prefix = system.InstancedFullPath + ".";
        foreach (ExportEntry module in pcc.Exports.Where(e => e.InstancedFullPath.StartsWith(prefix)))
        {
            PropertyCollection props = module.GetProperties();
            if (module.ClassName == "ParticleModuleRequired"
                && props.GetProp<ObjectProperty>("Material") is ObjectProperty m && pcc.GetEntry(m.Value) is ExportEntry material)
                yield return material;
            else if (module.ClassName == "ParticleModuleTypeDataMesh" && props.GetProp<BoolProperty>("bOverrideMaterial")?.Value != true
                && props.GetProp<ObjectProperty>("Mesh") is ObjectProperty mesh && pcc.GetEntry(mesh.Value) is ExportEntry staticMesh)
            {
                foreach (int index in StaticMeshMaterials(staticMesh))
                {
                    if (pcc.GetEntry(index) is ExportEntry meshMaterial)
                        yield return meshMaterial;
                }
            }
        }
    }

    private static IEnumerable<int> StaticMeshMaterials(ExportEntry staticMesh)
    {
        var binary = LegendaryExplorerCore.Unreal.BinaryConverters.ObjectBinary.From<LegendaryExplorerCore.Unreal.BinaryConverters.StaticMesh>(staticMesh);
        return binary.LODModels.Length > 0 ? binary.LODModels[0].Elements.Select(e => e.Material) : [];
    }

    // A module's simple properties (names, numbers, flags, object references), one line.
    private static string Summary(IMEPackage pcc, PropertyCollection props)
    {
        var parts = new List<string>();
        foreach (Property p in props)
        {
            string name = p.Name.Instanced;
            string value = p switch
            {
                ObjectProperty o when o.Value != 0 => pcc.GetEntry(o.Value)?.ObjectName.Instanced,
                NameProperty n => n.Value.Instanced,
                FloatProperty f => f.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                IntProperty i => i.Value.ToString(),
                BoolProperty b => b.Value ? "true" : "false",
                EnumProperty e => e.Value.Instanced,
                ByteProperty by => by.Value.ToString(),
                _ => null,
            };
            if (value != null)
                parts.Add($"{name}={value}");
        }
        return string.Join(", ", parts);
    }
}
