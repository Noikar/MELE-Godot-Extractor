using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LexExport;

// Finds Legendary Edition installs: every Steam library (from libraryfolders.vdf), plus the
// EA app / Origin registry entry. Prints "FOUND <path>" per valid install, best guess first.
public static class LocateCommand
{
    private const string SteamFolder = "Mass Effect Legendary Edition";

    public static int Run()
    {
        var found = new List<string>();
        void Consider(string root)
        {
            if (!string.IsNullOrEmpty(root) && IsInstall(root)
                && !found.Contains(Path.GetFullPath(root), StringComparer.OrdinalIgnoreCase))
                found.Add(Path.GetFullPath(root));
        }

        foreach (string library in SteamLibraries())
            Consider(Path.Combine(library, "steamapps", "common", SteamFolder));

        // EA app and Origin both write BioWare's key; check the 32-bit view too.
        foreach (string key in new[] { @"SOFTWARE\BioWare\Mass Effect Legendary Edition", @"SOFTWARE\WOW6432Node\BioWare\Mass Effect Legendary Edition" })
            Consider(Registry.LocalMachine.OpenSubKey(key)?.GetValue("Install Dir") as string);

        foreach (string root in found)
            Console.WriteLine($"FOUND {root}");
        if (found.Count == 0)
            Console.WriteLine("NONE");
        return 0;
    }

    // An install has the cooked packages of all three games where the manifest expects them.
    public static bool IsInstall(string root) =>
        new[] { "ME1", "ME2", "ME3" }.All(game => Directory.Exists(Path.Combine(root, "Game", game, "BioGame", "CookedPCConsole")));

    private static IEnumerable<string> SteamLibraries()
    {
        string steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string
            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
        if (steam == null)
            yield break;

        yield return steam;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;
        // Each library is a "path" "D:\\SteamLibrary" pair; backslashes are escaped.
        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }
}
