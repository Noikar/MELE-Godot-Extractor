using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using BioMorphFace = LegendaryExplorerCore.Unreal.Classes.BioMorphFace;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace LexExport;

using VERTEX = VertexBuilder<VertexPositionNormalTangent, VertexTexture1, VertexJoints4>;

// Exports one or more SkeletalMeshes (e.g. body + head) from a package into a single skinned
// .glb. All meshes share one skeleton, merged by bone name: the first mesh's skeleton is the
// base, and bones only a later mesh has (facial bones on a head) are appended under their
// named parent.
//
// Geometry is read straight from LOD 0's GPU-skin vertex buffer and index buffer rather than
// through LEX's PSK converter, which misreads LE's 32-bit index buffers.
public static class MeshCommand
{
    // args: mesh paths, then any number of "--anim <AnimSequence path>[=ClipName][@package.pcc]"
    // options and "--check <AnimSequence path>[@package.pcc]" (prints the animation-convention
    // check and exits). "@package.pcc" pulls the clip from another package in the same folder
    // (or "@ME2/package.pcc" from another game's), for shared skeletons (e.g. a human-male
    // idle on a salarian). "--bind <Bone,Bone,...>" after an --anim holds those bones at their bind pose in that clip. "--mat <MaterialInstance>,..."
    // after a mesh replaces its materials in slot order (another color variant of the same
    // armor). "--morph <BioMorphFace>" adds a morph-target head (the base head with the
    // character's face applied), its hair, and the character's appearance overrides.
    // "--hide <name>" drops sections whose material name contains it (e.g. eyelash cards whose
    // material lives in another package and would render as opaque flaps). "--override
    // <BioMaterialOverride>[@package.pcc]" applies an NPC's own appearance (its pawn type's clothing tints
    // and skin tone) on top of the morph face's. "--sounds" also exports the sounds the clips'
    // AnimNotify_Sounds play (a gun's fold clips) as .ogg files beside the .glb, listed on the
    // Armature node's extras as "clip_sounds": clip -> [{ time, file, volume, min_radius, max_radius }].
    // "--vfx <BioWeaponVFXAppearance>" adds a gun's effects (WeaponVfx).
    public static int Run(string packagePath, string outPath, string[] args)
    {
        var meshPaths = new List<string>();
        var materialSwaps = new Dictionary<int, string[]>();
        var morphs = new List<string>();
        var hidden = new List<string>();
        var pawnOverrides = new List<string>();
        var anims = new List<(string Path, string Clip, string Package, List<string> Bind)>();
        (string Path, string Package)? check = null;
        bool sounds = false;
        string vfx = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--sounds")
                sounds = true;
            else if (args[i] == "--vfx" && i + 1 < args.Length)
                vfx = args[++i];
            else if (args[i] == "--anim" && i + 1 < args.Length)
            {
                string[] at = args[++i].Split('@', 2);
                string[] eq = at[0].Split('=', 2);
                anims.Add((eq[0], eq.Length > 1 ? eq[1] : eq[0].Split('.').Last(), at.Length > 1 ? at[1] : null, new List<string>()));
            }
            else if (args[i] == "--bind" && i + 1 < args.Length && anims.Count > 0)
                anims[^1].Bind.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else if (args[i] == "--mat" && i + 1 < args.Length && meshPaths.Count > 0)
                materialSwaps[meshPaths.Count - 1] = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else if (args[i] == "--morph" && i + 1 < args.Length)
                morphs.Add(args[++i]);
            else if (args[i] == "--hide" && i + 1 < args.Length)
                hidden.Add(args[++i]);
            else if (args[i] == "--override" && i + 1 < args.Length)
                pawnOverrides.Add(args[++i]);
            else if (args[i] == "--check" && i + 1 < args.Length)
            {
                string[] at = args[++i].Split('@', 2);
                check = (at[0], at.Length > 1 ? at[1] : null);
            }
            else
                meshPaths.Add(args[i]);
        }

        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(packagePath);
        var skeleton = new MergedSkeleton();
        var parts = new List<(string Name, SkeletalMesh Mesh)>();
        var textures = new TextureCache(pcc, hidden) { BakeShaders = true };

        for (int m = 0; m < meshPaths.Count; m++)
        {
            ExportEntry export = FindExport(pcc, meshPaths[m], "SkeletalMesh");
            var mesh = ObjectBinary.From<SkeletalMesh>(export);
            if (materialSwaps.TryGetValue(m, out string[] swaps))
            {
                for (int s = 0; s < swaps.Length && s < mesh.Materials.Length; s++)
                    mesh.Materials[s] = FindExport(pcc, swaps[s], null).UIndex;
            }
            skeleton.Add(mesh.RefSkeleton);
            parts.Add((export.ObjectName.Instanced, mesh));
        }

        foreach (string morphPath in morphs)
        {
            ExportEntry export = FindExport(pcc, morphPath, "BioMorphFace");
            var morph = new BioMorphFace(export);
            SkeletalMesh head = morph.Apply();
            skeleton.Add(head.RefSkeleton);
            parts.Add((morph.m_oBaseHead.ObjectName.Instanced, head));
            if (morph.HairMesh != null)
            {
                skeleton.Add(morph.HairMesh.RefSkeleton);
                parts.Add((morph.m_oHairMesh.ObjectName.Instanced, morph.HairMesh));
            }
            int overrides = export.GetProperties().GetProp<ObjectProperty>("m_oMaterialOverrides")?.Value ?? 0;
            if (pcc.TryGetUExport(overrides, out ExportEntry overrideExport))
                textures.AddOverrides(overrideExport);
        }


        // Other packages holding clips, opened once each.
        var animPackages = new Dictionary<string, IMEPackage>(StringComparer.OrdinalIgnoreCase);
        IMEPackage PackageFor(string file)
        {
            if (file == null)
                return pcc;
            if (!animPackages.TryGetValue(file, out IMEPackage other))
                animPackages[file] = other = MEPackageHandler.OpenMEPackage(Path.Combine(Path.GetDirectoryName(packagePath)!, Program.ResolvePackage(file)));
            return other;
        }

        try
        {
            // Added after the morph faces', so the pawn's own colors win, except the face's skin
            // tone. "@package.pcc" reads it from another package (ME2 keeps pawns in the BioD
            // level, meshes in the BioP).
            foreach (string overridePath in pawnOverrides)
            {
                string[] at = overridePath.Split('@', 2);
                textures.AddOverrides(FindExport(PackageFor(at.Length > 1 ? at[1] : null), at[0], "BioMaterialOverride"), keepSkinTone: true);
            }

            if (check != null)
            {
                AnimationBaker.ReportConvention(PackageFor(check.Value.Package), check.Value.Path, skeleton);
                return 0;
            }

            NodeBuilder[] joints = skeleton.BuildNodes(out NodeBuilder armature);
            var extras = new JsonObject();
            var clipSounds = new JsonObject();
            var soundCache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
            foreach (var (animPath, clip, package, bind) in anims)
            {
                AnimationBaker.Bake(PackageFor(package), animPath, clip, skeleton, joints, inPlace: true, bind);
                if (sounds && SoundExport.ClipSounds(PackageFor(package), FindExport(PackageFor(package), animPath, "AnimSequence"), outDir, soundCache) is JsonArray played)
                    clipSounds[clip] = played;
            }
            if (clipSounds.Count > 0)
                extras["clip_sounds"] = clipSounds;
            if (vfx != null)
                extras["weapon_vfx"] = WeaponVfx.Export(pcc, vfx, outDir);
            if (extras.Count > 0)
                armature.Extras = extras;
            return Write(outPath, skeleton, parts, armature, joints, textures);
        }
        finally
        {
            foreach (IMEPackage other in animPackages.Values)
                other.Dispose();
            ShaderAnalysis.Forget(pcc);
        }
    }

    private static ExportEntry FindExport(IMEPackage pcc, string path, string className)
    {
        ExportEntry export = pcc.FindExport(path)
            ?? throw new ArgumentException($"No export '{path}' in {Path.GetFileName(pcc.FilePath)}.");
        if (className != null && export.ClassName != className)
            throw new ArgumentException($"'{path}' is a {export.ClassName}, not a {className}.");
        return export;
    }

    private static int Write(string outPath, MergedSkeleton skeleton, List<(string Name, SkeletalMesh Mesh)> parts,
        NodeBuilder armature, NodeBuilder[] joints, TextureCache textures)
    {
        var scene = new SceneBuilder();
        scene.AddNode(armature);

        foreach (var (name, mesh) in parts)
            scene.AddSkinnedMesh(BuildMesh(name, mesh, skeleton, textures), Matrix4x4.Identity, joints);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        SharpGLTF.Schema2.ModelRoot model = scene.ToGltf2(Program.GltfSettings);
        string dds = DdsTextures.Convert(model);
        OutputFiles.Write(outPath, temporary => model.SaveGLB(temporary, Program.GlbSettings));
        Console.WriteLine($"Wrote {outPath} ({skeleton.Bones.Count} bones; {dds}).");
        return 0;
    }

    internal static IMeshBuilder<MaterialBuilder> BuildMesh(string name, SkeletalMesh mesh, MergedSkeleton skeleton, TextureCache textures)
    {
        StaticLODModel lod = mesh.LODModels[0];
        GPUSkinVertex[] verts = lod.VertexBufferGPUSkin.VertexData;
        uint[] indices = ReadIndices(lod);

        // Mesh bone index -> merged skeleton joint index.
        int[] jointOf = mesh.RefSkeleton.Select(b => skeleton.IndexOf(b.Name.Instanced)).ToArray();

        // Vertex index -> owning chunk; a chunk's BoneMap turns its local influence indices
        // into mesh bone indices.
        var chunkOf = new SkelMeshChunk[verts.Length];
        foreach (SkelMeshChunk chunk in lod.Chunks)
        {
            int count = chunk.NumRigidVertices + chunk.NumSoftVertices;
            for (int v = 0; v < count; v++)
                chunkOf[chunk.BaseVertexIndex + v] = chunk;
        }

        var positions = new Vector3[verts.Length];
        var normals = new Vector3[verts.Length];
        var tangents = new Vector4[verts.Length];
        var uvs = new Vector2[verts.Length];
        for (int v = 0; v < verts.Length; v++)
        {
            positions[v] = UnrealSpace.Position(verts[v].Position);
            normals[v] = SafeNormalize(UnrealSpace.Direction(Unpack(verts[v].TangentZ)));
            uvs[v] = new Vector2(verts[v].UV.X, verts[v].UV.Y);
            // TangentZ.W holds the binormal's sign (0 or 255).
            Vector3 t = UnrealSpace.Direction(Unpack(verts[v].TangentX));
            t = SafeNormalize(t - normals[v] * Vector3.Dot(normals[v], t));
            tangents[v] = new Vector4(t, verts[v].TangentZ.W > 127 ? 1f : -1f);
        }
        float handedness = TangentHandedness(name, indices, positions, uvs, normals, tangents);
        for (int v = 0; v < verts.Length; v++)
            tangents[v].W *= handedness;

        // Converting space mirrors the mesh, which may reverse which side of each triangle
        // faces out. Pick the winding whose face normals agree with the stored vertex normals.
        bool flip = ShouldFlipWinding(indices, positions, normals);
        Console.WriteLine($"{name}: {verts.Length} verts, {indices.Length / 3} tris, {lod.Sections.Length} sections, "
            + $"{(lod.DataTypeSize == 4 ? "32" : "16")}-bit indices, winding {(flip ? "flipped" : "kept")}.");

        var vertexCache = new VERTEX[verts.Length];
        for (int v = 0; v < verts.Length; v++)
            vertexCache[v] = MakeVertex(verts[v], chunkOf[v], positions[v], normals[v], tangents[v], jointOf);

        CheckBindPose(name, vertexCache, skeleton);

        var builder = new MeshBuilder<VertexPositionNormalTangent, VertexTexture1, VertexJoints4>(name);
        foreach (SkelMeshSection section in lod.Sections)
        {
            int materialUIndex = section.MaterialIndex < mesh.Materials.Length ? mesh.Materials[section.MaterialIndex] : 0;
            if (textures.IsHidden(materialUIndex))
                continue;
            var primitive = builder.UsePrimitive(textures.MaterialFor(materialUIndex, $"{name}_mat{section.MaterialIndex}"));
            int triangles = SectionTriangles(name, section, indices.Length);
            for (int t = 0; t < triangles; t++)
            {
                long i = section.BaseIndex + t * 3L;
                VERTEX a = vertexCache[indices[i]];
                VERTEX b = vertexCache[indices[i + 1]];
                VERTEX c = vertexCache[indices[i + 2]];
                if (flip)
                    primitive.AddTriangle(a, c, b);
                else
                    primitive.AddTriangle(a, b, c);
            }
        }
        return builder;
    }

    // A section's triangle count. Some ME1 meshes (the hair HMF_HIR_Cte_MDL in the Wards' pawns)
    // read with junk in the count's high bits (331249 = 0x50DF1 for 3569 = 0xDF1 triangles), which
    // runs past the index buffer; their low 16 bits are the real count.
    private static int SectionTriangles(string name, SkelMeshSection section, int indexCount)
    {
        long available = (indexCount - (long)section.BaseIndex) / 3;
        if (section.NumTriangles <= available)
            return section.NumTriangles;
        int low = section.NumTriangles & 0xFFFF;
        int count = low <= available ? low : (int)Math.Max(available, 0);
        Console.WriteLine($"  {name}: section at index {section.BaseIndex} claims {section.NumTriangles} triangles, more than the buffer holds; using {count}.");
        return count;
    }

    // LEX keeps the index buffer as ushorts; with 4-byte indices each index is a (low, high) pair.
    private static uint[] ReadIndices(StaticLODModel lod)
    {
        ushort[] raw = lod.IndexBuffer;
        if (lod.DataTypeSize != 4)
            return raw.Select(i => (uint)i).ToArray();
        var result = new uint[raw.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = raw[i * 2] | ((uint)raw[i * 2 + 1] << 16);
        return result;
    }

    private static VERTEX MakeVertex(GPUSkinVertex v, SkelMeshChunk chunk, Vector3 position, Vector3 normal, Vector4 tangent, int[] jointOf)
    {
        var influences = new List<(int Joint, float Weight)>(4);
        for (int k = 0; k < 4; k++)
        {
            byte weight = v.InfluenceWeights[k];
            if (weight == 0 || chunk == null)
                continue;
            int meshBone = chunk.BoneMap[v.InfluenceBones[k]];
            influences.Add((jointOf[meshBone], weight / 255f));
        }
        float total = influences.Sum(x => x.Weight);
        (int, float)[] bindings = total > 0f
            ? influences.Select(x => (x.Joint, x.Weight / total)).ToArray()
            : [(0, 1f)];

        // UE3 UVs are top-left origin, which is also glTF's convention.
        return new VERTEX(
            new VertexPositionNormalTangent(position, normal, tangent),
            new VertexTexture1(new Vector2(v.UV.X, v.UV.Y)),
            new VertexJoints4(bindings));
    }

    // PackedNormal bytes map 0..255 onto -1..1.
    internal static Vector3 Unpack(PackedNormal n) =>
        new Vector3(n.X / 127.5f - 1f, n.Y / 127.5f - 1f, n.Z / 127.5f - 1f);

    internal static Vector3 SafeNormalize(Vector3 v) =>
        v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitY;

    internal static bool ShouldFlipWinding(uint[] indices, Vector3[] positions, Vector3[] normals)
    {
        double agreement = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = positions[indices[i]], b = positions[indices[i + 1]], c = positions[indices[i + 2]];
            Vector3 stored = normals[indices[i]] + normals[indices[i + 1]] + normals[indices[i + 2]];
            agreement += Vector3.Dot(Vector3.Cross(b - a, c - a), stored);
        }
        // glTF front faces are counter-clockwise, so a CCW face normal should match the stored one.
        return agreement < 0;
    }

    // The sign glTF tangents need. glTF's bitangent is cross(normal, tangent) * w and points
    // up the texture (toward -V), as the green channel does once MaterialBaker flips it from
    // Unreal's DirectX convention. Converting space mirrors the mesh, so the mesh's own signs
    // may all need flipping; the UVs decide. Also logs which way Unreal's binormals pointed
    // (+V means DirectX-style normal maps, which is what the green flip assumes).
    private static float TangentHandedness(string name, uint[] indices, Vector3[] positions, Vector2[] uvs,
        Vector3[] normals, Vector4[] tangents)
    {
        double agreement = 0;
        int agree = 0, total = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            Vector3 e1 = positions[b] - positions[a], e2 = positions[c] - positions[a];
            Vector2 d1 = uvs[b] - uvs[a], d2 = uvs[c] - uvs[a];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f)
                continue;
            Vector3 dPdv = (e2 * d1.X - e1 * d2.X) / det;
            Vector3 bitangent = Vector3.Cross(normals[a], new Vector3(tangents[a].X, tangents[a].Y, tangents[a].Z)) * tangents[a].W;
            float dot = Vector3.Dot(bitangent, -dPdv);
            agreement += dot;
            total++;
            if (dot > 0)
                agree++;
        }
        float sign = agreement >= 0 ? 1f : -1f;
        if (total > 0)
            Console.WriteLine($"  tangents: signs {(sign > 0 ? "kept" : "flipped")}, {100.0 * (sign > 0 ? agree : total - agree) / total:F0}% of faces agree; "
                + $"Unreal binormals point {(sign > 0 ? "+" : "-")}V.");
        return sign;
    }

    // Sanity check on the bone convention: in the bind pose, vertices driven almost entirely by
    // one bone should sit near that bone. A few centimetres is healthy; a wrong rotation
    // convention puts limbs tens of centimetres off.
    private static void CheckBindPose(string name, VERTEX[] verts, MergedSkeleton skeleton)
    {
        var sums = new Dictionary<int, (Vector3 Sum, int Count)>();
        foreach (VERTEX v in verts)
        {
            var (joint, weight) = v.Skinning.GetBinding(0);
            if (weight < 0.9f)
                continue;
            sums.TryGetValue(joint, out var s);
            sums[joint] = (s.Sum + v.Geometry.Position, s.Count + 1);
        }
        var gaps = sums.Where(kv => kv.Value.Count >= 10)
            .Select(kv => (Bone: skeleton.Bones[kv.Key].Name,
                           Gap: Vector3.Distance(kv.Value.Sum / kv.Value.Count, skeleton.BindWorldPosition(kv.Key))))
            .OrderByDescending(x => x.Gap)
            .ToList();
        if (gaps.Count > 0)
            Console.WriteLine($"  bind check: bone-to-skin gap avg {gaps.Average(g => g.Gap) * 100:F1} cm over {gaps.Count} bones; "
                + $"worst {gaps[0].Bone} {gaps[0].Gap * 100:F1} cm.");
    }
}
