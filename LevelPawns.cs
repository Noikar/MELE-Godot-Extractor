using System.IO.Enumeration;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using BioMorphFace = LegendaryExplorerCore.Unreal.Classes.BioMorphFace;

namespace LexExport;

// A level's ambient NPCs (BioPawns whose Tag matches the --npcs patterns, e.g. *_amb_* and
// Keeper): what they look like, where they stand, what they loop, and the cylinder that blocks
// players.
//
//   Pawns/<package>_<actor>  one empty node per pawn at its mesh's origin (its feet), whose
//                        extras ("pawn") name its model, the clip it loops ("clip"), the
//                        one-shot clips played over it now and then ("twitches"), and its
//                        collision cylinder (radius, half_height, center in the node's frame);
//                        "settle" when it stands at an action station and the game should drop
//                        its feet onto the floor below
//
// A look is the merged skeleton, the body, the head (the morph face applied to it, as ME1 does
// at load), and the hair, with the pawn's own material instances (already tinted for it:
// clothing colors, skin tone) baked from their compiled shaders at PawnTextureSize. Pawns whose
// meshes, material parameters, and overrides match share one, named by a hash of all that
// (ReadLook). Looks are baked into files of their own, looks/<hash>.glb (ExportLook, run by the
// looks manifest the level writes beside itself, <level>.looks.json: each look's package and
// actor), so a look several levels share is baked once, and the level only builds what it
// needs for its clips. In the level each look is a scene root, PawnAsset_<n> ("pawn_asset",
// "species", "look": the hash), holding the look's skeleton and a placeholder mesh (so the
// importer builds a Skeleton3D) but no model. Each clip is baked once, compactly
// (AnimationBaker's compact: rotations, plus translations only where the anim set uses them),
// onto the first of those roots that plays it, under the AnimSequence's name; the game
// retargets it to every look by bone name (see LevelPawns.gd), as ME1 shares one set of clips
// across the species on the human skeleton.
public partial class LevelBuilder
{
    // NPC textures are baked at 512 px, smaller than player characters':
    // they're seen from further away, and every look adds to first-run extraction.
    private const int PawnTextureSize = 512;

    // Part of every look's hash: bump it when an exporter change alters looks, so players'
    // games bake them again.
    private const int LookVersion = 2;

    private readonly List<string> _pawnPatterns = new();
    private NodeBuilder _pawnsNode;
    // Pawn looks by appearance key -> asset name.
    private readonly Dictionary<string, PawnAsset> _pawnAssets = new();
    // The looks manifest's entries for the level's looks, in order: id (the hash), label,
    // package, actor (its full path and index).
    private readonly JsonArray _looks = new();
    private int _pawns, _pawnsAtStations, _pawnsRevealed, _pawnsUnposed;

    private sealed class PawnAsset
    {
        public string Name;
        public MergedSkeleton Skeleton;
        public NodeBuilder[] Joints;
        public JsonObject Extras;
    }

    // Comma-separated Tag patterns (wildcards allowed) of the pawns to export.
    public void AddPawnPatterns(string patterns)
    {
        _pawnPatterns.AddRange(patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _pawnsNode ??= _root.CreateNode("Pawns");
    }

    private bool WantsPawn(string tag) =>
        tag != null && _pawnPatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, tag, ignoreCase: true));

    // Returns false when the pawn isn't one of ours, so AddActor counts it as skipped.
    private bool TryAddPawn(IMEPackage pcc, ExportEntry actor)
    {
        string tag = InheritedProp<NameProperty>(actor, "Tag")?.Value.Instanced;
        if (_pawnsNode == null || !WantsPawn(tag))
            return false;
        ExportEntry type = ActorType(pcc, actor);
        PawnPlan plan = PlanFor(pcc, actor, type);
        bool hidden = InheritedProp<BoolProperty>(actor, "bHidden")?.Value == true;
        if (!(plan.Shown ?? !hidden))
        {
            _skipped["BioPawn (hidden)"] = _skipped.GetValueOrDefault("BioPawn (hidden)") + 1;
            return true;
        }
        if (hidden)
            _pawnsRevealed++;

        if (!pcc.TryGetUExport(InheritedProp<ObjectProperty>(actor, "Mesh")?.Value ?? 0, out ExportEntry body))
        {
            _skipped["BioPawn (no mesh)"] = _skipped.GetValueOrDefault("BioPawn (no mesh)") + 1;
            return true;
        }
        PropertyCollection actorProps = actor.GetProperties();
        PawnAsset asset = PawnModel(pcc, actor, body, type);
        if (asset == null)
            return true;

        string packageName = Path.GetFileNameWithoutExtension(pcc.FilePath);
        Matrix4x4 actorToWorld = UnrealTransform.ActorToWorld(actorProps);
        Matrix4x4 meshToActor = UnrealTransform.ComponentToActor(InheritedProps(body));
        Matrix4x4.Invert(meshToActor, out Matrix4x4 actorToMesh);
        // A pawn taking up an action station stands at the station's spot, facing its way (ME1
        // walks or teleports it there at load, and its physics settles it onto the floor). The
        // stations aren't at one height (standing ones are at the pawn's middle, sitting ones
        // at its feet), so its feet go at the station and the game drops them onto the floor
        // below ("settle").
        Matrix4x4 meshToWorld = meshToActor * actorToWorld;
        bool settle = plan.Station != null;
        if (settle)
        {
            Matrix4x4 meshTurn = meshToActor with { M41 = 0, M42 = 0, M43 = 0 };
            meshToWorld = meshTurn * UnrealTransform.WithoutScale(UnrealTransform.ActorToWorld(plan.Station.GetProperties()));
            _pawnsAtStations++;
        }

        var pawn = new JsonObject
        {
            ["asset"] = asset.Name,
            ["tag"] = tag,
            ["package"] = packageName,
        };
        if (settle)
            pawn["settle"] = true;
        // The cylinder that blocks the player, centered on the actor's origin (UE3 pawns stand
        // at their cylinder's middle; the mesh hangs below it).
        if (pcc.TryGetUExport(InheritedProp<ObjectProperty>(actor, "CylinderComponent")?.Value ?? 0, out ExportEntry cylinder))
        {
            float radius = InheritedProp<FloatProperty>(cylinder, "CollisionRadius")?.Value ?? 34f;
            float height = InheritedProp<FloatProperty>(cylinder, "CollisionHeight")?.Value ?? 78f;
            Vector3 center = UnrealSpace.Position(Vector3.Transform(Vector3.Zero, actorToMesh));
            pawn["collision"] = new JsonObject
            {
                ["radius"] = radius * UnrealSpace.UnitsToMeters,
                ["half_height"] = height * UnrealSpace.UnitsToMeters,
                ["center"] = new JsonArray(center.X, center.Y, center.Z),
            };
        }

        // Its looping pose, and the one-shot twitches played over it now and then.
        string species = asset.Extras["species"]?.GetValue<string>();
        if (plan.Pose.Set != null && BakeClip(asset, plan.Pose.Set, plan.Pose.Anim, species) is string pose)
            pawn["clip"] = pose;
        else
            _pawnsUnposed++;
        var twitches = new JsonArray();
        foreach (var (set, anim) in plan.Twitches)
        {
            if (BakeClip(asset, set, anim, species) is string twitch)
                twitches.Add(twitch);
        }
        if (twitches.Count > 0)
            pawn["twitches"] = twitches;

        var node = new NodeBuilder($"{packageName}_{actor.ObjectName.Instanced}", new JsonObject { ["pawn"] = pawn });
        _pawnsNode.AddNode(node);
        node.LocalMatrix = UnrealTransform.ToGltf(meshToWorld);
        _pawns++;
        return true;
    }

    // The pawn's actor type (its behavior's m_oActorType), which holds its morph face, appearance,
    // and material overrides; null when it has none.
    private static ExportEntry ActorType(IMEPackage pcc, ExportEntry actor)
    {
        ExportEntry type = null;
        if (pcc.TryGetUExport(InheritedProp<ObjectProperty>(actor, "m_oBehavior")?.Value ?? 0, out ExportEntry behavior))
            pcc.TryGetUExport(InheritedProp<ObjectProperty>(behavior, "m_oActorType")?.Value ?? 0, out type);
        return type;
    }

    // An export's properties with the ones it leaves to its archetypes filled in (a prefab
    // member's mesh offset lives on its archetype).
    private static PropertyCollection InheritedProps(ExportEntry export)
    {
        PropertyCollection props = export.GetProperties();
        for (ExportEntry e = export.Archetype as ExportEntry; e != null; e = e.Archetype as ExportEntry)
        {
            foreach (Property inherited in e.GetProperties())
            {
                if (!props.Any(p => p.Name == inherited.Name))
                    props.Add(inherited);
            }
        }
        return props;
    }

    // A look's parts, read the same way by the level (for its hash and skeleton) and by
    // ExportLook (to bake it).
    private sealed class Look
    {
        public List<(string Name, SkeletalMesh Mesh)> Parts = new();
        public ExportEntry MorphExport, FaceOverride, TypeOverride;
        public BioMorphFace Morph;
        public bool Morphed;
        public string Species;
        // Everything that decides what it looks like, hashed into its name.
        public string Key;

        public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key)))[..16].ToLowerInvariant();

        public string Describe(ExportEntry actor) => $"{actor.ObjectName.Instanced}, {string.Join(" + ", Parts.Select(p => p.Name))}"
            + (Morphed ? $", face {MorphExport.ObjectName.Instanced}" : Morph != null ? ", face not applied (no base head)" : "");
    }

    // The pawn's look: its body component, its head (morphed by its actor type's face), its
    // other mesh components, and its hair, each with the component's own material instances.
    // Null when none of its components has a mesh.
    private static Look ReadLook(IMEPackage pcc, ExportEntry actor, ExportEntry body, ExportEntry type)
    {
        var components = new List<ExportEntry> { body };
        if (pcc.TryGetUExport(InheritedProp<ObjectProperty>(actor, "m_oHeadMesh")?.Value ?? 0, out ExportEntry headComponent))
            components.Add(headComponent);
        foreach (ObjectProperty extra in InheritedProp<ArrayProperty<ObjectProperty>>(actor, "m_aoMeshes") ?? Enumerable.Empty<ObjectProperty>())
        {
            if (pcc.TryGetUExport(extra.Value, out ExportEntry component) && !components.Any(c => c.UIndex == component.UIndex))
                components.Add(component);
        }

        // The actor type's morph face reshapes the base head the component draws, and brings
        // the hair; its material overrides (and the type's) are what tinted the pawn's instances.
        var look = new Look();
        if (type != null && pcc.TryGetUExport(type.GetProperties().GetProp<ObjectProperty>("m_oMorphFace")?.Value ?? 0, out ExportEntry morphExport))
        {
            look.MorphExport = morphExport;
            look.Morph = new BioMorphFace(morphExport);
            if (pcc.TryGetUExport(morphExport.GetProperties().GetProp<ObjectProperty>("m_oMaterialOverrides")?.Value ?? 0, out ExportEntry faceOverride))
                look.FaceOverride = faceOverride;
        }
        if (type != null && pcc.TryGetUExport(type.GetProperties().GetProp<ObjectProperty>("m_oMaterialOverrides")?.Value ?? 0, out ExportEntry typeOverride))
            look.TypeOverride = typeOverride;

        var key = new StringBuilder($"look {LookVersion} at {PawnTextureSize}|");
        key.Append(look.MorphExport?.InstancedFullPath ?? "-");
        foreach (ExportEntry component in components)
        {
            if (!pcc.TryGetUExport(InheritedProp<ObjectProperty>(component, "SkeletalMesh")?.Value ?? 0, out ExportEntry meshExport))
                continue;
            SkeletalMesh mesh;
            // The head component may hold only a blank stand-in (HMF_Blank_World_LOD0) that the
            // morph face's head replaces at load.
            if (look.Morph != null && !look.Morphed && (component == headComponent || look.Morph.m_oBaseHead?.InstancedFullPath == meshExport.InstancedFullPath))
            {
                mesh = MorphFace.Apply(look.Morph);
                look.Morphed = true;
            }
            else
                mesh = ObjectBinary.From<SkeletalMesh>(meshExport);
            // The component's own instances replace the mesh's materials slot by slot.
            var overrides = InheritedProp<ArrayProperty<ObjectProperty>>(component, "Materials");
            for (int s = 0; s < (overrides?.Count ?? 0) && s < mesh.Materials.Length; s++)
            {
                if (overrides[s].Value != 0)
                    mesh.Materials[s] = overrides[s].Value;
            }
            look.Parts.Add((meshExport.ObjectName.Instanced, mesh));
            key.Append('|').Append(meshExport.InstancedFullPath);
            foreach (int material in mesh.Materials)
                key.Append(';').Append(MaterialSignature(pcc, material));
        }
        if (look.Parts.Count == 0)
            return null;
        // The morph face's hair, unless the pawn wears hair of its own (m_oHairMesh).
        if (look.Morph?.HairMesh != null && InheritedProp<ObjectProperty>(actor, "m_oHairMesh") is not ObjectProperty { Value: not 0 })
        {
            look.Parts.Add((look.Morph.m_oHairMesh.ObjectName.Instanced, look.Morph.HairMesh));
            key.Append("|hair:").Append(look.Morph.m_oHairMesh.InstancedFullPath);
        }
        // Every part with face bones (a pawn's own hair too) takes the morph's, so the merged
        // skeleton holds them whichever part comes first.
        if (look.Morph != null)
        {
            foreach (var (_, mesh) in look.Parts)
                MorphFace.ApplyBoneOffsets(mesh, look.Morph.BoneOffsets);
        }
        // The overrides tint the bake too (the face's rides on its path).
        key.Append("|type:").Append(look.TypeOverride != null ? OverrideSignature(pcc, look.TypeOverride) : "-");
        look.Species = SpeciesOf(look.Parts[0].Name);
        look.Key = key.ToString();
        return look;
    }

    // A BioMaterialOverride's texture and color overrides, for a look's key.
    private static string OverrideSignature(IMEPackage pcc, ExportEntry materialOverride)
    {
        var text = new StringBuilder();
        PropertyCollection props = materialOverride.GetProperties();
        foreach (string list in new[] { "m_aTextureOverrides", "m_aColorOverrides" })
        {
            foreach (StructProperty entry in props.GetProp<ArrayProperty<StructProperty>>(list) ?? Enumerable.Empty<StructProperty>())
                text.Append('{').Append(PropertyText(pcc, entry)).Append('}');
        }
        return text.ToString();
    }

    // The level's side of a pawn's look: once per distinct look, a PawnAsset_<n> scene root with
    // its skeleton, which its clips are baked onto, and an entry in the looks manifest.
    private PawnAsset PawnModel(IMEPackage pcc, ExportEntry actor, ExportEntry body, ExportEntry type)
    {
        Look look = ReadLook(pcc, actor, body, type);
        if (look == null)
            return null;
        string hash = look.Hash;
        if (_pawnAssets.TryGetValue(hash, out PawnAsset existing))
            return existing;

        string name = $"PawnAsset_{_pawnAssets.Count}";
        var skeleton = new MergedSkeleton();
        foreach (var (_, mesh) in look.Parts)
            skeleton.Add(mesh.RefSkeleton);
        NodeBuilder[] joints = skeleton.BuildNodes(out NodeBuilder armature);
        var extras = new JsonObject { ["pawn_asset"] = name, ["species"] = look.Species, ["look"] = hash };
        var assetNode = new NodeBuilder(name, extras);
        assetNode.AddNode(armature);
        _scene.AddNode(assetNode);
        _scene.AddSkinnedMesh(SkeletonPlaceholder(name), Matrix4x4.Identity, joints);

        _looks.Add(new JsonObject
        {
            ["id"] = hash,
            ["label"] = $"{look.Species} ({actor.ObjectName.Instanced})",
            ["package"] = Program.GameRelative(pcc.FilePath),
            ["actor"] = actor.InstancedFullPath,
            ["uindex"] = actor.UIndex,
        });
        var asset = new PawnAsset { Name = name, Skeleton = skeleton, Joints = joints, Extras = extras };
        _pawnAssets[hash] = asset;
        Console.WriteLine($"  pawn look {name} = {hash} ({look.Describe(actor)}): {skeleton.Bones.Count} bones.");
        return asset;
    }

    // One 1 mm triangle skinned to the root bone: a skin needs a mesh, and without one the
    // importer builds the joints as plain nodes instead of a Skeleton3D. (SharpGLTF drops
    // degenerate triangles, so it can't be a point.)
    private static IMeshBuilder<MaterialBuilder> SkeletonPlaceholder(string name)
    {
        var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexJoints4>($"{name}_Skeleton");
        VertexBuilder<VertexPosition, VertexEmpty, VertexJoints4> Corner(float x, float z) =>
            new(new VertexPosition(x, 0f, z), new VertexEmpty(), new VertexJoints4((0, 1f)));
        mesh.UsePrimitive(new MaterialBuilder($"{name}_Skeleton").WithUnlitShader())
            .AddTriangle(Corner(0f, 0f), Corner(0.001f, 0f), Corner(0f, 0.001f));
        return mesh;
    }

    // Writes the level's looks manifest beside its .glb: <level>.looks.json.
    private void WriteLooks(string outPath)
    {
        if (_looks.Count == 0)
            return;
        var manifest = new JsonObject
        {
            ["version"] = LookVersion,
            ["level"] = Path.GetFileNameWithoutExtension(outPath),
            ["looks"] = _looks,
        };
        string path = Path.ChangeExtension(outPath, ".looks.json");
        OutputFiles.Update(path, _ => manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Looks: {_looks.Count} in {Path.GetFileName(path)}.");
    }

    // Bakes one look (a looks manifest entry) into its own .glb: a "PawnAsset" scene root
    // ("pawn_asset", "species", "look") with the skeleton and the skinned meshes, the same layout
    // as the level's PawnAsset_<n> roots. Throws when the look isn't the one the level named (the
    // exporter changed between the two).
    public static void ExportLook(string package, string actorPath, int uIndex, string id, string outPath)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        if (!pcc.TryGetUExport(uIndex, out ExportEntry actor) || actor.InstancedFullPath != actorPath)
            actor = pcc.FindExport(actorPath) ?? throw new InvalidDataException($"{actorPath} isn't in {package}");
        if (!pcc.TryGetUExport(InheritedProp<ObjectProperty>(actor, "Mesh")?.Value ?? 0, out ExportEntry body))
            throw new InvalidDataException($"{actorPath} has no mesh");
        Look look = ReadLook(pcc, actor, body, ActorType(pcc, actor)) ?? throw new InvalidDataException($"{actorPath} has no mesh parts");
        if (look.Hash != id)
            throw new InvalidDataException($"{actorPath} is look {look.Hash} now, not {id}: export the level again");

        const string name = "PawnAsset";
        var skeleton = new MergedSkeleton();
        foreach (var (_, mesh) in look.Parts)
            skeleton.Add(mesh.RefSkeleton);
        NodeBuilder[] joints = skeleton.BuildNodes(out NodeBuilder armature);
        var assetNode = new NodeBuilder(name, new JsonObject { ["pawn_asset"] = name, ["species"] = look.Species, ["look"] = id });
        assetNode.AddNode(armature);
        var scene = new SceneBuilder();
        scene.AddNode(assetNode);

        var textures = new TextureCache(pcc, []) { BakeShaders = true, ResolveImports = true, MaxTextureSize = PawnTextureSize };
        if (look.FaceOverride != null)
            textures.AddOverrides(look.FaceOverride);
        if (look.TypeOverride != null)
            textures.AddOverrides(look.TypeOverride, keepSkinTone: true);
        foreach (var (partName, mesh) in look.Parts)
            scene.AddSkinnedMesh(MeshCommand.BuildMesh($"{name}_{partName}", mesh, skeleton, textures), Matrix4x4.Identity, joints);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        SharpGLTF.Schema2.ModelRoot model = scene.ToGltf2(Program.GltfSettings);
        string dds = DdsTextures.Convert(model);
        OutputFiles.Write(outPath, temporary => model.SaveGLB(temporary, Program.GlbSettings));
        Console.WriteLine($"Wrote {outPath} ({look.Describe(actor)}; {skeleton.Bones.Count} bones; {dds}).");
        ShaderAnalysis.Forget(pcc);
    }

    // What a material draws as: its parent chain and every parameter value, so two pawns'
    // separately cooked instances with the same tints count as one look.
    private static string MaterialSignature(IMEPackage pcc, int material)
    {
        IEntry entry = material != 0 ? pcc.GetEntry(material) : null;
        if (entry is not ExportEntry export || export.ClassName is "Material")
            return entry?.InstancedFullPath ?? "-";
        var text = new StringBuilder();
        PropertyCollection props = export.GetProperties();
        text.Append(MaterialSignature(pcc, props.GetProp<ObjectProperty>("Parent")?.Value ?? 0));
        foreach (string list in new[] { "VectorParameterValues", "ScalarParameterValues", "TextureParameterValues" })
        {
            foreach (StructProperty parameter in props.GetProp<ArrayProperty<StructProperty>>(list) ?? Enumerable.Empty<StructProperty>())
            {
                text.Append('{');
                foreach (Property p in parameter.Properties)
                {
                    if (p.Name.Instanced == "ExpressionGUID")
                        continue;
                    text.Append(p.Name.Instanced).Append('=').Append(PropertyText(pcc, p)).Append(',');
                }
                text.Append('}');
            }
        }
        return text.ToString();
    }

    private static string PropertyText(IMEPackage pcc, Property p) => p switch
    {
        ObjectProperty o => pcc.GetEntry(o.Value)?.InstancedFullPath ?? "-",
        NameProperty n => n.Value.Instanced,
        FloatProperty f => f.Value.ToString("R"),
        StructProperty s => string.Join(",", s.Properties.Select(c => PropertyText(pcc, c))),
        _ => p.ToString(),
    };

    // The species a body mesh belongs to, from ME1's mesh prefixes (HMM_ARM_CTHb_MDL: human male).
    private static string SpeciesOf(string meshName)
    {
        string upper = meshName.ToUpperInvariant();
        // Ambient creatures carry the species second or third: AMB_KEE_NKDa_MDL, NCA_FAC_VOL_NKDa_MDL.
        if (upper.Contains("_KEE_"))
            return "keeper";
        if (upper.Contains("_VOL_"))
            return "volus";
        string prefix = upper.Split('_')[0];
        return prefix switch
        {
            "HMM" => "human_male",
            "HMF" => "human_female",
            "ASA" => "asari",
            "TUR" => "turian",
            "SAL" => "salarian",
            "KRO" => "krogan",
            "QRN" => "quarian",
            "KEE" => "keeper",
            _ => prefix.ToLowerInvariant(),
        };
    }

    // ---------------------------------------------------------------
    // ANIMATION (how ME1 drives ambient pawns, resolved statically from the level's Kismet)
    // ---------------------------------------------------------------
    //
    // Every ambient idle is a BioSeqAct_ActionStation whose [Pawn] link holds the pawn: its
    // m_EntranceTransition's nmPoseSet/nmPoseAnim is the looping pose, and its [PathNode] link,
    // when set, is the BioWp_ActionStation it takes up (a chair, a desk, a railing), placed in
    // world space; ME1 walks or teleports the pawn there at load. Twitches are SeqAct_Interp
    // matinees whose BioEvtSysTrackGesture (sActorTag names the matinee's link holding the pawn)
    // plays one gesture; the station fires them now and then. A pawn with no station stands at
    // its own place in its appearance's default pose (Bio_Appr_Character m_pCookedDefGestPoseSet
    // and m_nmDefaultGesturesPoseAnim). Clip <Set>/<Anim> is the AnimSequence <Set>_<Anim>, cooked
    // into the level or else into BIOC_Materials (ME1's always-loaded shared package).
    //
    // Links resolve through SeqVar_Object (ObjValue), SeqVar_External (the enclosing sequence's,
    // or its SequenceReference's, link of that label), and SeqVar_Named/ScopedNamed (variables of
    // that VarName). bHidden pawns that Kismet shows (BioSeqAct_ModifyPropertyPawn [Active] = true)
    // straight from a BioSeqAct_PMCheckState's False output are shown, and shown ones it hides that
    // way are hidden: a new game's plot flags are false. (The Chora's Den bartenders are such a
    // pair.)

    private const string SharedAnimationPackage = "ME1/BIOC_Materials.pcc";

    private sealed class PawnPlan
    {
        public (string Set, string Anim) Pose;
        public ExportEntry Station;               // its BioWp_ActionStation, when it takes one up
        public List<(string Set, string Anim)> Twitches = new();
        public bool? Shown;                       // Kismet's say at load, when it has one
    }

    private sealed class Kismet
    {
        public string Package;
        public List<(ExportEntry Op, string Link, HashSet<int> Objects)> Bindings = new();
        public Dictionary<int, List<(ExportEntry From, string Link)>> Incoming = new();
    }

    private Kismet _kismet;
    private IMEPackage _sharedAnimations;
    private readonly Dictionary<string, ExportEntry> _levelAnimations = new(StringComparer.OrdinalIgnoreCase);

    private Kismet KismetOf(IMEPackage pcc)
    {
        if (_kismet?.Package == pcc.FilePath)
            return _kismet;
        _kismet = new Kismet { Package = pcc.FilePath };
        _levelAnimations.Clear();
        foreach (ExportEntry e in pcc.Exports.Where(e => e.ClassName == "AnimSequence"))
            _levelAnimations.TryAdd(e.ObjectName.Instanced, e);

        var ops = pcc.Exports.Where(e => (e.ClassName.StartsWith("Seq") || e.ClassName.StartsWith("BioSeq"))
            && !e.ClassName.StartsWith("SeqVar")).ToList();
        var named = new Dictionary<string, List<ExportEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (ExportEntry v in pcc.Exports.Where(e => e.ClassName.StartsWith("SeqVar") && e.ClassName is not ("SeqVar_Named" or "SeqVar_ScopedNamed")))
        {
            string name = v.GetProperties().GetProp<NameProperty>("VarName")?.Value.Instanced;
            if (!string.IsNullOrEmpty(name) && name != "None")
            {
                if (!named.TryGetValue(name, out var list))
                    named[name] = list = new();
                list.Add(v);
            }
        }

        HashSet<int> Resolve(ExportEntry v, HashSet<int> seen)
        {
            var result = new HashSet<int>();
            if (!seen.Add(v.UIndex))
                return result;
            PropertyCollection p = v.GetProperties();
            switch (v.ClassName)
            {
                case "SeqVar_Object" or "SeqVar_ObjectVolume" or "SeqVar_Union":
                    if (p.GetProp<ObjectProperty>("ObjValue")?.Value is int value and not 0)
                        result.Add(value);
                    break;
                case "SeqVar_ObjectList":
                    foreach (ObjectProperty o in p.GetProp<ArrayProperty<ObjectProperty>>("ObjList") ?? Enumerable.Empty<ObjectProperty>())
                        result.Add(o.Value);
                    break;
                case "SeqVar_External":
                {
                    string label = p.GetProp<StrProperty>("VariableLabel")?.Value;
                    var sequence = v.Parent as ExportEntry;
                    var linker = sequence?.Parent is ExportEntry reference && reference.ClassName == "SequenceReference" ? reference : sequence;
                    foreach (ExportEntry owner in new[] { linker, sequence }.Where(o => o != null).Distinct())
                    {
                        foreach (var (link, vars) in VariableLinks(pcc, owner))
                        {
                            if (string.Equals(link, label, StringComparison.OrdinalIgnoreCase))
                                foreach (ExportEntry x in vars)
                                    result.UnionWith(Resolve(x, seen));
                        }
                    }
                    break;
                }
                case "SeqVar_Named" or "SeqVar_ScopedNamed":
                {
                    string find = p.GetProp<NameProperty>("FindVarName")?.Value.Instanced;
                    if (find != null && named.TryGetValue(find, out var list))
                        foreach (ExportEntry x in list)
                            result.UnionWith(Resolve(x, seen));
                    break;
                }
                default:
                    result.Add(v.UIndex);    // InterpData, SeqVar_Bool, ...: the variable itself
                    break;
            }
            return result;
        }

        foreach (ExportEntry op in ops)
        {
            foreach (var (link, vars) in VariableLinks(pcc, op))
            {
                var objects = new HashSet<int>();
                foreach (ExportEntry v in vars)
                    objects.UnionWith(Resolve(v, new HashSet<int>()));
                _kismet.Bindings.Add((op, link, objects));
            }
            foreach (StructProperty output in op.GetProperties().GetProp<ArrayProperty<StructProperty>>("OutputLinks") ?? Enumerable.Empty<StructProperty>())
            {
                foreach (StructProperty target in output.GetProp<ArrayProperty<StructProperty>>("Links") ?? Enumerable.Empty<StructProperty>())
                {
                    int to = target.GetProp<ObjectProperty>("LinkedOp")?.Value ?? 0;
                    if (to <= 0)
                        continue;
                    if (!_kismet.Incoming.TryGetValue(to, out var from))
                        _kismet.Incoming[to] = from = new();
                    from.Add((op, output.GetProp<StrProperty>("LinkDesc")?.Value));
                }
            }
        }
        return _kismet;
    }

    // A sequence op's variable links: (LinkDesc, the variables linked there).
    private static List<(string Link, List<ExportEntry> Vars)> VariableLinks(IMEPackage pcc, ExportEntry op)
    {
        var result = new List<(string, List<ExportEntry>)>();
        foreach (StructProperty link in op.GetProperties().GetProp<ArrayProperty<StructProperty>>("VariableLinks") ?? Enumerable.Empty<StructProperty>())
        {
            var vars = new List<ExportEntry>();
            foreach (ObjectProperty v in link.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (pcc.TryGetUExport(v.Value, out ExportEntry variable))
                    vars.Add(variable);
            }
            result.Add((link.GetProp<StrProperty>("LinkDesc")?.Value, vars));
        }
        return result;
    }

    private PawnPlan PlanFor(IMEPackage pcc, ExportEntry actor, ExportEntry type)
    {
        Kismet kismet = KismetOf(pcc);
        var plan = new PawnPlan();
        var mine = kismet.Bindings.Where(b => b.Objects.Contains(actor.UIndex)).ToList();
        static string Name(StructProperty s, string name) => s?.GetProp<NameProperty>(name)?.Value.Instanced is string v && v != "None" ? v : null;

        // Its action station: the first one that takes it (Flux's patrons have alternatives).
        foreach (var (op, link, _) in mine.Where(b => b.Op.ClassName == "BioSeqAct_ActionStation" && b.Link == "Pawn"))
        {
            StructProperty entrance = op.GetProperties().GetProp<StructProperty>("m_EntranceTransition");
            if (Name(entrance, "nmPoseSet") is not string set || Name(entrance, "nmPoseAnim") is not string anim)
                continue;
            plan.Pose = (set, anim);
            var station = kismet.Bindings.FirstOrDefault(b => b.Op == op && b.Link == "PathNode").Objects;
            if (station?.FirstOrDefault() is int index && pcc.TryGetUExport(index, out ExportEntry waypoint) && waypoint.ClassName == "BioWp_ActionStation")
                plan.Station = waypoint;
            break;
        }

        // Its twitches: the gesture tracks of matinees that bind it.
        foreach (var (op, link, _) in mine.Where(b => b.Op.ClassName == "SeqAct_Interp"))
        {
            var data = kismet.Bindings.FirstOrDefault(b => b.Op == op && b.Link == "Data").Objects ?? new HashSet<int>();
            foreach (int dataIndex in data)
            {
                if (!pcc.TryGetUExport(dataIndex, out ExportEntry interpData) || interpData.ClassName != "InterpData")
                    continue;
                foreach (ObjectProperty groupRef in interpData.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("InterpGroups") ?? Enumerable.Empty<ObjectProperty>())
                {
                    if (!pcc.TryGetUExport(groupRef.Value, out ExportEntry group))
                        continue;
                    foreach (ObjectProperty trackRef in group.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("InterpTracks") ?? Enumerable.Empty<ObjectProperty>())
                    {
                        if (!pcc.TryGetUExport(trackRef.Value, out ExportEntry track) || track.ClassName != "BioEvtSysTrackGesture")
                            continue;
                        PropertyCollection trackProps = track.GetProperties();
                        if (trackProps.GetProp<StrProperty>("sActorTag")?.Value != link)
                            continue;
                        foreach (StructProperty gesture in trackProps.GetProp<ArrayProperty<StructProperty>>("m_aGestures") ?? Enumerable.Empty<StructProperty>())
                        {
                            if (Name(gesture, "nmGestureSet") is string set && Name(gesture, "nmGestureAnim") is string anim && !plan.Twitches.Contains((set, anim)))
                                plan.Twitches.Add((set, anim));
                        }
                    }
                }
            }
        }

        // No station: the appearance's default pose.
        if (plan.Pose.Set == null && type != null
            && pcc.TryGetUExport(type.GetProperties().GetProp<ObjectProperty>("m_oAppearance")?.Value ?? 0, out ExportEntry appearance))
        {
            PropertyCollection appearanceProps = appearance.GetProperties();
            string anim = appearanceProps.GetProp<NameProperty>("m_nmDefaultGesturesPoseAnim")?.Value.Instanced;
            if (pcc.TryGetUExport(appearanceProps.GetProp<ObjectProperty>("m_pCookedDefGestPoseSet")?.Value ?? 0, out ExportEntry poseSet)
                && poseSet.GetProperties().GetProp<NameProperty>("m_nmOrigSetName")?.Value.Instanced is string set && anim != null)
                plan.Pose = (set, anim);
        }

        // Shown or hidden at load: ModifyPropertyPawn [Active] fired from a plot check's False output.
        foreach (var (op, _, _) in mine.Where(b => b.Op.ClassName == "BioSeqAct_ModifyPropertyPawn" && b.Link == "Target"))
        {
            bool fromFalse = kismet.Incoming.GetValueOrDefault(op.UIndex)?.Any(i => i.From.ClassName == "BioSeqAct_PMCheckState" && i.Link == "False") == true;
            var active = kismet.Bindings.FirstOrDefault(b => b.Op == op && b.Link == "Active").Objects;
            if (!fromFalse || active == null || active.Count != 1 || !pcc.TryGetUExport(active.First(), out ExportEntry flag) || flag.ClassName != "SeqVar_Bool")
                continue;
            plan.Shown = (flag.GetProperties().GetProp<IntProperty>("bValue")?.Value ?? 0) != 0;
        }
        return plan;
    }

    // The AnimSequence for ME1's <Set>/<Anim>: the level's own copy, else BIOC_Materials'. A
    // krogan takes the KRO_ version of a human set when there is one (its skeleton isn't the
    // human one others share).
    private (IMEPackage Package, ExportEntry Sequence) FindAnimation(string set, string anim, string species, bool sharedOnly = false)
    {
        var names = new List<string>();
        if (species == "krogan" && set.StartsWith("HMM_", StringComparison.OrdinalIgnoreCase))
            names.Add("KRO_" + set[4..] + "_" + anim);
        names.Add(set + "_" + anim);
        foreach (string name in names)
        {
            if (!sharedOnly && _levelAnimations.TryGetValue(name, out ExportEntry sequence))
                return (sequence.FileRef, sequence);
        }
        _sharedAnimations ??= MEPackageHandler.OpenMEPackage(Program.ResolvePackage(SharedAnimationPackage));
        foreach (string name in names)
        {
            ExportEntry shared = _sharedAnimations.Exports.FirstOrDefault(e => e.ClassName == "AnimSequence" && e.ObjectName.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (shared != null)
                return (_sharedAnimations, shared);
        }
        return (null, null);
    }

    // Bakes a clip onto a model once, under the AnimSequence's name (shared by every model that
    // plays it, so the game's per-model filter finds it). Null when ME1's clip isn't found, or
    // none of its tracks fit the model: some levels' copies of the head-look gestures
    // (DL_LookLeft, DL_HeadRight) map no bones, so the shared package's copy is tried next.
    private string BakeClip(PawnAsset asset, string set, string anim, string species)
    {
        var (package, sequence) = FindAnimation(set, anim, species);
        if (sequence == null)
        {
            _missingClips.Add($"{set}/{anim}");
            return null;
        }
        string clip = sequence.ObjectName.Instanced;
        // Baked once, onto the first model that plays it: the game retargets it by bone name.
        if (_bakedClips.Contains(clip))
            return clip;
        if (_unusableClips.Contains(clip))
            return null;
        int mapped = AnimationBaker.Bake(package, sequence.InstancedFullPath, clip, asset.Skeleton, asset.Joints, inPlace: true, compact: true);
        if (mapped == 0 && package != _sharedAnimations)
        {
            var (shared, sharedSequence) = FindAnimation(set, anim, species, sharedOnly: true);
            if (sharedSequence != null)
                mapped = AnimationBaker.Bake(shared, sharedSequence.InstancedFullPath, clip, asset.Skeleton, asset.Joints, inPlace: true, compact: true);
        }
        if (mapped == 0)
        {
            _unusableClips.Add(clip);
            return null;
        }
        _bakedClips.Add(clip);
        return clip;
    }

    private readonly HashSet<string> _bakedClips = new(StringComparer.OrdinalIgnoreCase);

    private readonly SortedSet<string> _unusableClips = new(StringComparer.OrdinalIgnoreCase);

    private readonly SortedSet<string> _missingClips = new(StringComparer.OrdinalIgnoreCase);
}
