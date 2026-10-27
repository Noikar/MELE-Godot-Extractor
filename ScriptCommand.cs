using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.UnrealScript;

namespace LexExport;

// `script <pcc> <filter>...`: decompiles UnrealScript, for porting a game's behavior faithfully
// (ME3's seeking power projectiles, ME1's Lift and Warp scripts). Prints every Function and State
// whose path contains a filter, and a Class whose path ends with one (".<filter>" or the whole
// path), as LEX's decompiler writes them. Class names resolve through the game's base files,
// so the game root (ME_LE_ROOT or `locate`) has to be set.
public static class ScriptCommand
{
    public static int Run(string packagePath, string[] filters)
    {
        LegendaryExplorerCoreLibSettings.Instance.LEDirectory = Program.GameRoot;
        IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(packagePath));
        using var lib = new FileLib(pcc);
        if (!lib.Initialize(new UnrealScriptOptionsPackage(), false))
            Console.Error.WriteLine("script: the script library didn't initialize; names from other packages may be missing.");
        int printed = 0;
        foreach (ExportEntry export in pcc.Exports)
        {
            if (export.ClassName is not ("Function" or "State" or "Class"))
                continue;
            string path = export.InstancedFullPath;
            bool match = export.ClassName == "Class"
                ? filters.Any(f => path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.EndsWith("." + f, StringComparison.OrdinalIgnoreCase))
                : filters.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (!match)
                continue;
            try
            {
                (_, string text) = UnrealScriptCompiler.DecompileExport(export, lib, null);
                Console.WriteLine($"// ===== {export.ClassName} {path}");
                Console.WriteLine(text);
                printed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"// !! {path}: {ex.Message}");
            }
        }
        Console.WriteLine($"// {printed} export(s) decompiled.");
        return 0;
    }
}
