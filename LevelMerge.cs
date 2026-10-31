using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace LexExport;

using MERGED_VERTEX = VertexBuilder<VertexPositionNormalTangent, VertexMerged, VertexEmpty>;

// Merged chunks: Godot doesn't batch, so every surface of every mesh instance is a draw call of
// its own, and the Wards' static meshes alone were about 2,500 of them, each with its own instance
// uniforms. The surfaces drawn with ME1's lightmaps (texture or vertex) are merged instead, in
// world space, into one mesh per package, material, lightmap (p<page> for a texture page, v for
// vertex-lit), and world cell (MergeCell meters a side, so frustum culling still has pieces to
// cull). The unlit opaque sections on those meshes (emissive panels) are merged the same way, as
// u, whatever their instance's lighting:
//   Merged/<package>/<material>_<lightmap>_<x>_<y>_<z>
// with extras "me_lightmap" { type (texture, vertex, or none for u), page, merged: true },
// "lightmap_uv": false (merged levels use
// ME1's lightmaps, not Godot bakes), and "pieces", the piece index: [{ name, package,
// vertices: [start, count], bounds: [min xyz, max xyz] }], one per instance, so a level mesh can
// still be edited (a piece's triangles are those whose vertices fall in its range, since Godot's
// import may reorder the indices but keeps the vertices in order).
//
// What each instance carried on its node goes into its vertices:
//   - texture-lit: UV2 is the atlas position (lightmap UV × CoordinateScale + CoordinateBias), so
//     the shaders' scale and bias stay at identity.
//   - vertex-lit: the samples as before, COLOR_0 and TEXCOORD_2/3 (Godot's CUSTOM0).
//   - both: scale0 and scale1 in TEXCOORD_4/5 (CUSTOM1: scale0.xyz, scale1.x) and TEXCOORD_6
//     (CUSTOM2: scale1.yz). Godot reads CUSTOM<n> from TEXCOORD_(2 + 2n) and _(3 + 2n), and stops
//     at the first one missing, so texture-lit chunks carry an empty CUSTOM0.
//
// Left separate, as before: glow and translucent sections (Godot sorts those per object),
// materials whose translated shader reads the object's own transform or the second UV set (both
// change when a piece is merged), meshes ME1 didn't lightmap (LightEnvironment lights those as
// props), movers, placeables' effects, decals, and everything skeletal.
public partial class LevelBuilder
{
    public const float DefaultMergeCell = 32f;

    // The merged chunks' cell size, in meters; 0 leaves every mesh separate (levels.json "merge": false).
    public float MergeCell { get; set; } = DefaultMergeCell;

    // One instance whose sections join chunks: where it is and its original lighting.
    private sealed class MergeInstance
    {
        public string Name;
        public MeshGeometry Geometry;
        public Matrix4x4 World;                  // Unreal space
        public int Uv2;
        public int Page;                         // -1: vertex-lit
        public Vector4 ScaleBias;
        public Vector3 Scale0, Scale1;
        public byte[] Samples;
        public MERGED_VERTEX[] Vertices;         // in world space, built when its first chunk is written
    }

    // A chunk: its material, lightmap ("p<page>", "v", or "u" for unlit surfaces), and cell.
    private readonly record struct ChunkKey(int Material, string Lightmap, int X, int Y, int Z);

    private readonly NodeBuilder _mergedNode;
    // The current package's chunks, in the order they were started, each with its pieces' sections.
    private readonly Dictionary<ChunkKey, List<(MergeInstance Instance, List<int> Sections)>> _chunks = new();
    private readonly List<ChunkKey> _chunkOrder = new();
    // Materials by identity, for the chunk keys, and whether each can be merged.
    private readonly Dictionary<MaterialBuilder, int> _materialIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<MaterialBuilder> _materialsById = new();
    private readonly Dictionary<MaterialBuilder, MergeKind> _mergeable = new(ReferenceEqualityComparer.Instance);
    private int _mergeSurfaces, _mergeChunks, _mergeSeparate, _mergeSharedPieces;

    // Translated code that reads the object's transform, its bounds, the second UV set
    // (in_uv.zw), or UV channels 2 and 3 (which only separate meshes carry; see BuildRender).
    private static readonly Regex InstanceDependent = new(@"local_to_world|world_to_local|object_bounds|in_uv_extra|in_uv\.[xyzw]*[zw]");

    // Whether any of a material's translations reads a name (an input, uniform, or constant).
    private static bool Reads(MaterialBuilder material, string name)
    {
        if (material?.Extras is not JsonObject extras || extras["me_material"] is not JsonObject translated)
            return false;
        foreach (string policy in new[] { "texture", "vertex", "unlit", "environment" })
        {
            if (translated[policy]?["code"]?.GetValue<string>() is string code && code.Contains(name))
                return true;
        }
        return false;
    }

    // Adds an instance's mergeable sections to the current package's chunks and returns the
    // section materials left for its own node (merged ones null). merged: any section was.
    private MaterialBuilder[] AddMergePiece(string name, MeshGeometry geometry, MaterialBuilder[] materials, Matrix4x4 world,
        int uv2, int page, Vector4 scaleBias, Vector3 scale0, Vector3 scale1, byte[] samples, out bool merged)
    {
        merged = false;
        if (MergeCell <= 0f || !materials.Any(m => MergeKindOf(m) != MergeKind.None))
            return materials;

        // The cell holding the instance's middle, so all its pieces land in the same one.
        Matrix4x4 toGltf = UnrealTransform.ToGltf(world);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in geometry.GltfPositions)
        {
            Vector3 at = Vector3.Transform(p, toGltf);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        Vector3 cell = (min + max) * 0.5f / MergeCell;
        var instance = new MergeInstance
        {
            Name = name,
            Geometry = geometry,
            World = world,
            Uv2 = uv2,
            Page = page,
            ScaleBias = scaleBias,
            Scale0 = scale0,
            Scale1 = scale1,
            Samples = samples,
        };

        var own = (MaterialBuilder[])materials.Clone();
        for (int s = 0; s < materials.Length; s++)
        {
            MergeKind kind = MergeKindOf(materials[s]);
            if (kind == MergeKind.None)
                continue;
            if (!_materialIds.TryGetValue(materials[s], out int id))
            {
                _materialIds[materials[s]] = id = _materialsById.Count;
                _materialsById.Add(materials[s]);
            }
            // Unlit surfaces ignore the lightmap, so pieces of any lighting share their chunks.
            string lightmap = kind == MergeKind.Unlit ? "u" : page >= 0 ? $"p{page}" : "v";
            var key = new ChunkKey(id, lightmap, (int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z));
            if (!_chunks.TryGetValue(key, out var pieces))
            {
                _chunks[key] = pieces = new List<(MergeInstance, List<int>)>();
                _chunkOrder.Add(key);
            }
            // An instance's sections of one material are one piece.
            if (pieces.Count > 0 && pieces[^1].Instance == instance)
                pieces[^1].Sections.Add(s);
            else
                pieces.Add((instance, new List<int> { s }));
            own[s] = null;
            merged = true;
            _mergeSurfaces++;
        }
        return own;
    }

    private enum MergeKind { None, Lit, Unlit }

    // Which chunks a section can join: lit opaque or cut-out surfaces (the ones drawn with ME1's
    // lightmaps), or unlit opaque ones (emissive panels), which
    // need no sorting either. Not when the translated shader they draw with depends on the instance.
    private MergeKind MergeKindOf(MaterialBuilder material)
    {
        if (material == null)
            return MergeKind.None;
        if (_mergeable.TryGetValue(material, out MergeKind known))
            return known;
        bool unlit = material.ShaderStyle == MaterialBuilder.SHADERUNLIT;
        MergeKind kind = material.AlphaMode == AlphaMode.BLEND ? MergeKind.None : unlit ? MergeKind.Unlit : MergeKind.Lit;
        if (kind != MergeKind.None && material.Extras is JsonObject extras)
        {
            if (extras.ContainsKey("me_shader"))
                kind = MergeKind.None;
            if (extras["me_material"] is JsonObject translated)
            {
                foreach (string policy in unlit ? new[] { "unlit" } : new[] { "texture", "vertex" })
                {
                    if (translated[policy]?["code"]?.GetValue<string>() is string code && InstanceDependent.IsMatch(code))
                        kind = MergeKind.None;
                }
            }
        }
        _mergeable[material] = kind;
        return kind;
    }

    // Writes the current package's chunks under Merged/<package> and starts the next package's.
    private void WriteMergedChunks(string packageName)
    {
        if (_chunkOrder.Count == 0)
            return;
        NodeBuilder packageNode = _mergedNode.CreateNode(packageName);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChunkKey key in _chunkOrder)
        {
            var pieces = _chunks[key];
            MaterialBuilder material = _materialsById[key.Material];
            string name = $"{material.Name}_{key.Lightmap}_{key.X}_{key.Y}_{key.Z}";
            for (int n = 2; !names.Add(name); n++)
                name = $"{material.Name}_{key.Lightmap}_{key.X}_{key.Y}_{key.Z}_{n}";

            var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexMerged, VertexEmpty>(name);
            var primitive = mesh.UsePrimitive(material);
            var index = new JsonArray();
            foreach ((MergeInstance instance, List<int> sections) in pieces)
            {
                instance.Vertices ??= MergedVertices(instance);
                MeshGeometry g = instance.Geometry;
                ushort[] indices = g.Lod.IndexBuffer;
                // Mirrored instances reverse the apparent winding, which Godot no longer sees.
                bool flip = g.FlipWinding ^ (instance.World.GetDeterminant() < 0);
                int start = primitive.Vertices.Count;
                bool shared = false;
                foreach (int s in sections)
                {
                    StaticMeshElement element = g.Lod.Elements[s];
                    for (int t = 0; t < element.NumTriangles; t++)
                    {
                        long i = element.FirstIndex + t * 3L;
                        if (!g.Draws(i))
                            continue;
                        var a = instance.Vertices[indices[i]];
                        var b = instance.Vertices[indices[i + 1]];
                        var c = instance.Vertices[indices[i + 2]];
                        var (ia, ib, ic) = flip ? primitive.AddTriangle(a, c, b) : primitive.AddTriangle(a, b, c);
                        // A vertex equal to an earlier piece's is shared with it, outside this range.
                        if ((ia >= 0 && ia < start) || (ib >= 0 && ib < start) || (ic >= 0 && ic < start))
                            shared = true;
                    }
                }
                int count = primitive.Vertices.Count - start;
                if (shared)
                    _mergeSharedPieces++;
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (int v = start; v < start + count; v++)
                {
                    min = Vector3.Min(min, primitive.Vertices[v].Geometry.Position);
                    max = Vector3.Max(max, primitive.Vertices[v].Geometry.Position);
                }
                index.Add(new JsonObject
                {
                    ["name"] = instance.Name,
                    ["package"] = packageName,
                    ["vertices"] = new JsonArray(start, count),
                    ["bounds"] = count > 0 ? new JsonArray(min.X, min.Y, min.Z, max.X, max.Y, max.Z) : new JsonArray(),
                });
            }
            if (primitive.Triangles.Count == 0)
                continue;

            // Unlit chunks are "none": they keep their own (or their translated unlit) material.
            string type = key.Lightmap switch { "v" => "vertex", "u" => "none", _ => "texture" };
            var lighting = new JsonObject { ["type"] = type, ["merged"] = true };
            if (type == "texture")
                lighting["page"] = int.Parse(key.Lightmap[1..]);
            var node = new NodeBuilder(name, new JsonObject
            {
                ["me_lightmap"] = lighting,
                ["lightmap_uv"] = false,
                ["pieces"] = index,
            });
            packageNode.AddNode(node);
            _scene.AddRigidMesh(mesh, node);
            _mergeChunks++;
        }
        _chunks.Clear();
        _chunkOrder.Clear();
    }

    // An instance's vertices in world space (glTF): positions and the tangent frame transformed
    // (normals by the inverse transpose, for non-uniform scale; a mirrored instance's bitangent
    // sign flips), UV2 at its place in the lightmap atlas, and its lightmap scales per vertex.
    private static MERGED_VERTEX[] MergedVertices(MergeInstance instance)
    {
        MeshGeometry g = instance.Geometry;
        Matrix4x4 toGltf = UnrealTransform.ToGltf(instance.World);
        Matrix4x4.Invert(toGltf, out Matrix4x4 inverse);
        Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverse);
        float handedness = toGltf.GetDeterminant() < 0 ? -1f : 1f;
        var scales0 = new Vector2(instance.Scale0.X, instance.Scale0.Y);
        var scales1 = new Vector2(instance.Scale0.Z, instance.Scale1.X);
        var scales2 = new Vector2(instance.Scale1.Y, instance.Scale1.Z);

        var vertices = new MERGED_VERTEX[g.GltfPositions.Length];
        for (int v = 0; v < vertices.Length; v++)
        {
            Vector3 position = Vector3.Transform(g.GltfPositions[v], toGltf);
            Vector3 normal = MeshCommand.SafeNormalize(Vector3.TransformNormal(g.Normals[v], normalMatrix));
            Vector4 t = g.Tangents[v];
            Vector3 tangent = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), toGltf);
            tangent -= normal * Vector3.Dot(normal, tangent);
            tangent = tangent.LengthSquared() > 1e-8f ? Vector3.Normalize(tangent) : AnyPerpendicular(normal);

            Vector2 lightmapUv = g.UVs[instance.Uv2][v];
            Vector4 color = Vector4.One;
            Vector2 directionalXY = Vector2.Zero, directionalZ = Vector2.Zero;
            if (instance.Samples == null)
            {
                lightmapUv = lightmapUv * new Vector2(instance.ScaleBias.X, instance.ScaleBias.Y)
                    + new Vector2(instance.ScaleBias.Z, instance.ScaleBias.W);
            }
            else
            {
                color = Unit(instance.Samples, v * 8);
                Vector4 directional = Unit(instance.Samples, v * 8 + 4);
                directionalXY = new Vector2(directional.X, directional.Y);
                directionalZ = new Vector2(directional.Z, 0f);
            }
            vertices[v] = new MERGED_VERTEX(
                new VertexPositionNormalTangent(position, normal, new Vector4(tangent, t.W * handedness)),
                new VertexMerged(color, g.UVs[0][v], lightmapUv, directionalXY, directionalZ, scales0, scales1, scales2));
        }
        return vertices;
    }

    private string MergeSummary() => MergeCell <= 0f
        ? "Merged: off (every mesh separate)."
        : $"Merged: {_mergeSurfaces} surfaces into {_mergeChunks} chunks ({MergeCell:0.#} m cells), {_mergeSeparate} lightmapped left separate"
            + (_mergeSharedPieces > 0 ? $"; {_mergeSharedPieces} pieces share vertices with an earlier piece (their ranges miss them)." : ".");
}

// A merged chunk's vertex attributes beyond position and tangent frame: COLOR_0 (a vertex
// lightmap's color map; white when texture-lit) and TEXCOORD_0 to _6 (see LevelMerge above).
// SharpGLTF writes the first four texture coordinates itself and asks for the rest as custom
// attributes.
public struct VertexMerged : IVertexCustom, IEquatable<VertexMerged>
{
    public Vector4 Color;
    public Vector2 TexCoord0, TexCoord1, TexCoord2, TexCoord3, TexCoord4, TexCoord5, TexCoord6;

    private static readonly string[] Custom = ["TEXCOORD_4", "TEXCOORD_5", "TEXCOORD_6"];

    public VertexMerged(Vector4 color, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3, Vector2 uv4, Vector2 uv5, Vector2 uv6)
    {
        Color = color;
        (TexCoord0, TexCoord1, TexCoord2, TexCoord3, TexCoord4, TexCoord5, TexCoord6) = (uv0, uv1, uv2, uv3, uv4, uv5, uv6);
    }

    readonly IEnumerable<KeyValuePair<string, AttributeFormat>> IVertexReflection.GetEncodingAttributes()
    {
        yield return new("COLOR_0", new AttributeFormat(SharpGLTF.Schema2.DimensionType.VEC4, SharpGLTF.Schema2.EncodingType.UNSIGNED_BYTE, true));
        for (int i = 0; i < 7; i++)
            yield return new($"TEXCOORD_{i}", new AttributeFormat(SharpGLTF.Schema2.DimensionType.VEC2));
    }

    public readonly int MaxColors => 1;

    public readonly int MaxTextCoords => 4;

    public readonly IEnumerable<string> CustomAttributes => Custom;

    public readonly Vector4 GetColor(int index) => index == 0 ? Color : throw new ArgumentOutOfRangeException(nameof(index));

    public readonly Vector2 GetTexCoord(int index) => index switch
    {
        0 => TexCoord0,
        1 => TexCoord1,
        2 => TexCoord2,
        3 => TexCoord3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public void SetColor(int setIndex, Vector4 color)
    {
        if (setIndex == 0)
            Color = color;
    }

    public void SetTexCoord(int setIndex, Vector2 coord)
    {
        switch (setIndex)
        {
            case 0: TexCoord0 = coord; break;
            case 1: TexCoord1 = coord; break;
            case 2: TexCoord2 = coord; break;
            case 3: TexCoord3 = coord; break;
        }
    }

    public readonly bool TryGetCustomAttribute(string attributeName, out object value)
    {
        value = attributeName switch
        {
            "TEXCOORD_4" => TexCoord4,
            "TEXCOORD_5" => TexCoord5,
            "TEXCOORD_6" => TexCoord6,
            _ => null,
        };
        return value != null;
    }

    public void SetCustomAttribute(string attributeName, object value)
    {
        switch (attributeName)
        {
            case "TEXCOORD_4": TexCoord4 = (Vector2)value; break;
            case "TEXCOORD_5": TexCoord5 = (Vector2)value; break;
            case "TEXCOORD_6": TexCoord6 = (Vector2)value; break;
        }
    }

    public readonly void Validate()
    {
    }

    // Morph targets only; level meshes have none.
    public readonly VertexMaterialDelta Subtract(IVertexMaterial baseValue) => throw new NotSupportedException();

    public void Add(in VertexMaterialDelta delta) => throw new NotSupportedException();

    public readonly bool Equals(VertexMerged other) => Color == other.Color
        && TexCoord0 == other.TexCoord0 && TexCoord1 == other.TexCoord1 && TexCoord2 == other.TexCoord2 && TexCoord3 == other.TexCoord3
        && TexCoord4 == other.TexCoord4 && TexCoord5 == other.TexCoord5 && TexCoord6 == other.TexCoord6;

    public override readonly bool Equals(object obj) => obj is VertexMerged other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Color, TexCoord0, TexCoord1, TexCoord2, TexCoord3, TexCoord4, TexCoord5, TexCoord6);
}
