using System.Globalization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Prints every particle system whose path contains a filter: each of its modules, with
// their cooked distributions' lookup tables.
public static class ParticlesCommand
{
    public static int Run(string package, string filter)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        foreach (ExportEntry system in pcc.Exports.Where(e => e.ClassName == "ParticleSystem"
            && e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"== {system.InstancedFullPath}");
            foreach (ExportEntry module in pcc.Exports.Where(e => e.InstancedFullPath.StartsWith(system.InstancedFullPath + ".")
                && e.ClassName.StartsWith("ParticleModule")))
            {
                Console.WriteLine($"  {module.ObjectName.Instanced} ({module.ClassName})");
                foreach (Property p in module.GetProperties())
                    Print(module, p, "    ");
            }
        }
        return 0;
    }

    private static void Print(ExportEntry export, Property p, string indent)
    {
        string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        if (p is StructProperty sp)
        {
            // A cooked distribution: its lookup table (see ParticleExport), floats or vectors.
            Property table = sp.Properties.FirstOrDefault(c => c.Name.Instanced == "LookupTable");
            if (table != null)
            {
                IEnumerable<string> values = table switch
                {
                    ArrayProperty<FloatProperty> floats => floats.Select(f => F(f.Value)),
                    ArrayProperty<StructProperty> vectors => vectors.Select(v =>
                        $"({string.Join(" ", v.Properties.OfType<FloatProperty>().Select(c => F(c.Value)))})"),
                    _ => [],
                };
                string Byte(string name) => sp.GetProp<ByteProperty>(name)?.Value.ToString() ?? "-";
                string Float(string name) => sp.GetProp<FloatProperty>(name) is FloatProperty f ? F(f.Value) : "-";
                Console.WriteLine($"{indent}{p.Name.Instanced}: op {Byte("Op")} lock {Byte("Type")} timescale {Float("LookupTableTimeScale")}"
                    + $" start {Float("LookupTableStartTime")} [{string.Join(", ", values)}]");
                return;
            }
            Console.WriteLine($"{indent}{p.Name.Instanced}:");
            foreach (Property c in sp.Properties)
                Print(export, c, indent + "  ");
            return;
        }
        string value = p switch
        {
            ObjectProperty o => $"{export.FileRef.GetEntry(o.Value)?.InstancedFullPath}",
            NameProperty n => n.Value.Instanced,
            IntProperty i => i.Value.ToString(),
            FloatProperty f => F(f.Value),
            BoolProperty b => b.Value.ToString(),
            EnumProperty en => en.Value.Instanced,
            ByteProperty by => by.Value.ToString(),
            _ => $"({p.PropType})",
        };
        Console.WriteLine($"{indent}{p.Name.Instanced} = {value}");
    }
}
