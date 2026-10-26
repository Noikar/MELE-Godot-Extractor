using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Scenes;

namespace LexExport;

// A level's ambient sounds and music volumes.
//
//   AmbientSounds/<package>_<actor>  one empty node per AmbientSound at its location, whose
//                        extras ("ambient_sound") are SoundExport's description of its cue
//                        (file, volume, min_radius, max_radius, ...) with the AudioComponent's
//                        VolumeMultiplier folded into volume, and "music" when the cue is
//                        music (ME1's clubs play theirs this way: Flux's c_bar_music, Chora's
//                        Den's c_stripclub_music). Its .ogg goes where placeables' sounds go.
//   MusicVolumes/<MusicID>_<n>  one empty node per BioMusicVolume (from --music-volumes),
//                        whose extras ("music_volume") hold its MusicID, MusicPriority, and
//                        brush as convex hulls: flat [x, y, z, ...] point lists in glTF space.
//                        The game plays the MusicID's track (MusicCommand) while the player
//                        is inside; a MusicID whose track is MUSIC_NONE (Flux, Chora's Den)
//                        silences the area music there, leaving the clubs' AmbientSounds.
//   StreamVolumes/<chunk>_<n>  one empty node per BioTriggerStream (from --stream-volumes)
//                        that shows one of the level's chunks, whose extras ("stream_volume")
//                        hold the chunk it belongs to ("in_chunk"), the level's chunks it makes
//                        visible ("visible", its first streaming state: Default, free roam;
//                        the others are plot and cutscene states), and its brush as hulls.
//                        ME1 shows only the chunks the volume the player last entered lists,
//                        so an area's ambient sounds (each carries its "chunk") play only
//                        there: Flux's unattenuated air loop isn't heard on the concourse.
//
// AmbientSounds live in the _SND sublevels (and some _DSG ones: Chora's Den's music is in
// BIOA_STA60_05A_DSG). Music volumes live in a location's persistent level (BIOA_STA00 for
// the whole Citadel), so only those whose MusicID the level asks for are exported. So do the
// stream volumes; a chunk is a package name without its _LAY/_ART/_PLC/_DSG/_SND suffix
// (BIOA_STA60_09_SND is in chunk BIOA_STA60_09, BIOA_STA60_05A_DSG in BIOA_STA60_05A).
public partial class LevelBuilder
{
    private NodeBuilder _ambientNode;
    private NodeBuilder _musicVolumesNode;
    private NodeBuilder _streamVolumesNode;
    private int _ambientSounds, _ambientMusic, _musicVolumeCount, _streamVolumeCount;

    // A package's streaming chunk: its name without the sublevel kind.
    public static string Chunk(string packageName) =>
        Regex.Replace(packageName, "_(LAY|ART|PLC|DSG|SND)$", "", RegexOptions.IgnoreCase);

    private bool IsAmbientSound(ExportEntry actor, PropertyCollection props) =>
        actor.ClassName.StartsWith("AmbientSound", StringComparison.Ordinal) && props.GetProp<ObjectProperty>("AudioComponent") != null;

    // An AmbientSound: its AudioComponent's cue, exported once per run (as placeables' sounds
    // are), at the actor's location. Ones set not to play on their own (bAutoPlay false: a
    // Kismet sequence starts them) are left out.
    private void AddAmbientSound(IMEPackage pcc, ExportEntry actor, PropertyCollection props, Matrix4x4 actorToWorld)
    {
        if (props.GetProp<BoolProperty>("bAutoPlay")?.Value == false
            || !pcc.TryGetUExport(props.GetProp<ObjectProperty>("AudioComponent").Value, out ExportEntry component))
        {
            _skipped[actor.ClassName + " (not autoplay)"] = _skipped.GetValueOrDefault(actor.ClassName + " (not autoplay)") + 1;
            return;
        }
        PropertyCollection componentProps = component.GetProperties();
        if (!pcc.TryGetUExport(componentProps.GetProp<ObjectProperty>("SoundCue")?.Value ?? 0, out ExportEntry cue))
        {
            _skipped[actor.ClassName + " (no cue)"] = _skipped.GetValueOrDefault(actor.ClassName + " (no cue)") + 1;
            return;
        }
        if (!_sounds.TryGetValue(cue.InstancedFullPath, out JsonObject sound))
        {
            try
            {
                sound = SoundExport.Export(pcc, cue, _soundsDir);
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"  sound {cue.ObjectName.Instanced} left out: {ex.Message}");
            }
            _sounds[cue.InstancedFullPath] = sound;
        }
        if (sound == null)
            return;

        var ambient = (JsonObject)sound.DeepClone();
        float componentVolume = componentProps.GetProp<FloatProperty>("VolumeMultiplier")?.Value ?? 1f;
        ambient["volume"] = ambient["volume"]!.GetValue<float>() * componentVolume;
        // ME1 files its club music under the AMBIENCE sound group like any other ambience, so
        // the cue's name is what tells music apart (c_bar_music, c_stripclub_music).
        bool music = cue.ObjectName.Instanced.Contains("music", StringComparison.OrdinalIgnoreCase);
        if (music)
        {
            ambient["music"] = true;
            _ambientMusic++;
        }

        _ambientNode ??= _root.CreateNode("AmbientSounds");
        string packageName = Path.GetFileNameWithoutExtension(pcc.FilePath);
        ambient["chunk"] = Chunk(packageName);
        var node = new NodeBuilder($"{packageName}_{actor.ObjectName.Instanced}", new JsonObject { ["ambient_sound"] = ambient });
        _ambientNode.AddNode(node);
        node.LocalMatrix = Matrix4x4.CreateTranslation(UnrealSpace.Position(actorToWorld.Translation));
        _ambientSounds++;
    }

    // The BioMusicVolumes of a package (a location's persistent level) whose MusicID is one of
    // musicIds, each with its brush's convex hulls.
    public void AddMusicVolumes(IMEPackage pcc, string[] musicIds)
    {
        ExportEntry levelExport = pcc.Exports.FirstOrDefault(e => e.ClassName == "Level");
        if (levelExport == null)
            return;
        _musicVolumesNode ??= _root.CreateNode("MusicVolumes");
        var found = new List<string>();
        foreach (int actorIndex in ObjectBinary.From<Level>(levelExport).Actors)
        {
            if (actorIndex == 0 || !pcc.TryGetUExport(actorIndex, out ExportEntry actor) || actor.ClassName != "BioMusicVolume")
                continue;
            PropertyCollection props = actor.GetProperties();
            string musicId = props.GetProp<NameProperty>("MusicID")?.Value.Instanced ?? "";
            if (!musicIds.Contains(musicId, StringComparer.OrdinalIgnoreCase)
                || props.GetProp<ObjectProperty>("BrushComponent") is not ObjectProperty brushRef
                || !pcc.TryGetUExport(brushRef.Value, out ExportEntry brush)
                || brush.GetProperties().GetProp<StructProperty>("BrushAggGeom") is not StructProperty agg)
                continue;

            JsonArray hulls = BrushHulls(props, agg);
            if (hulls.Count == 0)
                continue;
            int priority = props.GetProp<IntProperty>("MusicPriority")?.Value ?? 0;
            _musicVolumesNode.AddNode(new NodeBuilder($"{musicId}_{_musicVolumeCount}", new JsonObject
            {
                ["music_volume"] = new JsonObject { ["music_id"] = musicId, ["priority"] = priority, ["hulls"] = hulls },
            }));
            _musicVolumeCount++;
            found.Add($"{musicId} ({hulls.Count} hulls, priority {priority})");
        }
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(pcc.FilePath)}: music volumes {(found.Count > 0 ? string.Join(", ", found) : "none")}.");
    }

    // The BioTriggerStreams of a package (a location's persistent level) that show any of the
    // level's chunks, each with its first streaming state's chunk and visible chunks (only the
    // level's own) and its brush's convex hulls.
    public void AddStreamVolumes(IMEPackage pcc, HashSet<string> chunks)
    {
        ExportEntry levelExport = pcc.Exports.FirstOrDefault(e => e.ClassName == "Level");
        if (levelExport == null)
            return;
        _streamVolumesNode ??= _root.CreateNode("StreamVolumes");
        int found = 0;
        foreach (int actorIndex in ObjectBinary.From<Level>(levelExport).Actors)
        {
            if (actorIndex == 0 || !pcc.TryGetUExport(actorIndex, out ExportEntry actor) || actor.ClassName != "BioTriggerStream")
                continue;
            PropertyCollection props = actor.GetProperties();
            StructProperty state = props.GetProp<ArrayProperty<StructProperty>>("StreamingStates")?.FirstOrDefault();
            if (state == null)
                continue;
            string inChunk = state.Properties.GetProp<NameProperty>("InChunkName")?.Value.Instanced ?? "";
            var visible = new JsonArray();
            foreach (NameProperty name in state.Properties.GetProp<ArrayProperty<NameProperty>>("VisibleChunkNames") ?? Enumerable.Empty<NameProperty>())
            {
                if (chunks.Contains(name.Value.Instanced))
                    visible.Add(name.Value.Instanced);
            }
            if ((!chunks.Contains(inChunk) && visible.Count == 0)
                || props.GetProp<ObjectProperty>("BrushComponent") is not ObjectProperty brushRef
                || !pcc.TryGetUExport(brushRef.Value, out ExportEntry brush)
                || brush.GetProperties().GetProp<StructProperty>("BrushAggGeom") is not StructProperty agg)
                continue;
            JsonArray hulls = BrushHulls(props, agg);
            if (hulls.Count == 0)
                continue;
            _streamVolumesNode.AddNode(new NodeBuilder($"{inChunk}_{_streamVolumeCount}", new JsonObject
            {
                ["stream_volume"] = new JsonObject { ["in_chunk"] = inChunk, ["visible"] = visible, ["hulls"] = hulls },
            }));
            _streamVolumeCount++;
            found++;
        }
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(pcc.FilePath)}: {found} stream volumes for {chunks.Count} chunks.");
    }

    // A volume's brush as convex hulls in glTF space: flat [x, y, z, ...] point lists.
    private static JsonArray BrushHulls(PropertyCollection props, StructProperty agg)
    {
        Matrix4x4 actorToWorld = UnrealTransform.ActorToWorld(props);
        var hulls = new JsonArray();
        foreach (Vector3[] hull in ReadAggGeom(agg))
        {
            var points = new JsonArray();
            foreach (Vector3 p in hull)
            {
                Vector3 world = UnrealSpace.Position(Vector3.Transform(p, actorToWorld));
                points.Add(world.X);
                points.Add(world.Y);
                points.Add(world.Z);
            }
            hulls.Add(points);
        }
        return hulls;
    }
}
