using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Scenes;

namespace LexExport;

// Bakes AnimSequences onto the merged skeleton's glTF nodes as named animations.
//
// LEX turns an AnimSequence into PSA data: per frame, a local translation and rotation for
// every animated bone, in the same Unreal space as the RefSkeleton. Each key is converted the
// same way bones are, so a clip plays on the exported skeleton as it does in-game.
//
// Like the game, only rotations are taken from the clip, except for the bones its anim set
// lists in UseTranslationBoneNames (Root, props); every other bone keeps the mesh's own bone
// lengths. That's what lets one clip play on differently proportioned bodies and faces.
public static class AnimationBaker
{
    // bindBones: bones held at their bind pose for this clip instead of following it, e.g. the
    // neck and head when a borrowed human clip droops a salarian's longer neck. compact leaves
    // out the translation tracks of bones that keep their bind translation and writes a single
    // key for tracks that never change; a level's NPC clips use it (the game retargets them by
    // bone name, so bones without a track keep their own model's rest pose). additive adds a held
    // pose's difference from a base pose on top of the clip, as ME2's BioAnimNodeBlendAdditive does
    // (a krogan's pistol hold, KRO_CB_Pistol_CB_IdleOffset minus KRO_CB_Rifle_CB_IdlePose, over its
    // rifle locomotion): per bone, rotation = (pose * base^-1) * clip and position += pose - base.
    // Returns how many of the clip's tracks found a bone (0: nothing was baked).
    public static int Bake(IMEPackage pcc, string sequencePath, string clipName, MergedSkeleton skeleton,
        NodeBuilder[] joints, bool inPlace, IReadOnlyCollection<string> bindBones = null, bool compact = false,
        AdditivePose additive = null)
    {
        ExportEntry export = pcc.FindExport(sequencePath)
            ?? throw new ArgumentException($"No export '{sequencePath}' in {Path.GetFileName(pcc.FilePath)}.");
        if (export.ClassName != "AnimSequence")
            throw new ArgumentException($"'{sequencePath}' is a {export.ClassName}, not an AnimSequence.");

        var sequence = ObjectBinary.From<AnimSequence>(export);
        PSA psa = PSA.CreateFrom(sequence);
        PSA.PSAAnimInfo info = psa.Infos[0];
        int boneCount = psa.Bones.Count;
        int frames = info.NumRawFrames;
        float rate = info.AnimRate > 0 ? info.AnimRate : 30f;

        int[] jointOf = psa.Bones.Select(b => skeleton.Contains(b.Name) ? skeleton.IndexOf(b.Name) : -1).ToArray();
        int skipped = jointOf.Count(j => j < 0);

        // The root bone's horizontal travel is root motion. For a character the server moves,
        // keep the clip in place and let the controller supply the movement.
        int rootBone = Array.FindIndex(jointOf, j => j >= 0 && skeleton.Bones[j].Parent < 0);
        HashSet<string> translated = TranslatedBones(pcc, export);

        for (int b = 0; b < boneCount; b++)
        {
            int joint = jointOf[b];
            if (joint < 0)
                continue;
            NodeBuilder node = joints[joint];

            bool holdBind = bindBones != null && bindBones.Contains(psa.Bones[b].Name, StringComparer.OrdinalIgnoreCase);
            bool useTranslation = !holdBind && (translated == null || translated.Contains(psa.Bones[b].Name) || b == rootBone);
            MergedSkeleton.Bone bind = skeleton.Bones[joint];
            (Quaternion Rotation, Vector3 Offset) delta = (Quaternion.Identity, Vector3.Zero);
            bool added = !holdBind && additive != null && additive.Deltas.TryGetValue(psa.Bones[b].Name, out delta);

            Vector3 firstPos = FromPsa(psa.Keys[info.FirstRawFrame * boneCount + b].Position);
            var positions = new Vector3[frames];
            var rotations = new Quaternion[frames];
            for (int f = 0; f < frames; f++)
            {
                PSA.PSAAnimKeys key = psa.Keys[(info.FirstRawFrame + f) * boneCount + b];
                Vector3 pos = FromPsa(key.Position);
                if (inPlace && b == rootBone)
                    pos = new Vector3(firstPos.X, firstPos.Y, pos.Z);
                positions[f] = useTranslation ? UnrealSpace.Position(pos) : bind.Translation;
                rotations[f] = holdBind ? bind.Rotation : UnrealSpace.Rotation(FromPsa(key.Rotation));
                if (added)
                {
                    rotations[f] = Quaternion.Normalize(delta.Rotation * rotations[f]);
                    if (useTranslation)
                        positions[f] += delta.Offset;
                }
            }

            if (!compact || useTranslation)
            {
                var translation = node.UseTranslation().UseTrackBuilder(clipName);
                int count = compact && positions.All(p => Vector3.DistanceSquared(p, positions[0]) < 1e-10f) ? 1 : frames;
                for (int f = 0; f < count; f++)
                    translation.SetPoint(f / rate, positions[f]);
            }
            var rotation = node.UseRotation().UseTrackBuilder(clipName);
            int rotationCount = compact && rotations.All(q => MathF.Abs(Quaternion.Dot(q, rotations[0])) > 0.999999f) ? 1 : frames;
            for (int f = 0; f < rotationCount; f++)
                rotation.SetPoint(f / rate, rotations[f]);
        }

        // Horizontal root travel over the clip, i.e. the speed it was authored for. ME
        // locomotion clips report 0 (authored in place), so CharacterModel's clip speeds are
        // tuned by eye instead.
        float speed = 0f;
        if (rootBone >= 0 && frames > 1)
        {
            Vector3 start = FromPsa(psa.Keys[info.FirstRawFrame * boneCount + rootBone].Position);
            Vector3 end = FromPsa(psa.Keys[(info.FirstRawFrame + frames - 1) * boneCount + rootBone].Position);
            speed = new Vector2(end.X - start.X, end.Y - start.Y).Length() * UnrealSpace.UnitsToMeters / ((frames - 1) / rate);
        }

        string addedNote = additive == null ? ""
            : $", plus {additive.Name} on {additive.Deltas.Keys.Count(bone => psa.Bones.Any(p => p.Name.Equals(bone, StringComparison.OrdinalIgnoreCase)))}/{additive.Deltas.Count} of its moving bones";
        Console.WriteLine($"  anim {clipName}: {frames} frames @ {rate:F0} fps ({frames / rate:F2} s), "
            + $"{boneCount - skipped}/{boneCount} tracks mapped{(bindBones?.Count > 0 ? $", held at bind: {string.Join(", ", bindBones)}" : "")}{addedNote}, root speed {speed:F2} m/s{(inPlace ? " (removed, in place)" : "")}.");
        return boneCount - skipped;
    }

    // A held pose's per-bone difference from a base pose (both read at their first frame, in glTF
    // space), for Bake's additive. Bones that don't move from the base are left out.
    public sealed class AdditivePose
    {
        public string Name { get; }
        public Dictionary<string, (Quaternion Rotation, Vector3 Offset)> Deltas { get; } = new(StringComparer.OrdinalIgnoreCase);

        public AdditivePose(IMEPackage posePcc, string posePath, IMEPackage basePcc, string basePath)
        {
            Dictionary<string, (Quaternion Rotation, Vector3 Position)> pose = FirstFrame(posePcc, posePath);
            Dictionary<string, (Quaternion Rotation, Vector3 Position)> from = FirstFrame(basePcc, basePath);
            Name = $"{posePath.Split('.').Last()} - {basePath.Split('.').Last()}";
            foreach (var (bone, key) in pose)
            {
                if (!from.TryGetValue(bone, out var start))
                    continue;
                Quaternion turn = Quaternion.Normalize(key.Rotation * Quaternion.Inverse(start.Rotation));
                Vector3 offset = key.Position - start.Position;
                if (AngleBetween(turn, Quaternion.Identity) > 0.05 || offset.Length() > 1e-4f)
                    Deltas[bone] = (turn, offset);
            }
        }

        private static Dictionary<string, (Quaternion Rotation, Vector3 Position)> FirstFrame(IMEPackage pcc, string sequencePath)
        {
            ExportEntry export = pcc.FindExport(sequencePath)
                ?? throw new ArgumentException($"No export '{sequencePath}' in {Path.GetFileName(pcc.FilePath)}.");
            PSA psa = PSA.CreateFrom(ObjectBinary.From<AnimSequence>(export));
            var frame = new Dictionary<string, (Quaternion, Vector3)>(StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < psa.Bones.Count; b++)
            {
                PSA.PSAAnimKeys key = psa.Keys[psa.Infos[0].FirstRawFrame * psa.Bones.Count + b];
                frame[psa.Bones[b].Name] = (UnrealSpace.Rotation(FromPsa(key.Rotation)), UnrealSpace.Position(FromPsa(key.Position)));
            }
            return frame;
        }
    }

    // The bones whose translation the clip drives, from its BioAnimSetData; null (all bones)
    // when the set doesn't list any.
    private static HashSet<string> TranslatedBones(IMEPackage pcc, ExportEntry sequence)
    {
        int setData = sequence.GetProperties().GetProp<ObjectProperty>("m_pBioAnimSetData")?.Value ?? 0;
        if (!pcc.TryGetUExport(setData, out ExportEntry data))
            return null;
        var names = data.GetProperties().GetProp<ArrayProperty<NameProperty>>("UseTranslationBoneNames");
        return names?.Count > 0
            ? names.Select(n => n.Value.Instanced).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
    }

    // Compares a clip's first frame against the bind pose using the PSA conversion below.
    // On a base-pose clip nearly every bone should land within a few degrees and translations
    // should match to ~0 cm; if not, the PSA convention changed and FromPsa needs revisiting.
    public static void ReportConvention(IMEPackage pcc, string sequencePath, MergedSkeleton skeleton)
    {
        ExportEntry export = pcc.FindExport(sequencePath);
        PSA psa = PSA.CreateFrom(ObjectBinary.From<AnimSequence>(export));
        var angles = new List<double>();
        var gaps = new List<double>();
        for (int b = 0; b < psa.Bones.Count; b++)
        {
            string name = psa.Bones[b].Name;
            if (!skeleton.Contains(name))
                continue;
            MergedSkeleton.Bone bone = skeleton.Bones[skeleton.IndexOf(name)];
            PSA.PSAAnimKeys key = psa.Keys[psa.Infos[0].FirstRawFrame * psa.Bones.Count + b];
            angles.Add(AngleBetween(bone.Rotation, UnrealSpace.Rotation(FromPsa(key.Rotation))));
            gaps.Add(Vector3.Distance(bone.Translation, UnrealSpace.Position(FromPsa(key.Position))) * 100);
        }
        static double Median(List<double> v) => v.OrderBy(x => x).ElementAt(v.Count / 2);
        Console.WriteLine($"  convention check on {export.ObjectName.Instanced}: {angles.Count(a => a < 5)}/{angles.Count} bones "
            + $"within 5° of bind (median {Median(angles):F1}°), translation median {Median(gaps):F2} cm.");
    }

    // LEX's PSA keys are mirrored across the XZ plane relative to the RefSkeleton (the ActorX
    // convention): translations have Y negated, and rotations come out as (x, -y, z, w).
    // Verified against the bind pose with --check (107/109 bones within 5°, median 0°).
    private static Vector3 FromPsa(Vector3 position) => new Vector3(position.X, -position.Y, position.Z);

    private static Quaternion FromPsa(Quaternion rotation) => new Quaternion(rotation.X, -rotation.Y, rotation.Z, rotation.W);

    private static double AngleBetween(Quaternion a, Quaternion b)
    {
        float dot = Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));
        return 2 * Math.Acos(Math.Min(1f, dot)) * 180 / Math.PI;
    }
}
