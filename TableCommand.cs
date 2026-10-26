using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Classes;

namespace LexExport;

// Prints a Bio2DA table (BioWare's game-data tables, e.g. Engine.pcc
// BIOG_2DA_Music_X.Music_Music) as tab-separated rows, with the column names first.
public static class TableCommand
{
    public static int Run(string package, string path)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        ExportEntry export = pcc.Exports.FirstOrDefault(e => e.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (export == null)
        {
            Console.Error.WriteLine($"No export {path}.");
            return 1;
        }

        var table = new Bio2DA(export);
        Console.WriteLine("row\t" + string.Join("\t", table.ColumnNames));
        for (int r = 0; r < table.RowCount; r++)
        {
            var cells = Enumerable.Range(0, table.ColumnCount).Select(c => table[r, c]?.DisplayableValue ?? "");
            Console.WriteLine(table.RowNames[r] + "\t" + string.Join("\t", cells));
        }
        return 0;
    }
}
