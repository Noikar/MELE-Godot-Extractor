using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Movers: level actors (InterpActors) that a looping matinee moves from the moment the level
// loads, e.g. the Wards' sky traffic past Chora's Den (BIOA_STA60_04_LAY: six cars, Car1..Car6,
// on one curved lane at six heights, a 6 s matinee at PlayRate 0.4). ME1 plays these on
// SeqEvent_LevelLoaded; we draw the actor where it was placed unless its path comes along.
//
// Each such actor's mesh node gets "me_mover" in its extras: its node transform sampled over
// one loop, in glTF space ("fps" samples a second of play time over "length" seconds; flat
// "position" [x, y, z, ...] and "rotation" [x, y, z, w, ...] arrays), which the game loops
// (LevelMovers.gd). Movers have no collision: a hull left at the start would block a lane the
// cars have moved on from.
//
// A move track's keys (PosTrack, EulerTrack: roll, pitch, yaw in degrees as X, Y, Z) are
// FInterpCurves, Hermite between keys with their stored tangents. With IMF_RelativeToInitial
// (the default) they're relative to the actor's placed location and rotation, in its frame:
// the matinee's transform is RelTM · InitialTM, so a key's offset turns with the actor.
public partial class LevelBuilder
{
    private const int MoverFps = 20;

    private sealed record MoverTrack(ExportEntry Track, float Length, float PlayRate);

    // The package's movers, by actor UIndex (AddPackage sets it before reading actors).
    private Dictionary<int, MoverTrack> _movers = new();
    private int _moverCount;

    // Finds the looping matinees that a SeqEvent_LevelLoaded starts (its Play input, index 0) and
    // the move track of each group bound to an actor.
    private Dictionary<int, MoverTrack> FindMovers(IMEPackage pcc)
    {
        var movers = new Dictionary<int, MoverTrack>();
        Kismet kismet = KismetOf(pcc);
        foreach (ExportEntry matinee in pcc.Exports.Where(e => e.ClassName == "SeqAct_Interp"))
        {
            PropertyCollection props = matinee.GetProperties();
            if (props.GetProp<BoolProperty>("bLooping")?.Value != true || !StartedOnLoad(pcc, matinee))
                continue;
            var bindings = kismet.Bindings.Where(b => b.Op == matinee).ToList();
            ExportEntry data = bindings.Where(b => b.Link == "Data").SelectMany(b => b.Objects)
                .Select(i => pcc.TryGetUExport(i, out ExportEntry e) ? e : null)
                .FirstOrDefault(e => e?.ClassName == "InterpData");
            if (data == null)
                continue;
            PropertyCollection dataProps = data.GetProperties();
            float length = dataProps.GetProp<FloatProperty>("InterpLength")?.Value ?? 0f;
            float rate = props.GetProp<FloatProperty>("PlayRate")?.Value ?? 1f;
            if (length <= 0f || rate <= 0f)
                continue;

            foreach (ObjectProperty groupRef in dataProps.GetProp<ArrayProperty<ObjectProperty>>("InterpGroups") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (!pcc.TryGetUExport(groupRef.Value, out ExportEntry group))
                    continue;
                PropertyCollection groupProps = group.GetProperties();
                string name = groupProps.GetProp<NameProperty>("GroupName")?.Value.Instanced;
                ExportEntry move = (groupProps.GetProp<ArrayProperty<ObjectProperty>>("InterpTracks") ?? Enumerable.Empty<ObjectProperty>())
                    .Select(t => pcc.TryGetUExport(t.Value, out ExportEntry e) ? e : null)
                    .FirstOrDefault(e => e?.ClassName == "InterpTrackMove");
                if (move == null)
                    continue;
                foreach (int actor in bindings.Where(b => string.Equals(b.Link, name, StringComparison.OrdinalIgnoreCase)).SelectMany(b => b.Objects))
                    movers[actor] = new MoverTrack(move, length, rate);
            }
        }
        return movers;
    }

    private static bool StartedOnLoad(IMEPackage pcc, ExportEntry matinee)
    {
        foreach (ExportEntry loaded in pcc.Exports.Where(e => e.ClassName == "SeqEvent_LevelLoaded"))
        {
            foreach (StructProperty output in loaded.GetProperties().GetProp<ArrayProperty<StructProperty>>("OutputLinks") ?? Enumerable.Empty<StructProperty>())
            {
                foreach (StructProperty target in output.GetProp<ArrayProperty<StructProperty>>("Links") ?? Enumerable.Empty<StructProperty>())
                {
                    if (target.GetProp<ObjectProperty>("LinkedOp")?.Value == matinee.UIndex
                        && (target.GetProp<IntProperty>("InputLinkIdx")?.Value ?? 0) == 0)
                        return true;
                }
            }
        }
        return false;
    }

    // The mover's node transform over one loop, as "me_mover" extras, or null when the actor
    // isn't a mover. componentToActor is the mesh component's offset; actorProps the placed actor.
    private JsonObject MoverExtras(ExportEntry actor, PropertyCollection actorProps, Matrix4x4 componentToActor)
    {
        if (actorProps == null || !_movers.TryGetValue(actor.UIndex, out MoverTrack mover))
            return null;
        PropertyCollection track = mover.Track.GetProperties();
        var positions = Curve(track.GetProp<StructProperty>("PosTrack"));
        var rotations = Curve(track.GetProp<StructProperty>("EulerTrack"));
        bool relative = track.GetProp<EnumProperty>("MoveFrame")?.Value.Instanced is null or "IMF_RelativeToInitial";

        // ActorToWorld is pre-pivot · scale · rotation · location; the matinee replaces the last two.
        Matrix4x4 placed = UnrealTransform.ActorToWorld(actorProps);
        Matrix4x4 initial = UnrealTransform.WithoutScale(placed);
        initial.Translation = actorProps.GetProp<StructProperty>("Location") is StructProperty location
            ? UnrealTransform.Vector(location) : Vector3.Zero;
        // What's left of the placement: the pre-pivot and scale.
        Matrix4x4 scaleOnly = placed * Inverse(initial);

        float seconds = mover.Length / mover.PlayRate;
        int frames = Math.Max(2, (int)MathF.Ceiling(seconds * MoverFps) + 1);
        var position = new JsonArray();
        var rotation = new JsonArray();
        for (int i = 0; i < frames; i++)
        {
            float t = Math.Min(i / (float)MoverFps, seconds) * mover.PlayRate;
            Vector3 offset = Evaluate(positions, t);
            Vector3 euler = Evaluate(rotations, t);
            Matrix4x4 key = Rotator(euler);
            key.Translation = offset;
            Matrix4x4 actorTM = relative ? key * initial : key;
            Matrix4x4 gltf = UnrealTransform.ToGltf(componentToActor * scaleOnly * actorTM);
            Matrix4x4.Decompose(gltf, out _, out Quaternion q, out Vector3 p);
            position.Add(Round(p.X)); position.Add(Round(p.Y)); position.Add(Round(p.Z));
            rotation.Add(Round(q.X)); rotation.Add(Round(q.Y)); rotation.Add(Round(q.Z)); rotation.Add(Round(q.W));
        }
        _moverCount++;
        Console.WriteLine($"  mover {actor.ObjectName.Instanced}: {seconds:0.##} s loop, {frames} samples");
        return new JsonObject
        {
            ["length"] = Round(seconds),
            ["fps"] = MoverFps,
            ["position"] = position,
            ["rotation"] = rotation,
        };
    }

    private static float Round(float v) => MathF.Round(v, 5);

    private static Matrix4x4 Inverse(Matrix4x4 m) => Matrix4x4.Invert(m, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;

    private sealed record CurveKey(float Time, Vector3 Value, Vector3 Arrive, Vector3 Leave, string Mode);

    private static List<CurveKey> Curve(StructProperty curve)
    {
        var keys = new List<CurveKey>();
        foreach (StructProperty point in curve?.GetProp<ArrayProperty<StructProperty>>("Points") ?? Enumerable.Empty<StructProperty>())
        {
            keys.Add(new CurveKey(
                point.GetProp<FloatProperty>("InVal")?.Value ?? 0f,
                point.GetProp<StructProperty>("OutVal") is StructProperty v ? UnrealTransform.Vector(v) : Vector3.Zero,
                point.GetProp<StructProperty>("ArriveTangent") is StructProperty a ? UnrealTransform.Vector(a) : Vector3.Zero,
                point.GetProp<StructProperty>("LeaveTangent") is StructProperty l ? UnrealTransform.Vector(l) : Vector3.Zero,
                point.GetProp<EnumProperty>("InterpMode")?.Value.Instanced ?? point.GetProp<ByteProperty>("InterpMode")?.Value.ToString() ?? "CIM_Linear"));
        }
        return keys;
    }

    // UE3's FInterpCurve::Eval: held before the first key and after the last, Hermite between
    // curve keys (tangents scaled by the keys' gap), linear or constant per the earlier key.
    private static Vector3 Evaluate(List<CurveKey> keys, float t)
    {
        if (keys.Count == 0)
            return Vector3.Zero;
        if (t <= keys[0].Time)
            return keys[0].Value;
        for (int i = 0; i < keys.Count - 1; i++)
        {
            CurveKey a = keys[i], b = keys[i + 1];
            if (t >= b.Time)
                continue;
            float gap = b.Time - a.Time;
            if (gap <= 0f)
                return b.Value;
            float u = (t - a.Time) / gap;
            if (a.Mode == "CIM_Constant")
                return a.Value;
            if (a.Mode == "CIM_Linear")
                return Vector3.Lerp(a.Value, b.Value, u);
            float u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * a.Value + (u3 - 2 * u2 + u) * gap * a.Leave
                + (u3 - u2) * gap * b.Arrive + (-2 * u3 + 3 * u2) * b.Value;
        }
        return keys[^1].Value;
    }

    // An Euler key (X roll, Y pitch, Z yaw, degrees) as Unreal's FRotationMatrix.
    private static Matrix4x4 Rotator(Vector3 euler)
    {
        const float ToRadians = MathF.PI / 180f;
        float pitch = euler.Y * ToRadians, yaw = euler.Z * ToRadians, roll = euler.X * ToRadians;
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);
        return new Matrix4x4(
            cp * cy, cp * sy, sp, 0,
            sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp, 0,
            -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp, 0,
            0, 0, 0, 1);
    }
}
