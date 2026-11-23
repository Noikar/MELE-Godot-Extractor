using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME1;

namespace LexExport;

// ME1's English text by string id, as a level package sees it: the package's own talk files,
// then its "<package>_LOC_INT" companion's (where a _DSG sublevel's strings live, e.g. the
// Wards' sign names in BIOA_STA60_00_DSG_LOC_INT's sta00_popup_text_D), then the game's global
// talk file (Startup_INT's GlobalTlk, the shared names such as "Crate"). Each file's talk
// files are parsed once per process; the male-voiced "_M" copies are skipped (names match).
public static class Me1Talk
{
    private const string GlobalPackage = "Startup_INT.pcc";

    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, List<ME1TalkFile>> Parsed = new(StringComparer.OrdinalIgnoreCase);

    // "" when no talk file has the id (or it's 0, "no name").
    public static string Text(IMEPackage pcc, int id)
    {
        if (id <= 0 || pcc.FilePath == null)
            return "";
        string folder = Path.GetDirectoryName(pcc.FilePath)!;
        string name = Path.GetFileNameWithoutExtension(pcc.FilePath);
        string[] files =
        [
            pcc.FilePath,
            Path.Combine(folder, name + "_LOC_INT.pcc"),
            Path.Combine(folder, GlobalPackage),
        ];
        foreach (string file in files)
        {
            foreach (ME1TalkFile tlk in TalkFiles(file))
            {
                string found = tlk.FindDataById(id, false, false, false, false);
                if (!string.IsNullOrEmpty(found) && !found.StartsWith("No Data"))
                    return found.Trim('"');
            }
        }
        return "";
    }

    // A package's English (unsuffixed) talk files, parsed once; empty when it has none or the
    // file doesn't exist.
    private static List<ME1TalkFile> TalkFiles(string path)
    {
        lock (CacheLock)
        {
            if (Parsed.TryGetValue(path, out List<ME1TalkFile> cached))
                return cached;
        }
        List<ME1TalkFile> files = new List<ME1TalkFile>();
        if (File.Exists(path))
        {
            using IMEPackage package = MEPackageHandler.OpenMEPackage(path);
            foreach (ExportEntry export in package.Exports)
            {
                if (export.ClassName == "BioTlkFile" && (export.ObjectName.Name.EndsWith(".tlk") || export.ObjectName.Name.EndsWith("_tlk") || export.ObjectName.Name == "tlk"))
                    files.Add(new ME1TalkFile(export));
            }
        }
        lock (CacheLock)
            Parsed[path] = files;
        return files;
    }
}
