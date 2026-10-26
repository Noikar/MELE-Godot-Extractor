using System.Numerics;
using System.Text.Json.Nodes;
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
//
// AmbientSounds live in the _SND sublevels (and some _DSG ones: Chora's Den's music is in
// BIOA_STA60_05A_DSG). Music volumes live in a location's persistent level (BIOA_STA00 for
// the whole Citadel), so only those whose MusicID the level asks for are exported.
public partial class LevelBuilder
{
    private NodeBuilder _ambientNode;
    private NodeBuilder _musicVolumesNode;
    private int _ambientSounds, _ambientMusic, _musicVolumeCount;

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
}
