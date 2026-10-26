using LegendaryExplorerCore.Packages;

namespace LexExport;

// Prints every entry that references an export (or import) whose path contains a filter, with
// where the reference sits (a property name, or binary data). Used to find what spawns things
// that aren't placed in the level, e.g. which placeable uses a BioVFXTemplate.
public static class RefsCommand
{
    public static int Run(string package, string filter)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        IEnumerable<IEntry> targets = pcc.Exports.Cast<IEntry>().Concat(pcc.Imports)
            .Where(e => e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (IEntry target in targets)
        {
            Console.WriteLine($"== {target.UIndex} {target.ClassName} {target.InstancedFullPath}");
            foreach ((IEntry user, List<string> places) in target.GetEntriesThatReferenceThisOne())
                Console.WriteLine($"   {user.UIndex} {user.ClassName} {user.InstancedFullPath}: {string.Join(", ", places)}");
        }
        return 0;
    }
}
