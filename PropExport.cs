using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace LexExport;

using COLLISION_VERTEX = VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>;
using SCREEN_VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>;

// One StaticMesh as a prop the game places itself (props.json: a quest's terminal), in the mesh's
// own frame:
//
//   PropAsset/<mesh>         the mesh, its materials baked from their compiled static-mesh
//                            shaders (ShaderBaker on FLocalVertexFactory) like a character's:
//                            base color, normal, specular, and the emissive glow on its own
//                            texture, so the game can switch a screen on and off
//   PropAsset/Collision/Hull_<n>  the mesh's simplified collision (its BodySetup's convex
//                            hulls), when it has any; the game boxes its bounds otherwise
//   PropAsset/Screens/<state>  with the entry's "screen": a quad over the prop's own screen
//                            per state ("repaired", "broken"), carrying one of the games'
//                            hologram textures, its tint in the extras ({ "screen", "tint" });
//                            the game draws it additive and shows the one its state wants.
//                            Terminal01's screen is only painted dark on its front face, so the
//                            quad's corners (glTF, the prop's frame) come from the manifest.
//
// It shares the level export's mesh reader (ReadGeometry, BuildRender), so a prop reads exactly
// as the same mesh does when a level places it.
public partial class LevelBuilder
{
    // Props stand a meter or two from the player: half the characters' 2048 px is plenty.
    private const int PropTextureSize = 1024;

    // screen: the manifest entry's "screen" ({ "corners": four [x, y, z] from the bottom left,
    // counterclockwise as seen from the front, "states": { <state>: { "texture", "tint" } } }),
    // or an undefined element for none.
    public static void ExportProp(string package, string meshPath, string outPath, JsonElement screen = default)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        ExportEntry meshExport = pcc.Exports.FirstOrDefault(e => e.ClassName == "StaticMesh"
            && e.InstancedFullPath.Equals(meshPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"No StaticMesh {meshPath} in {Path.GetFileName(package)}.");

        TextureCache textures = new TextureCache(pcc, [])
        {
            BakeShaders = true,
            BakeVertexFactory = "FLocalVertexFactory",
            MaxTextureSize = PropTextureSize,
        };
        MeshGeometry geometry = ReadGeometry(pcc, meshExport, textures);
        IMeshBuilder<MaterialBuilder> render = BuildRender(geometry, geometry.SectionMaterials, 0, null)
            ?? throw new InvalidDataException($"{meshPath} draws no sections.");

        SceneBuilder scene = new SceneBuilder();
        NodeBuilder asset = new NodeBuilder("PropAsset", new JsonObject { ["prop_asset"] = meshExport.ObjectName.Instanced });
        scene.AddRigidMesh(render, asset.CreateNode(geometry.Name));

        // The convex hulls whatever UseSimpleBoxCollision says: the level only skips them for
        // meshes it collides per triangle, which a prop never does.
        List<Vector3[]> hulls = ReadBodySetup(pcc, meshExport.GetProperties());
        NodeBuilder collision = asset.CreateNode("Collision");
        MaterialBuilder collisionMaterial = new MaterialBuilder("Collision");
        for (int n = 0; n < hulls.Count; n++)
        {
            COLLISION_VERTEX[] points = hulls[n].Select(p => new COLLISION_VERTEX(UnrealSpace.Position(p))).ToArray();
            MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty> hull = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>($"Hull_{n}");
            PrimitiveBuilder<MaterialBuilder, VertexPosition, VertexEmpty, VertexEmpty> primitive = hull.UsePrimitive(collisionMaterial);
            for (int i = 1; i + 1 < points.Length; i++)
                primitive.AddTriangle(points[0], points[i], points[i + 1]);
            scene.AddRigidMesh(hull, collision.CreateNode(hull.Name));
        }

        int screens = screen.ValueKind == JsonValueKind.Object ? AddScreens(scene, asset, pcc, textures, screen) : 0;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        SharpGLTF.Schema2.ModelRoot model = scene.ToGltf2(Program.GltfSettings);
        string dds = DdsTextures.Convert(model);
        OutputFiles.Write(outPath, temporary => model.SaveGLB(temporary, Program.GlbSettings));
        Console.WriteLine($"Wrote {outPath} ({geometry.Name}: {geometry.GltfPositions.Length} vertices, "
            + $"{geometry.Lod.Elements.Length} sections, {hulls.Count} collision hulls, {screens} screens; {dds}).");
        ShaderAnalysis.Forget(pcc);
    }

    // One quad per screen state, with its texture as stored (a hologram's grayscale or colored
    // image; the tint is applied in game).
    private static int AddScreens(SceneBuilder scene, NodeBuilder asset, IMEPackage pcc, TextureCache textures, JsonElement screen)
    {
        Vector3[] corners = screen.GetProperty("corners").EnumerateArray()
            .Select(c => new Vector3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle())).ToArray();
        if (corners.Length != 4)
            throw new InvalidDataException("A prop's screen needs four corners.");
        Vector3 normal = Vector3.Normalize(Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]));
        Vector2[] uvs = [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)];
        SCREEN_VERTEX[] vertices = corners.Select((c, i) => new SCREEN_VERTEX(new VertexPositionNormal(c, normal), new VertexTexture1(uvs[i]))).ToArray();

        NodeBuilder parent = asset.CreateNode("Screens");
        int count = 0;
        foreach (JsonProperty state in screen.GetProperty("states").EnumerateObject())
        {
            string texturePath = state.Value.GetProperty("texture").GetString();
            ExportEntry texture = pcc.Exports.FirstOrDefault(e => e.ClassName == "Texture2D"
                && e.InstancedFullPath.Equals(texturePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"No Texture2D {texturePath} in {Path.GetFileName(pcc.FilePath)}.");
            MemoryImage image = textures.Decode(texture) ?? throw new InvalidDataException($"Could not decode {texturePath}.");
            MaterialBuilder material = new MaterialBuilder($"Screen_{state.Name}").WithUnlitShader().WithDoubleSide(true)
                .WithAlpha(AlphaMode.BLEND).WithChannelImage(KnownChannel.BaseColor, image);
            MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> quad = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>($"Screen_{state.Name}");
            PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> primitive = quad.UsePrimitive(material);
            primitive.AddTriangle(vertices[0], vertices[1], vertices[2]);
            primitive.AddTriangle(vertices[0], vertices[2], vertices[3]);
            JsonArray tint = state.Value.TryGetProperty("tint", out JsonElement t)
                ? new JsonArray(t.EnumerateArray().Select(v => (JsonNode)v.GetSingle()).ToArray())
                : new JsonArray(1f, 1f, 1f);
            NodeBuilder node = new NodeBuilder(state.Name, new JsonObject { ["screen"] = state.Name, ["tint"] = tint });
            parent.AddNode(node);
            scene.AddRigidMesh(quad, node);
            count++;
        }
        return count;
    }
}
