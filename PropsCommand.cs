using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Prints the properties of every export whose path (or class) contains a filter, structs and
// arrays expanded. Object references print as their index and path.
public static class PropsCommand
{
    public static int Run(string package, string filter)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        foreach (ExportEntry e in pcc.Exports.Where(e => e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.ClassName.Equals(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"== {e.UIndex} {e.ClassName} {e.InstancedFullPath}");
            foreach (Property p in e.GetProperties())
                Print(pcc, p, "   ");
        }
        return 0;
    }

    private static void Print(IMEPackage pcc, Property p, string indent)
    {
        string value = p switch
        {
            ObjectProperty o => $"{o.Value} {pcc.GetEntry(o.Value)?.InstancedFullPath}",
            NameProperty n => n.Value.Instanced,
            StrProperty s => s.Value,
            IntProperty i => i.Value.ToString(),
            FloatProperty f => f.Value.ToString(),
            BoolProperty b => b.Value.ToString(),
            EnumProperty en => en.Value.Instanced,
            ByteProperty by => by.Value.ToString(),
            _ => "",
        };
        Console.WriteLine($"{indent}{p.Name.Instanced} ({p.PropType}) {value}");

        switch (p)
        {
            case StructProperty sp:
                foreach (Property c in sp.Properties)
                    Print(pcc, c, indent + "   ");
                break;
            case ArrayProperty<StructProperty> structs:
                foreach (StructProperty c in structs)
                {
                    Console.WriteLine(indent + "   -");
                    foreach (Property cc in c.Properties)
                        Print(pcc, cc, indent + "      ");
                }
                break;
            case ArrayProperty<ObjectProperty> objects:
                foreach (ObjectProperty c in objects)
                    Console.WriteLine($"{indent}   - {c.Value} {pcc.GetEntry(c.Value)?.InstancedFullPath}");
                break;
            case ArrayProperty<NameProperty> names:
                foreach (NameProperty c in names)
                    Console.WriteLine($"{indent}   - {c.Value.Instanced}");
                break;
            case ArrayProperty<IntProperty> ints:
                Console.WriteLine($"{indent}   [{string.Join(", ", ints.Select(i => i.Value))}]");
                break;
            case ArrayProperty<FloatProperty> floats:
                Console.WriteLine($"{indent}   [{string.Join(", ", floats.Select(f => f.Value))}]");
                break;
        }
    }
}
