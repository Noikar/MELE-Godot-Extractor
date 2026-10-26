using LegendaryExplorerCore.Packages;

namespace LexExport;

// Lists a package's exports (index, class, path, size), optionally only those whose path or
// class contains a filter. The quickest look at what a package holds.
public static class ExportsCommand
{
    public static int Run(string package, string filter)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        foreach (ExportEntry e in pcc.Exports)
        {
            if (filter == null
                || e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"{e.UIndex,6} {e.ClassName,-28} {e.InstancedFullPath}  ({e.DataSize} bytes)");
        }
        return 0;
    }
}
