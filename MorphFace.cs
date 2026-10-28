using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using BioMorphFace = LegendaryExplorerCore.Unreal.Classes.BioMorphFace;

namespace LexExport;

// A BioMorphFace reshapes its base head in two parts: new vertex positions (the sliders' morph
// targets) and m_aFinalSkeleton, new local positions for the face bones (eyes, lids, brows,
// lips, cheeks). The game skins the morphed vertices with the base head's bind pose but poses
// the final skeleton, so each moved bone carries its vertices on top of the vertex morph. The
// two don't overlap: the eyeballs get all of their placement from the bones (the morph moves
// their vertices 0.2 mm, their bones 1.2 cm on sta60_amb_genericmale01), and a brow can move
// down in the vertices and up in the bones. LEX's Apply() sets the vertices only, which left
// every eye in front of its lids.
public static class MorphFace
{
    // The morph face's head with both parts applied, plus its hair (which can be skinned to
    // face bones too).
    public static SkeletalMesh Apply(BioMorphFace morph)
    {
        SkeletalMesh head = morph.Apply();
        int moved = ApplyBoneOffsets(head, morph.BoneOffsets);
        if (morph.HairMesh != null)
            ApplyBoneOffsets(morph.HairMesh, morph.BoneOffsets);
        Console.WriteLine($"  morph {morph.Export.ObjectName.Instanced}: {moved}/{morph.BoneOffsets.Count} face bones moved.");
        return head;
    }

    // Moves the mesh's bones named in the offsets to their final local positions and its LOD 0
    // vertices with them, so the bind pose is the final skeleton and the bones pivot where the
    // game's do. A mesh whose bones already sit there is left as it is, so applying the offsets
    // to every part of a model is safe. Returns how many bones moved.
    public static int ApplyBoneOffsets(SkeletalMesh mesh, IReadOnlyList<BoneOffset> offsets)
    {
        MeshBone[] bones = mesh.RefSkeleton;
        Matrix4x4[] before = WorldPose(bones);
        int moved = 0;
        foreach (BoneOffset offset in offsets)
        {
            MeshBone bone = bones.FirstOrDefault(b => b.Name.Instanced.Equals(offset.Name, StringComparison.OrdinalIgnoreCase));
            if (bone == null || Vector3.DistanceSquared(bone.Position, offset.Position) < 1e-8f)
                continue;
            bone.Position = offset.Position;
            moved++;
        }
        if (moved == 0)
            return 0;

        // Offsets only translate, so each bone's change from bind to final is a translation
        // too; the normals and tangents keep their directions.
        Matrix4x4[] after = WorldPose(bones);
        var shift = new Vector3[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            shift[i] = after[i].Translation - before[i].Translation;

        StaticLODModel lod = mesh.LODModels[0];
        GPUSkinVertex[] verts = lod.VertexBufferGPUSkin.VertexData;
        foreach (SkelMeshChunk chunk in lod.Chunks)
        {
            int count = chunk.NumRigidVertices + chunk.NumSoftVertices;
            for (int v = (int)chunk.BaseVertexIndex; v < chunk.BaseVertexIndex + count; v++)
            {
                GPUSkinVertex vertex = verts[v];
                Vector3 move = Vector3.Zero;
                for (int k = 0; k < 4; k++)
                    move += shift[chunk.BoneMap[vertex.InfluenceBones[k]]] * (vertex.InfluenceWeights[k] / 255f);
                vertex.Position += move;
                verts[v] = vertex;
            }
        }
        return moved;
    }

    // Each bone's transform in mesh space (row vectors: a child's local, then its parent's).
    private static Matrix4x4[] WorldPose(MeshBone[] bones)
    {
        var world = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(bones[i].Orientation) * Matrix4x4.CreateTranslation(bones[i].Position);
            world[i] = i == 0 ? local : local * world[bones[i].ParentIndex];
        }
        return world;
    }
}
