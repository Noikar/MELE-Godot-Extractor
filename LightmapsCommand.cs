using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Prints the lightmaps of the StaticMeshComponents whose path contains a filter: every texture
// slot and scale vector, the coordinate scale and bias, and the baked lights. For checking how a
// game fills the four slots (LE1 and LE3 name and scale them differently).
public static class LightmapsCommand
{
    public static int Run(string package, string filter, int limit)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        int shown = 0;
        foreach (ExportEntry e in pcc.Exports.Where(e => e.ClassName == "StaticMeshComponent"
            && e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            StaticMeshComponent binary;
            try
            {
                binary = ObjectBinary.From<StaticMeshComponent>(e);
            }
            catch (Exception)
            {
                continue;
            }
            if (binary.LODData.Length == 0)
                continue;
            string Name(int index) => index == 0 ? "-" : pcc.GetEntry(index)?.ObjectName.Instanced ?? index.ToString();
            string text = binary.LODData[0].LightMap switch
            {
                LightMap_2D two => $"2D\n    1 {Name(two.Texture1)} {two.ScaleVector1}\n    2 {Name(two.Texture2)} {two.ScaleVector2}"
                    + $"\n    3 {Name(two.Texture3)} {two.ScaleVector3}\n    4 {Name(two.Texture4)} {two.ScaleVector4}"
                    + $"\n    coordinates {two.CoordinateScale} + {two.CoordinateBias}, {two.LightGuids?.Length ?? 0} lights",
                LightMap_1D one => $"1D scale1 {one.ScaleVector1}, scale2 {one.ScaleVector2}, {one.LightGuids?.Length ?? 0} lights",
                null => "none",
                LightMap other => other.GetType().Name,
            };
            Console.WriteLine($"{e.InstancedFullPath}: {text}");
            if (++shown >= limit)
                break;
        }
        return 0;
    }
}
