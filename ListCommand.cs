using LegendaryExplorerCore.Packages;

namespace LexExport;

// Prints the exports worth extracting from a package, grouped by class.
public static class ListCommand
{
    private static readonly string[] InterestingClasses =
    [
        "SkeletalMesh", "StaticMesh", "AnimSet", "AnimSequence", "AnimTree",
        "Texture2D", "Material", "MaterialInstanceConstant",
    ];

    public static int Run(string packagePath, string filter)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(packagePath);
        Console.WriteLine($"{Path.GetFileName(packagePath)}  ({pcc.Game}, {pcc.ExportCount} exports)");

        var groups = pcc.Exports
            .Where(e => InterestingClasses.Contains(e.ClassName))
            .Where(e => filter == null || e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.ClassName)
            .OrderBy(g => Array.IndexOf(InterestingClasses, g.Key));

        foreach (var group in groups)
        {
            Console.WriteLine($"\n[{group.Key}] ({group.Count()})");
            foreach (ExportEntry export in group.OrderBy(e => e.InstancedFullPath))
                Console.WriteLine($"  {export.InstancedFullPath}");
        }
        return 0;
    }
}
