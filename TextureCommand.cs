using LegendaryExplorerCore.Packages;
using Texture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

namespace LexExport;

// Writes every Texture2D whose path contains a filter to <out folder>/<name>.png at full size,
// as stored (sRGB textures stay sRGB-encoded). For working out a material's recipe from its
// masks and stacks. With --dds, writes <name>.dds instead: the format and mips ME1 stores, as
// the level and character exports carry them (DdsTextures).
public static class TextureCommand
{
    public static int Run(string package, string filter, string outFolder, bool dds)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        Directory.CreateDirectory(outFolder);
        foreach (ExportEntry e in pcc.Exports.Where(e => e.ClassName is "Texture2D" or "LightMapTexture2D" or "ShadowMapTexture2D"
            && e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path.Combine(outFolder, e.ObjectName.Instanced + (dds ? ".dds" : ".png"));
            bool srgb = e.GetProperty<LegendaryExplorerCore.Unreal.BoolProperty>("SRGB")?.Value ?? true;
            bool ok;
            string note = "";
            if (dds)
            {
                byte[] data = DdsTextures.OriginalDds(e, int.MaxValue, out string failure);
                ok = data != null;
                if (ok)
                    File.WriteAllBytes(path, data);
                Texture2D texture = new Texture2D(e);
                // Where each mip lives: in the package, or a texture cache file (TFC) beside it.
                string mips = string.Join(", ", texture.Mips.Select(m => $"{m.width}x{m.height} {m.storageType}"
                    + (m.IsPackageStored ? "" : $" in {m.TextureCacheName}")));
                note = $" ({texture.TextureFormat}{(ok ? "" : ": " + failure)}; mips {mips})";
            }
            else
                ok = new Texture2D(e).ExportToPNG(path);
            Console.WriteLine($"{(ok ? "wrote" : "FAILED")} {path}{note}{(srgb ? "" : " (linear)")}");
        }
        return 0;
    }
}
