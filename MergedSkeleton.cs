using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace LexExport;

// One skeleton shared by several meshes, keyed by bone name. Bone transforms are kept in
// glTF space (converted on Add), local to their parent.
public class MergedSkeleton
{
    public record Bone(string Name, int Parent, Vector3 Translation, Quaternion Rotation);

    private readonly List<Bone> _bones = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Bone> Bones => _bones;

    public void Add(MeshBone[] refSkeleton)
    {
        for (int i = 0; i < refSkeleton.Length; i++)
        {
            MeshBone b = refSkeleton[i];
            string name = b.Name.Instanced;
            if (_index.ContainsKey(name))
                continue;

            // ParentIndex == own index marks the root in UE3.
            int parent = b.ParentIndex == i ? -1 : IndexOf(refSkeleton[b.ParentIndex].Name.Instanced);
            _index[name] = _bones.Count;
            // RefSkeleton orientations are used as-is: unlike PSK files, they need no
            // conjugation for child bones (verified with MeshCommand's bind check).
            _bones.Add(new Bone(name, parent, UnrealSpace.Position(b.Position), UnrealSpace.Rotation(b.Orientation)));
        }
    }

    public int IndexOf(string name) =>
        _index.TryGetValue(name, out int i) ? i : throw new KeyNotFoundException($"Bone '{name}' not in skeleton.");

    public bool Contains(string name) => _index.ContainsKey(name);

    public Matrix4x4 LocalMatrix(int i) =>
        Matrix4x4.CreateFromQuaternion(_bones[i].Rotation) * Matrix4x4.CreateTranslation(_bones[i].Translation);

    // System.Numerics uses row vectors, so world = local * parentWorld.
    public Matrix4x4 BindWorld(int i) =>
        _bones[i].Parent < 0 ? LocalMatrix(i) : LocalMatrix(i) * BindWorld(_bones[i].Parent);

    public Vector3 BindWorldPosition(int i) => BindWorld(i).Translation;

    // Builds glTF nodes for every bone under an "Armature" root, returned in bone index order
    // (the order VertexJoints4 indices refer to).
    public NodeBuilder[] BuildNodes(out NodeBuilder armature)
    {
        armature = new NodeBuilder("Armature");
        var nodes = new NodeBuilder[_bones.Count];
        for (int i = 0; i < _bones.Count; i++)
        {
            Bone b = _bones[i];
            NodeBuilder parent = b.Parent < 0 ? armature : nodes[b.Parent];
            nodes[i] = parent.CreateNode(b.Name);
            nodes[i].LocalTransform = new AffineTransform(null, b.Rotation, b.Translation);
        }
        return nodes;
    }
}
