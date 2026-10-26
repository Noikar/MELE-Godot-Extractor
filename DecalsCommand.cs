using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Prints every DecalComponent's cooked receivers: the clipped receiver geometry UE3 draws a
// static decal with (vertex and triangle counts, a few vertices' positions, decal UVs, and
// lightmap coordinates), the receiver's own lightmap, and the decal's.
public static class DecalsCommand
{
    public static int Run(string package)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        foreach (ExportEntry e in pcc.Exports.Where(e => e.ClassName == "DecalComponent"))
        {
            PropertyCollection props = e.GetProperties();
            string material = pcc.GetEntry(props.GetProp<ObjectProperty>("DecalMaterial")?.Value ?? 0)?.InstancedFullPath;
            Console.WriteLine($"== {e.UIndex} {e.InstancedFullPath}  material {material}");
            var decal = ObjectBinary.From<DecalComponent>(e);
            foreach (StaticReceiverData r in decal.StaticReceivers)
            {
                ExportEntry receiver = pcc.GetUExport(r.PrimitiveComponent);
                string receiverMap = "?";
                try
                {
                    var smc = ObjectBinary.From<StaticMeshComponent>(receiver);
                    receiverMap = smc.LODData.Length > 0 ? smc.LODData[0].LightMap?.GetType().Name ?? "null" : "no LOD";
                }
                catch (Exception ex)
                {
                    receiverMap = ex.Message;
                }
                Console.WriteLine($"   receiver {receiver.InstancedFullPath} ({receiver.ClassName}, lightmap {receiverMap}): "
                    + $"{r.Vertices.Length} vertices, {r.Indices.Length} indices, {r.NumTriangles} triangles, "
                    + $"decal lightmap {Describe(r.LightMap)}, Data {r.Data}, InstanceIndex {r.InstanceIndex}");
                foreach (DecalVertex v in r.Vertices.Take(4))
                {
                    Vector3 n = MeshCommand.Unpack(v.TangentZ);
                    Console.WriteLine($"      pos {v.Position}  normal {n}  uv {v.ProjectedUVs}  lightmap {v.LightMapCoordinate}  "
                        + $"normal transform {v.NormalTransform1} {v.NormalTransform2}");
                }
            }
        }
        return 0;
    }

    private static string Describe(LightMap map) => map switch
    {
        null => "null",
        LightMap_1D one => $"1D ({one.DirectionalSamples?.Length ?? 0} samples, scale1 {one.ScaleVector1}, scale2 {one.ScaleVector2})",
        LightMap_2D two => $"2D (textures {two.Texture1}/{two.Texture2}, scale {two.CoordinateScale}, bias {two.CoordinateBias})",
        _ => map.GetType().Name,
    };
}
