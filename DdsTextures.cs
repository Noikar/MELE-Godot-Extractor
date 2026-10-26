using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using LexImage = LegendaryExplorerCore.Textures.Image;
using Texture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

namespace LexExport;

// Writes a model's textures as DDS (glTF's MSFT_texture_dds), so the game uploads them as they
// are: block-compressed, with mipmaps. Godot's runtime glTF loader decodes PNGs to uncompressed
// RGBA without mipmaps (2.7 GB of video memory for the Wards); the game reads DDS through
// godot/DdsExtension.gd instead. The editor's import unpacks them as PNGs, as before.
//
// Every image is DDS from the moment it's made, with no PNG in between. Textures carried exactly
// as ME1 stored them (Original) keep ME1's own data and mips. The rest, which we bake (material
// diffuses, flipped normal maps, roughness, translucent bakes, character and NPC looks), get
// mipmaps and are encoded by DirectXTex (Encode): BC1, or BC3 with alpha, and BC5 for normal
// maps, whose two channels every shader that reads them rebuilds Z from.
public static class DdsTextures
{
    // Points every DDS texture of the finished model at its image through MSFT_texture_dds, and
    // encodes any PNG or JPEG image that reached the model anyway. Returns a summary for the
    // export log.
    public static string Convert(ModelRoot model)
    {
        var normals = model.LogicalMaterials
            .SelectMany(m => m.Channels)
            .Where(c => c.Key == "Normal" && c.Texture?.PrimaryImage != null)
            .Select(c => c.Texture.PrimaryImage)
            .ToHashSet();

        // Stragglers only: everything we write is already DDS. A DDS texture's fallback image
        // is SharpGLTF's own placeholder PNG, which stays.
        var stragglers = model.LogicalTextures.Select(t => t.PrimaryImage)
            .Where(i => i != null && (i.Content.IsPng || i.Content.IsJpg)).Distinct().ToList();
        var converted = new byte[stragglers.Count][];
        Parallel.For(0, stragglers.Count, ParallelLimits.Options, i =>
            converted[i] = EncodeDds(stragglers[i].Content, normals.Contains(stragglers[i])));
        for (int i = 0; i < stragglers.Count; i++)
        {
            if (converted[i] != null)
                stragglers[i].Content = new MemoryImage(converted[i]);
        }

        int dds = 0;
        long bytes = 0;
        foreach (SharpGLTF.Schema2.Image image in model.LogicalImages)
        {
            if (!image.Content.IsDds)
                continue;
            dds++;
            bytes += image.Content.Content.Length;
        }
        foreach (SharpGLTF.Schema2.Texture texture in model.LogicalTextures)
        {
            // Re-set, so a DDS image is referenced through the extension.
            if (texture.PrimaryImage is { Content.IsDds: true } image)
                texture.SetImage(image);
        }
        int failed = converted.Count(c => c == null);
        return $"{dds} textures as DDS ({bytes / 1048576.0:0} MB)"
            + (stragglers.Count > 0 ? $", {stragglers.Count - failed} encoded from PNG" : "")
            + (failed > 0 ? $", {failed} left as PNG" : "");
    }

    // An ME1 texture's own mips from the largest no wider than maxWidth down, as a DDS image;
    // null when OriginalDds can't write it.
    public static MemoryImage? Original(ExportEntry export, int maxWidth)
    {
        byte[] dds = OriginalDds(export, maxWidth, out _);
        return dds != null ? new MemoryImage(dds) : (MemoryImage?)null;
    }

    // Baked pixels as a DDS image (see EncodeBgra).
    public static MemoryImage Encode(Rgba32[] pixels, int width, int height, bool normalMap)
    {
        var bgra = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            bgra[i * 4] = p.B;
            bgra[i * 4 + 1] = p.G;
            bgra[i * 4 + 2] = p.R;
            bgra[i * 4 + 3] = p.A;
        }
        return Encode(bgra, width, height, normalMap);
    }

    // BGRA pixels as a DDS image (see EncodeBgra). Should DirectXTex fail, the image goes in as
    // a PNG and Convert reports it.
    public static MemoryImage Encode(byte[] bgra, int width, int height, bool normalMap)
    {
        byte[] dds = EncodeBgra(bgra, width, height, normalMap);
        if (dds != null)
            return new MemoryImage(dds);
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(bgra, width, height);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return new MemoryImage(png.ToArray());
    }

    // An ME1 texture's own mips, from the largest no wider than maxWidth down, as a DDS. Mips
    // listed above the cooked size are empty and skipped. Null when a mip below them can't be
    // read or the format has no DDS mapping here (failure says why).
    public static byte[] OriginalDds(ExportEntry export, int maxWidth, out string failure)
    {
        failure = null;
        try
        {
            var texture = new Texture2D(export);
            PixelFormat format = LexImage.getPixelFormatType(texture.TextureFormat);
            if (!Writable(format))
            {
                failure = $"format {format}";
                return null;
            }
            var mips = new List<Mip>();
            foreach (Texture2DMipInfo mip in texture.Mips.Where(m => m.width <= maxWidth))
            {
                if (mip.storageType == StorageTypes.empty)
                {
                    if (mips.Count == 0)
                        continue;
                    failure = $"mip {mip.width}x{mip.height} is empty";
                    return null;
                }
                mips.Add(new Mip(texture.GetImageBytesForMip(mip, export.Game, true, out _, null, null), mip.width, mip.height));
            }
            if (mips.Count == 0)
            {
                failure = "no mips";
                return null;
            }
            return WriteDds(mips, format);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex.Message;
            return null;
        }
    }

    // A PNG or JPEG that reached a model (see Convert), as EncodeBgra.
    private static byte[] EncodeDds(MemoryImage png, bool normalMap)
    {
        try
        {
            // ImageSharp reads any PNG (LEX's loader rejects some, e.g. 16-bit ones).
            using var decoded = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(png.Content.Span);
            byte[] pixels = new byte[decoded.Width * decoded.Height * 4];
            decoded.CopyPixelDataTo(pixels);
            return EncodeBgra(pixels, decoded.Width, decoded.Height, normalMap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"  could not decode a {(png.IsPng ? "PNG" : "JPEG")} for DDS ({png.Content.Length} bytes): {ex.Message}");
            return null;
        }
    }

    // BGRA pixels with box-filtered mipmaps down to 1x1, each block-compressed by DirectXTex.
    // Null when the encoder fails.
    private static byte[] EncodeBgra(byte[] pixels, int width, int height, bool normalMap)
    {
        try
        {
            PixelFormat format = normalMap ? PixelFormat.BC5 : HasAlpha(pixels) ? PixelFormat.DXT5 : PixelFormat.DXT1;
            var mips = new List<Mip>();
            while (true)
            {
                mips.Add(new Mip(TexConverter.ConvertTexture(pixels, (uint)width, (uint)height, PixelFormat.ARGB, format), width, height));
                if (width == 1 && height == 1)
                    break;
                pixels = HalfSize(pixels, ref width, ref height);
            }
            return WriteDds(mips, format);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"  could not encode a {width}x{height} texture as DDS: {ex.Message}");
            return null;
        }
    }

    // Whether any texel isn't opaque (ARGB is stored B, G, R, A).
    private static bool HasAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 255)
                return true;
        }
        return false;
    }

    // The next mip of a BGRA image: each texel averages the (up to) 2x2 texels it covers.
    private static byte[] HalfSize(byte[] bgra, ref int width, ref int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        int stepX = width > 1 ? 2 : 1, stepY = height > 1 ? 2 : 1;
        var half = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < stepY; dy++)
                    {
                        for (int dx = 0; dx < stepX; dx++)
                            sum += bgra[((y * stepY + dy) * width + x * stepX + dx) * 4 + c];
                    }
                    half[(y * w + x) * 4 + c] = (byte)((sum + stepX * stepY / 2) / (stepX * stepY));
                }
            }
        }
        width = w;
        height = h;
        return half;
    }

    // ---------------------------------------------------------------
    // DDS files
    // ---------------------------------------------------------------

    private readonly record struct Mip(byte[] Data, int Width, int Height);

    // DXGI formats for the DX10 header the block formats are written with.
    private static uint DxgiFormat(PixelFormat format) => format switch
    {
        PixelFormat.DXT1 => 71,  // BC1_UNORM
        PixelFormat.DXT3 => 74,  // BC2_UNORM
        PixelFormat.DXT5 => 77,  // BC3_UNORM
        PixelFormat.ATI2 or PixelFormat.BC5 => 83,  // BC5_UNORM
        PixelFormat.BC7 => 98,  // BC7_UNORM
        _ => 0,
    };

    private static bool Writable(PixelFormat format) =>
        DxgiFormat(format) != 0 || format is PixelFormat.ARGB or PixelFormat.G8;

    // A DDS file: the block formats with a DX10 header, uncompressed BGRA and 8-bit luminance
    // (which samples as gray, as D3D9's L8 did) with the legacy one. Sampling decides sRGB.
    private static byte[] WriteDds(List<Mip> mips, PixelFormat format)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x20534444u);  // "DDS "
        writer.Write(124u);  // header size
        // Caps, height, width, pixel format, mipmap count, linear size.
        writer.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u);
        writer.Write((uint)mips[0].Height);
        writer.Write((uint)mips[0].Width);
        writer.Write((uint)mips[0].Data.Length);
        writer.Write(0u);  // depth
        writer.Write((uint)mips.Count);
        for (int i = 0; i < 11; i++)
            writer.Write(0u);

        writer.Write(32u);  // pixel format size
        if (format == PixelFormat.ARGB)
        {
            writer.Write(0x1u | 0x40u);  // alpha pixels, RGB
            writer.Write(0u);
            writer.Write(32u);
            writer.Write(0x00FF0000u);
            writer.Write(0x0000FF00u);
            writer.Write(0x000000FFu);
            writer.Write(0xFF000000u);
        }
        else if (format == PixelFormat.G8)
        {
            writer.Write(0x20000u);  // luminance
            writer.Write(0u);
            writer.Write(8u);
            writer.Write(0xFFu);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
        }
        else
        {
            writer.Write(0x4u);  // four CC
            writer.Write(0x30315844u);  // "DX10"
            for (int i = 0; i < 5; i++)
                writer.Write(0u);
        }
        writer.Write(0x1000u | 0x8u | 0x400000u);  // texture, complex, mipmap
        for (int i = 0; i < 4; i++)
            writer.Write(0u);

        if (DxgiFormat(format) != 0)
        {
            writer.Write(DxgiFormat(format));
            writer.Write(3u);  // 2D texture
            writer.Write(0u);
            writer.Write(1u);  // array size
            writer.Write(0u);
        }
        foreach (Mip mip in mips)
            writer.Write(mip.Data);
        writer.Flush();
        return stream.ToArray();
    }
}
