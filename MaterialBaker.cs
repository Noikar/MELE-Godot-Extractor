using System.Numerics;
using LegendaryExplorerCore.Packages;
using SharpGLTF.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Texture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;
using Texture2DMipInfo = LegendaryExplorerCore.Unreal.Classes.Texture2DMipInfo;

namespace LexExport;

// Bakes Mass Effect's tinted materials into a plain base-color texture. Many characters don't
// have a real diffuse: their color comes from shader math over packed mask textures and tint
// colors set per material instance (or per character, through a BioMaterialOverride). The
// cooked master materials keep only their parameter nodes, so the recipes below were worked
// out from the textures themselves and checked against renders.
public static class MaterialBaker
{
    // Textures larger than this are halved until they fit, to keep the .glb size sane.
    private const int MaxSize = 2048;

    // Tries every recipe in turn; null when the material isn't one of the tinted kinds.
    // alphaMask is set when the result carries cutout opacity (hair cards).
    public static MemoryImage? Bake(Dictionary<string, ExportEntry> textures, Dictionary<string, Vector3> colors,
        Dictionary<string, float> scalars, out bool alphaMask, out string recipe)
    {
        alphaMask = false;
        recipe = null;
        Vector3 Color(string name, Vector3 fallback) => colors.TryGetValue(name, out Vector3 c) ? c : fallback;
        Vector3 skin = Color("SkinTone", new Vector3(0.76f, 0.32f, 0.15f));

        if (textures.TryGetValue("TNT_Diff_Stack", out ExportEntry diffStack))
        {
            Pixels diff = Pixels.Load(diffStack);
            if (diff == null)
                return null;

            if (colors.ContainsKey("TNT_Param_01"))
            {
                // ME1 tint (TNT_MASTER_MAT, read from its compiled shader). Tint_Stack's RGB
                // channels, picked by the Param vectors, shade three tint colors and its alpha
                // shades Colour_04; the TNT_Stripe texture, picked by Param_Stripe, blends in the
                // stripe color. Diffuse red is the shading, and diffuse alpha splits cloth (1)
                // from exposed skin (0), which is a slightly desaturated skin tone.
                recipe = "ME1 tint";
                Pixels mask = textures.TryGetValue("TNT_Tint_Stack", out ExportEntry m) ? Pixels.Load(m) : null;
                Pixels stripes = textures.TryGetValue("TNT_Stripe", out ExportEntry st) ? Pixels.Load(st) : null;
                bool maskSrgb = m != null && IsSrgb(m);
                bool stripeSrgb = st != null && IsSrgb(st);
                bool diffSrgb = IsSrgb(diffStack);

                var channels = new Vector3[3];
                var tints = new Vector3[3];
                for (int i = 0; i < 3; i++)
                {
                    channels[i] = Color($"TNT_Param_0{i + 1}", Vector3.Zero);
                    tints[i] = Color($"TNT_Colour_0{i + 1}", Vector3.One);
                }
                Vector3 panel = Color("TNT_Colour_04", Vector3.One);
                Vector3 stripe = Color("TNT_Stripe_Colour", Vector3.One);
                Vector3 stripeChannel = Color("TNT_Param_Stripe", Vector3.UnitX);
                float desaturation = scalars.TryGetValue("TNT_Desaturation_Scalar", out float ds) ? ds : 0.25f;
                float luminance = Vector3.Dot(skin, new Vector3(0.3f, 0.59f, 0.11f));
                Vector3 skinColor = Vector3.Lerp(skin, new Vector3(luminance), desaturation);

                return diff.Map((x, y, d) =>
                {
                    // The default mask and stripe textures are plain white.
                    Vector4 t = mask?.Sample(x, y, diff) ?? Vector4.One;
                    if (maskSrgb)
                        t = new Vector4(Linear(t.AsVector3()), t.W);
                    Vector4 sp = stripes?.Sample(x, y, diff) ?? Vector4.One;
                    if (stripeSrgb)
                        sp = new Vector4(Linear(sp.AsVector3()), sp.W);

                    Vector3 tint = t.W * panel;
                    for (int i = 0; i < 3; i++)
                        tint += Vector3.Dot(t.AsVector3(), channels[i]) * tints[i];
                    float s = Vector3.Dot(sp.AsVector3(), stripeChannel);
                    tint = Vector3.Lerp(tint, stripe, s);

                    float shade = diffSrgb ? Linear(d.X) : d.X;
                    return new Vector4(shade * Vector3.Lerp(skinColor, tint, d.W), 1f);
                });
            }

            // ME2 tint stack (quarian suits): each diffuse channel is the shading of one tint
            // color's region, so the color is their weighted sum. Alpha isn't sRGB-encoded.
            recipe = "tint stack";
            var stack = new Vector3[4];
            for (int i = 0; i < 4; i++)
                stack[i] = Color($"TNT_Colour_0{i + 1}", Vector3.One);
            return diff.Map((_, _, d) =>
            {
                Vector3 lin = Linear(d.AsVector3());
                return new Vector4(lin.X * stack[0] + lin.Y * stack[1] + lin.Z * stack[2] + d.W * stack[3], 1f);
            });
        }

        // Morph-head face and scalp: a neutral, very pale diffuse multiplied by the skin tone.
        foreach (string name in new[] { "HED_Diff", "HED_Scalp_Diff" })
        {
            if (textures.TryGetValue(name, out ExportEntry tex) && Pixels.Load(tex) is Pixels pale)
            {
                recipe = "skin tone";
                return pale.Map((_, _, d) => new Vector4(Linear(d.AsVector3()) * skin, 1f));
            }
        }

        // Hair cards: green is the strand shading, alpha the coverage.
        if (textures.TryGetValue("HAIR_Diff", out ExportEntry hairTex) && Pixels.Load(hairTex) is Pixels hair)
        {
            recipe = "hair";
            alphaMask = true;
            Vector3 hairColor = Color("HED_Hair_Colour_Vector", new Vector3(0.06f, 0.035f, 0.025f));
            return hair.Map((_, _, d) => new Vector4(Vector3.Min(hairColor * Linear(d.Y) * 4f, Vector3.One), d.W));
        }

        return null;
    }

    // A tangent-space normal map for glTF. Unreal's are DirectX-style (green points down the
    // texture) and glTF's are OpenGL-style, so green is flipped. Z is rebuilt from X and Y,
    // since some formats only keep two channels.
    internal static MemoryImage BakeNormal(Pixels pixels) =>
        pixels.MapRaw((_, _, d) =>
        {
            float x = d.X * 2f - 1f;
            float y = 1f - d.Y * 2f;
            float z = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y));
            return new Vector4(x * 0.5f + 0.5f, y * 0.5f + 0.5f, z * 0.5f + 0.5f, 1f);
        }, normalMap: true);

    // glTF's metallic-roughness texture (green = roughness, blue = metallic) from an Unreal
    // specular map: the brighter the specular, the glossier the surface. averageRoughness
    // is for the export log.
    internal static MemoryImage BakeRoughness(Pixels pixels, out float averageRoughness)
    {
        double sum = 0.0;
        foreach (Vector4 d in pixels.Data)
            sum += Roughness(d);
        averageRoughness = (float)(sum / pixels.Data.Length);
        return pixels.MapRaw((_, _, d) => new Vector4(1f, Roughness(d), 0f, 1f));
    }

    // One texture channel's share of a baked translucent surface: Weights are its
    // coefficients in the red, green, blue, and opacity outputs.
    public record ChannelTerm(ExportEntry Texture, int Channel, Vector4 Weights);

    // A translucent surface as ME's shader draws it: each output is a constant plus weighted
    // texture channels, summed in linear space (color channels of sRGB textures are
    // linearized first). HDR colors are stored divided by scale (1 when nothing exceeds 1),
    // which the game multiplies back in. Null when no texture decodes.
    public static MemoryImage? BakeTranslucent(List<ChannelTerm> terms, Vector4 constant, out float scale)
    {
        scale = 1f;
        var layers = terms
            .Select(t => (Term: t, Pixels: Cached(t.Texture), Srgb: IsSrgb(t.Texture)))
            .Where(l => l.Pixels != null)
            .ToList();
        if (layers.Count == 0)
            return null;
        Pixels canvas = layers.MaxBy(l => l.Pixels.Width * l.Pixels.Height).Pixels;

        Vector4 Shade(int x, int y)
        {
            Vector4 value = constant;
            foreach (var (term, pixels, srgb) in layers)
            {
                float c = Channel(pixels.Sample(x, y, canvas), term.Channel);
                value += term.Weights * (srgb && term.Channel < 3 ? Linear(c) : c);
            }
            return value;
        }

        float peak = 0f;
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                Vector4 v = Shade(x, y);
                peak = MathF.Max(peak, MathF.Max(v.X, MathF.Max(v.Y, v.Z)));
            }
        }
        float s = scale = MathF.Max(1f, peak);
        return canvas.Map((x, y, _) =>
        {
            Vector4 v = Shade(x, y);
            return new Vector4(v.X / s, v.Y / s, v.Z / s, v.W);
        });
    }

    // A texture channel's average (linear for the color channels of sRGB textures), for
    // samples that can't be baked at the surface's UVs, such as view-dependent ones.
    public static float ChannelMean(ExportEntry texture, int channel)
    {
        Pixels pixels = Cached(texture);
        if (pixels == null)
            return 0f;
        bool linearize = IsSrgb(texture) && channel < 3;
        double sum = 0.0;
        foreach (Vector4 d in pixels.Data)
        {
            float c = Channel(d, channel);
            sum += linearize ? Linear(c) : c;
        }
        return (float)(sum / pixels.Data.Length);
    }

    // Textures the translucent bakes and channel means read, decoded once per level
    // (ForgetDecoded empties it when the level is written).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Pixels>> DecodedTextures = new();

    private static Pixels Cached(ExportEntry texture) =>
        DecodedTextures.GetOrAdd(texture.InstancedFullPath, _ => new Lazy<Pixels>(() => Pixels.Load(texture))).Value;

    public static void ForgetDecoded() => DecodedTextures.Clear();

    // Unreal textures are sRGB unless SRGB is turned off (normal maps, masks).
    internal static bool IsSrgb(ExportEntry texture) =>
        texture.GetProperty<LegendaryExplorerCore.Unreal.BoolProperty>("SRGB")?.Value ?? true;

    private static float Channel(Vector4 v, int channel) => channel switch
    {
        0 => v.X,
        1 => v.Y,
        2 => v.Z,
        _ => v.W,
    };

    private const float GlossiestRoughness = 0.15f;
    private const float RoughestRoughness = 0.95f;

    private static float Roughness(Vector4 specular)
    {
        float strength = MathF.Max(specular.X, MathF.Max(specular.Y, specular.Z));
        return Math.Clamp(1f - strength, GlossiestRoughness, RoughestRoughness);
    }

    internal static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    internal static Vector3 Linear(Vector3 c) => new Vector3(Linear(c.X), Linear(c.Y), Linear(c.Z));

    internal static float Srgb(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    // A decoded texture as raw 0..1 RGBA floats (still sRGB-encoded where the texture is).
    internal class Pixels
    {
        public int Width, Height;
        public Vector4[] Data;

        public static Pixels Load(ExportEntry texture)
        {
            try
            {
                Pixels pixels = FromArgb(texture) ?? FromPngExport(texture);
                while (pixels.Width > MaxSize)
                    pixels = pixels.Half();
                return pixels;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"  could not decode {texture.InstancedFullPath}: {ex.Message}");
                return null;
            }
        }

        // One mip of a texture, decoded from its stored data as is (TextureCache picks the
        // mip). The same texels as LEX's PNG export of that mip, minus a PNG encode and decode
        // (checked on DXT1/5, BC5, BC7; for A8R8G8B8 LEX's PNG copies gray into alpha, which no
        // bake reads). Null when it can't be decoded.
        public static Pixels FromMip(ExportEntry texture, Texture2D texture2D, Texture2DMipInfo mip)
        {
            byte[] bgra = MipBgra(texture, texture2D, mip);
            return bgra != null ? FromBgra(bgra, mip.width, mip.height) : null;
        }

        // One mip as BGRA bytes (see FromMip).
        public static byte[] MipBgra(ExportEntry texture, Texture2D texture2D, Texture2DMipInfo mip)
        {
            try
            {
                int width = mip.width, height = mip.height;
                byte[] raw = texture2D.GetImageBytesForMip(mip, texture.Game, true, out _, null, null);
                byte[] bgra = LegendaryExplorerCore.Textures.Image.convertRawToARGB(raw, ref width, ref height,
                    LegendaryExplorerCore.Textures.Image.getPixelFormatType(texture2D.TextureFormat), false);
                return bgra != null && width == mip.width && height == mip.height && bgra.Length == width * height * 4 ? bgra : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"  could not decode {texture.InstancedFullPath} at {mip.width}x{mip.height}: {ex.Message}");
                return null;
            }
        }

        private static Pixels FromBgra(byte[] bytes, int width, int height)
        {
            var pixels = new Pixels { Width = width, Height = height, Data = new Vector4[width * height] };
            for (int i = 0; i < pixels.Data.Length; i++)
                pixels.Data[i] = new Rgba32(bytes[i * 4 + 2], bytes[i * 4 + 1], bytes[i * 4], bytes[i * 4 + 3]).ToVector4();
            return pixels;
        }

        // The top mip as raw BGRA, skipping a PNG encode and decode (the same texels as the PNG
        // export for DXT1/5, BC5, and BC7); null when LEX can't, or the size doesn't add up.
        private static Pixels FromArgb(ExportEntry texture)
        {
            var texture2D = new Texture2D(texture);
            Texture2DMipInfo top = texture2D.GetTopMip();
            using var argb = new MemoryStream();
            if (top == null || !texture2D.ExportToARGB(argb) || argb.Length != (long)top.width * top.height * 4)
                return null;
            return FromBgra(argb.GetBuffer(), top.width, top.height);
        }

        // LEX's raw image path rejects some LE formats, but its PNG export handles them all.
        private static Pixels FromPngExport(ExportEntry texture)
        {
            using var png = new MemoryStream();
            if (!new Texture2D(texture).ExportToPNG(outStream: png))
                throw new InvalidDataException("PNG export failed");
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(png.ToArray());
            return FromImage(image);
        }

        // One bulk copy out of the image instead of its per-pixel indexer.
        private static Pixels FromImage(Image<Rgba32> image)
        {
            var raw = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(raw);
            var pixels = new Pixels { Width = image.Width, Height = image.Height, Data = new Vector4[raw.Length] };
            for (int i = 0; i < raw.Length; i++)
                pixels.Data[i] = raw[i].ToVector4();
            return pixels;
        }

        // Box-filtered half-size copy.
        internal Pixels Half()
        {
            var half = new Pixels { Width = Width / 2, Height = Height / 2 };
            half.Data = new Vector4[half.Width * half.Height];
            for (int y = 0; y < half.Height; y++)
            {
                for (int x = 0; x < half.Width; x++)
                {
                    int i = y * 2 * Width + x * 2;
                    half.Data[y * half.Width + x] = (Data[i] + Data[i + 1] + Data[i + Width] + Data[i + Width + 1]) / 4f;
                }
            }
            return half;
        }

        // The pixel of this texture at the same UV as (x, y) in another texture.
        public Vector4 Sample(int x, int y, Pixels other) =>
            Data[(y * Height / other.Height) * Width + x * Width / other.Width];

        // Runs a per-pixel function returning linear RGB + alpha and encodes the result as DDS.
        public MemoryImage Map(Func<int, int, Vector4, Vector4> shade)
        {
            var result = new Rgba32[Width * Height];
            Parallel.For(0, Height, ParallelLimits.Options, y =>
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    Vector4 c = shade(x, y, Data[i]);
                    result[i] = new Rgba32(Srgb(c.X), Srgb(c.Y), Srgb(c.Z), Math.Clamp(c.W, 0f, 1f));
                }
            });
            return DdsTextures.Encode(result, Width, Height, normalMap: false);
        }

        // Like Map, for data textures (normals, roughness): the values are written as-is,
        // without sRGB encoding.
        public MemoryImage MapRaw(Func<int, int, Vector4, Vector4> shade, bool normalMap = false)
        {
            var result = new Rgba32[Width * Height];
            Parallel.For(0, Height, ParallelLimits.Options, y =>
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    result[i] = new Rgba32(Vector4.Clamp(shade(x, y, Data[i]), Vector4.Zero, Vector4.One));
                }
            });
            return DdsTextures.Encode(result, Width, Height, normalMap);
        }
    }
}

internal static class VectorExtensions
{
    public static Vector3 AsVector3(this Vector4 v) => new Vector3(v.X, v.Y, v.Z);
}
