using LegendaryExplorerCore.Packages;

namespace LexExport;

// Searches many packages at once for exports of a class whose path contains a substring,
// e.g. every AnimSequence with "Idle" in the name across ME2's squadmate packages.
public static class FindCommand
{
    public static int Run(string directory, string fileGlob, string className, string filter)
    {
        string[] files = Directory.GetFiles(directory, fileGlob)
            .Where(f => !Path.GetFileName(f).Contains("_LOC_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToArray();
        Console.WriteLine($"Scanning {files.Length} packages for {className} matching '{filter}'...");

        // An export can be duplicated into many packages; report each path once, with the first
        // package it was found in.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            try
            {
                using IMEPackage pcc = MEPackageHandler.OpenMEPackage(file);
                foreach (ExportEntry e in pcc.Exports)
                {
                    if (e.ClassName == className
                        && e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !seen.ContainsKey(e.InstancedFullPath))
                        seen[e.InstancedFullPath] = Path.GetFileName(file);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  skipped {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        foreach (var (path, file) in seen.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {path}    [{file}]");
        Console.WriteLine($"{seen.Count} unique match(es).");
        return 0;
    }
}
