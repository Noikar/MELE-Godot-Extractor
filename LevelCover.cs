using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// ---------------------------------------------------------------
// COVER (--paths): the level's CoverLinks, UE3's cover system, which ME1, ME2, and ME3 share
// ---------------------------------------------------------------
//
// A CoverLink is an actor holding an ordered list of slots: the spots a pawn takes cover at.
// Each pair of neighboring slots is a cover segment a pawn can slide along (the last joins the
// first when the link is bLooped). A slot's place is the link's Location plus its LocationOffset
// turned by the link's Rotation (UE3's CoverLink::GetSlotLocation), and it faces the link's
// Rotation plus its RotationOffset: *into* the cover. Its CoverType says how tall the cover is
// (CT_Standing hides a standing pawn, CT_MidLevel a crouched one), and its flags what a pawn can
// do there: lean out left or right, pop up over it, mantle over it, slip around its corner, or
// swat-turn to the next cover. LEX fills every struct field, so the slot defaults
// (Engine.pcc Default__CoverLink) never need reading; the link's own fields left at their
// defaults are missing, so missing reads as false.
//
// FireLinks (which slots can shoot which) are left out: LE1 stores them as items and LE3 packs
// them; the game's own rays answer that.

public partial class LevelBuilder
{
    // A CoverReference (MantleTarget, SlipTarget, OverlapClaims): a CoverLink and one of its
    // slots, or a CoverSlotMarker (Slot -1), by its NavKey.
    private readonly record struct CoverRef(string Key, int Slot);

    private sealed class CoverSlotInfo
    {
        public Vector3 At;              // glTF space, at the pawn's middle (as the link sits)
        public Vector3 Facing;          // glTF space, unit length, into the cover
        public string Type;             // "standing", "mid", or "none"
        public List<string> Actions = new List<string>();
        public string MarkerKey;        // its CoverSlotMarker's NavKey (a path point), or null
        public CoverRef? Mantle;
        public List<(int Direction, CoverRef Target)> Slips = new List<(int, CoverRef)>();
        public List<CoverRef> Overlaps = new List<CoverRef>();
    }

    private sealed class CoverLinkInfo
    {
        public string Key;
        public bool Looped;
        public bool Circular;
        public List<CoverSlotInfo> Slots = new List<CoverSlotInfo>();
    }

    private readonly List<CoverLinkInfo> _coverLinks = new List<CoverLinkInfo>();
    // Each CoverSlotMarker's slot (marker key -> link key, slot index).
    private readonly Dictionary<string, (string Link, int Slot)> _coverMarkers = new Dictionary<string, (string, int)>();
    // How far each slot's marker sits from the slot we compute (UE units), to check the math.
    private readonly List<float> _coverMarkerGaps = new List<float>();
    // References whose target wasn't exported (a sublevel left out), for the log.
    private readonly List<string> _coverUnresolved = new List<string>();

    // A sublevel's CoverLinks (called beside AddPathNetwork).
    private void AddCover(IMEPackage pcc)
    {
        foreach (ExportEntry link in pcc.Exports.Where(e => e.ClassName == "CoverLink"))
        {
            PropertyCollection props = link.GetProperties();
            ArrayProperty<StructProperty> slots = props.GetProp<ArrayProperty<StructProperty>>("Slots");
            if (slots == null || slots.Count == 0)
                continue;
            bool linkDisabled = props.GetProp<BoolProperty>("bDisabled")?.Value == true;
            CoverLinkInfo info = new CoverLinkInfo
            {
                Key = NavKey(pcc, link),
                Looped = props.GetProp<BoolProperty>("bLooped")?.Value == true,
                Circular = props.GetProp<BoolProperty>("bCircular")?.Value == true,
            };
            // The link's turn without scale (a CoverLink is never scaled, but stay safe).
            Matrix4x4 toWorld = UnrealTransform.WithoutScale(UnrealTransform.ActorToWorld(props));
            for (int i = 0; i < slots.Count; i++)
            {
                StructProperty slot = slots[i];
                bool Flag(string name) => slot.GetProp<BoolProperty>(name)?.Value == true;

                Vector3 offset = slot.GetProp<StructProperty>("LocationOffset") is StructProperty o ? UnrealTransform.Vector(o) : Vector3.Zero;
                Vector3 ueAt = Vector3.Transform(offset, toWorld);
                const float ToRadians = MathF.PI / 32768f;
                float yaw = (slot.GetProp<StructProperty>("RotationOffset")?.GetProp<IntProperty>("Yaw")?.Value ?? 0) * ToRadians;
                Vector3 ueFacing = Vector3.TransformNormal(new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f), toWorld);

                CoverSlotInfo entry = new CoverSlotInfo
                {
                    At = UnrealSpace.Position(ueAt),
                    Facing = Vector3.Normalize(UnrealSpace.Direction(ueFacing)),
                    Type = slot.GetProp<EnumProperty>("CoverType")?.Value.Instanced switch
                    {
                        "CT_Standing" => "standing",
                        "CT_MidLevel" => "mid",
                        _ => "none",
                    },
                };

                // What a pawn can do there: UE3's bCan* (worked out by the path builder) gated by
                // the designer's bAllow*, with the bForce* overrides.
                if (Flag("bLeanLeft"))
                    entry.Actions.Add("lean_left");
                if (Flag("bLeanRight"))
                    entry.Actions.Add("lean_right");
                if ((Flag("bCanPopUp") || Flag("bForceCanPopUp")) && Flag("bAllowPopup"))
                    entry.Actions.Add("pop_up");
                if (Flag("bCanMantle") && Flag("bAllowMantle"))
                    entry.Actions.Add("mantle");
                if ((Flag("bCanCoverSlip_Left") || Flag("bForceCanCoverSlip_Left")) && Flag("bAllowCoverSlip"))
                    entry.Actions.Add("slip_left");
                if ((Flag("bCanCoverSlip_Right") || Flag("bForceCanCoverSlip_Right")) && Flag("bAllowCoverSlip"))
                    entry.Actions.Add("slip_right");
                if (Flag("bCanSwatTurn_Left") && Flag("bAllowSwatTurn"))
                    entry.Actions.Add("swat_left");
                if (Flag("bCanSwatTurn_Right") && Flag("bAllowSwatTurn"))
                    entry.Actions.Add("swat_right");
                if (Flag("bPlayerOnly"))
                    entry.Actions.Add("player_only");
                if (!Flag("bEnabled") || linkDisabled)
                    entry.Actions.Add("disabled");

                // Its marker: a path point, so routes can end on the slot.
                if (pcc.TryGetUExport(slot.GetProp<ObjectProperty>("SlotMarker")?.Value ?? 0, out ExportEntry marker))
                {
                    entry.MarkerKey = NavKey(pcc, marker);
                    _coverMarkers[entry.MarkerKey] = (info.Key, i);
                    Vector3 markerAt = UnrealTransform.ActorToWorld(marker.GetProperties()).Translation;
                    _coverMarkerGaps.Add(Vector3.Distance(markerAt, ueAt));
                }

                if (slot.GetProp<StructProperty>("MantleTarget") is StructProperty mantle && ReadCoverRef(pcc, mantle) is CoverRef target)
                    entry.Mantle = target;
                foreach (StructProperty slip in slot.GetProp<ArrayProperty<StructProperty>>("SlipTarget") ?? new ArrayProperty<StructProperty>("SlipTarget"))
                {
                    if (ReadCoverRef(pcc, slip) is CoverRef to)
                        entry.Slips.Add((slip.GetProp<IntProperty>("Direction")?.Value ?? 0, to));
                }
                foreach (StructProperty claim in slot.GetProp<ArrayProperty<StructProperty>>("OverlapClaims") ?? new ArrayProperty<StructProperty>("OverlapClaims"))
                {
                    if (ReadCoverRef(pcc, claim) is CoverRef other)
                        entry.Overlaps.Add(other);
                }
                info.Slots.Add(entry);
            }
            _coverLinks.Add(info);
        }
    }

    // A CoverReference's target, or null when it names nothing. Its Actor is set within a
    // sublevel, its Guid (the target's NavGuid) across sublevels. A marker's SlotIdx is junk.
    private static CoverRef? ReadCoverRef(IMEPackage pcc, StructProperty reference)
    {
        int slot = reference.GetProp<IntProperty>("SlotIdx")?.Value ?? -1;
        if (pcc.TryGetUExport(reference.GetProp<ObjectProperty>("Actor")?.Value ?? 0, out ExportEntry actor))
            return new CoverRef(NavKey(pcc, actor), actor.ClassName == "CoverLink" ? slot : -1);
        string guid = GuidKey(reference.GetProp<StructProperty>("Guid"));
        return guid == null ? null : new CoverRef(guid, slot);
    }

    // "me_cover", beside "me_paths" (pathIndex: each path point's key -> its index there):
    // "links" ({"first", "count", "looped", "circular"}: a link's slots are "slots"[first ..
    // first + count), in order, each neighboring pair a segment) and "slots" ({"at": [x, y, z],
    // "facing": [x, y, z] into the cover, "type", "link", "actions", and when present "marker"
    // (its path point), "mantle" ([slot, point]: where a mantle over it lands), "slip"
    // ([direction, slot, point] each: -1 left, 1 right), and "overlap" (slots too close to share,
    // UE3's OverlapClaims)}). A target's slot or point is -1 when it has none: a slip or mantle
    // may land on a plain PathNode, and a slot's marker may be off the walkable network.
    private int _coverSlots, _coverStanding, _coverMid;

    private JsonObject WriteCover(Dictionary<string, int> pathIndex)
    {
        // Every slot's index, by its link and place in it.
        Dictionary<string, int> firstSlot = new Dictionary<string, int>();
        Dictionary<string, int> slotCount = new Dictionary<string, int>();
        int count = 0;
        foreach (CoverLinkInfo link in _coverLinks)
        {
            firstSlot[link.Key] = count;
            slotCount[link.Key] = link.Slots.Count;
            count += link.Slots.Count;
        }
        // Each slot's marker, by slot index.
        List<string> markerOf = _coverLinks.SelectMany(l => l.Slots).Select(s => s.MarkerKey).ToList();
        int PointOf(string key) => key != null && pathIndex.TryGetValue(key, out int point) ? point : -1;
        // A reference's slot and path point (each -1 when it has none), or null when it names
        // neither (a target in a sublevel that wasn't exported).
        (int Slot, int Point)? Resolve(CoverRef reference)
        {
            int slot = -1;
            if (reference.Slot >= 0 && firstSlot.TryGetValue(reference.Key, out int first) && reference.Slot < slotCount[reference.Key])
                slot = first + reference.Slot;
            else if (_coverMarkers.TryGetValue(reference.Key, out (string Link, int Slot) owner) && firstSlot.TryGetValue(owner.Link, out int start))
                slot = start + owner.Slot;
            int point = slot >= 0 ? PointOf(markerOf[slot]) : PointOf(reference.Key);
            if (slot < 0 && point < 0)
            {
                _coverUnresolved.Add($"{reference.Key}#{reference.Slot}");
                return null;
            }
            return (slot, point);
        }

        JsonArray links = new JsonArray();
        JsonArray slots = new JsonArray();
        for (int l = 0; l < _coverLinks.Count; l++)
        {
            CoverLinkInfo link = _coverLinks[l];
            links.Add(new JsonObject
            {
                ["first"] = firstSlot[link.Key],
                ["count"] = link.Slots.Count,
                ["looped"] = link.Looped,
                ["circular"] = link.Circular,
            });
            foreach (CoverSlotInfo slot in link.Slots)
            {
                JsonObject entry = new JsonObject
                {
                    ["at"] = new JsonArray(slot.At.X, slot.At.Y, slot.At.Z),
                    ["facing"] = new JsonArray(slot.Facing.X, slot.Facing.Y, slot.Facing.Z),
                    ["type"] = slot.Type,
                    ["link"] = l,
                    ["actions"] = new JsonArray(slot.Actions.Select(a => (JsonNode)a).ToArray()),
                };
                if (slot.MarkerKey != null && pathIndex.TryGetValue(slot.MarkerKey, out int marker))
                    entry["marker"] = marker;
                if (slot.Mantle is CoverRef mantle && Resolve(mantle) is (int, int) across)
                    entry["mantle"] = new JsonArray(across.Slot, across.Point);
                JsonArray slips = new JsonArray();
                foreach ((int direction, CoverRef target) in slot.Slips)
                {
                    if (Resolve(target) is (int, int) to)
                        slips.Add(new JsonArray(direction, to.Slot, to.Point));
                }
                if (slips.Count > 0)
                    entry["slip"] = slips;
                JsonArray overlaps = new JsonArray();
                foreach (CoverRef claim in slot.Overlaps)
                {
                    if (Resolve(claim) is (int, int) other && other.Slot >= 0)
                        overlaps.Add(other.Slot);
                }
                if (overlaps.Count > 0)
                    entry["overlap"] = overlaps;
                slots.Add(entry);

                _coverSlots++;
                if (slot.Type == "standing")
                    _coverStanding++;
                else if (slot.Type == "mid")
                    _coverMid++;
            }
        }
        return new JsonObject { ["links"] = links, ["slots"] = slots };
    }

    private string CoverSummary()
    {
        int Count(string action) => _coverLinks.Sum(l => l.Slots.Count(s => s.Actions.Contains(action)));
        string gaps = _coverMarkerGaps.Count == 0 ? "no markers"
            : $"markers {_coverMarkerGaps.Average():0.#} uu from their slots on average, {_coverMarkerGaps.Max():0.#} at most";
        return $"Cover: {_coverLinks.Count} links, {_coverSlots} slots ({_coverStanding} standing, {_coverMid} mid), "
            + $"{Count("lean_left") + Count("lean_right")} leans, {Count("pop_up")} pop-ups, {Count("mantle")} mantles, "
            + $"{Count("disabled")} disabled, {Count("player_only")} player-only; {gaps}"
            + (_coverUnresolved.Count > 0 ? $"; {_coverUnresolved.Count} references not exported (" + string.Join(", ", _coverUnresolved.Distinct().Take(8)) + ")" : "") + ".";
    }
}
