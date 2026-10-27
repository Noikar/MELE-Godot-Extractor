using System.Diagnostics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Exports Legendary Edition 2 and 3 sounds, which are Wwise (ME1's are ISACT: SoundExport).
//
// A WwiseEvent export names its bank (Relationships.Bank) and its HIRC id (Id). In the bank's
// hierarchy (HIRC) the Event lists actions; each Play action targets a node: a Sound (one WEM),
// a random or sequence container (its children are variants, one picked per play), a blend
// container (its children play together, as layers), or a switch (its first child). Volumes and
// pitches add up from the sound through its parents (containers, actor mixers), in dB and cents.
// A WEM is in some bank's media (DIDX/DATA, LEX's EmbeddedFiles) or, when streamed, in a
// WwiseStream export pointing into an .afc file. Banks share media: Warp's cast plays a sound
// whose WEM is in Wwise_Power_Shared, in SFXGame.pcc, so the event's own package is searched
// first, then SharedPackages.
//
// LEX parses Event and EventAction objects only, so the rest is read here from the v134 (LE3)
// layout:
//   Sound:     u32 plugin, u8 stream type, u32 source id, u32 media size, u8 source bits,
//              [u32 size + params for a source plugin (plugin & 0xF == 2)], then NodeBase.
//   Container, actor mixer: NodeBase first.
//   NodeBase:  u8 override FX, u8 FX count, [u8 bypass + 7 bytes per FX], u8 override
//              attachment, u32 bus, u32 parent, u8 flags, then props (u8 count, ids, 4-byte
//              values: 0x00 volume dB, 0x02 pitch cents, 0x46 attenuation id) and ranged props
//              (u8 count, ids, min/max float pairs).
//   Attenuation: u8 cone enabled, [cone], 7 curve indices, u8 curve count, curves (u8 scaling,
//              u16 point count, points of (f32 from, f32 to, u32 interpolation)); curve 0 is
//              volume over distance, in Unreal units.
// Children are found by their parent id, which covers every container type.
//
// WEMs are decoded with vgmstream-cli (shipped beside the extractor in vgmstream/) to .wav.
public static class WwiseExport
{
    private static readonly string[] SharedPackages = ["SFXGame.pcc"];

    private const uint PropVolume = 0x00, PropPitch = 0x02, PropAttenuation = 0x46;

    // One HIRC node, as far as playing it goes.
    private sealed class Node
    {
        public uint Id;
        public HIRCType Type;
        public uint Parent;
        public float Volume, Pitch;
        public float VolumeMin, VolumeMax, PitchMin, PitchMax;
        public uint Attenuation;
        // Sounds only.
        public uint Source;
        public bool IsCodec;
    }

    // Every bank, WEM, and stream of a set of packages, merged (HIRC and media ids are global).
    private sealed class Library
    {
        public readonly Dictionary<uint, WwiseBankParsed.HIRCObject> Objects = [];
        public readonly Dictionary<uint, Node> Nodes = [];
        public readonly Dictionary<uint, List<uint>> Children = [];
        public readonly Dictionary<uint, byte[]> Media = [];
        public readonly Dictionary<uint, (ExportEntry Export, WwiseStream Stream)> Streams = [];
        public readonly Dictionary<uint, (float Min, float Max)> Attenuations = [];
    }

    // A layer of the sound: its variants (one plays, at random) and their combined settings.
    private sealed class Layer
    {
        public readonly List<uint> Sources = [];
        public float Volume, Pitch, VolumeMin, VolumeMax, PitchMin, PitchMax;
        public uint Attenuation;
    }

    // Writes <outDir>/<name>.wav (layers and variants: <name>_<n>.wav) and returns what the game
    // plays: { "layers": [{ "file", "variants"?, "volume", "pitch"?, "pitch_random"?,
    // "volume_random"?, "min_radius"?, "max_radius"? }] }, volume linear, pitch a multiplier.
    public static JsonObject Export(string packagePath, string eventPath, string outDir, string name)
    {
        IMEPackage pcc = EntryScope.OpenShared(packagePath)
            ?? throw new FileNotFoundException($"{Path.GetFileName(packagePath)} is missing from the install.");
        ExportEntry wwiseEvent = pcc.Exports.FirstOrDefault(e => e.ClassName == "WwiseEvent" && e.InstancedFullPath == eventPath)
            ?? throw new InvalidDataException($"no WwiseEvent {eventPath} in {Path.GetFileName(packagePath)}");
        Library library = Load(packagePath);
        List<Layer> layers = Layers(library, EventId(wwiseEvent));
        if (layers.Count == 0)
            throw new InvalidDataException($"{eventPath} plays nothing this exporter can read");

        Directory.CreateDirectory(outDir);
        var layerJson = new JsonArray();
        int fileIndex = 0;
        foreach (Layer layer in layers)
        {
            var files = new JsonArray();
            foreach (uint source in layer.Sources)
            {
                byte[] wem = WemOf(library, source, packagePath);
                if (wem == null)
                {
                    Console.WriteLine($"  sound {name}: media {source} not found, skipped");
                    continue;
                }
                fileIndex++;
                string file = fileIndex == 1 ? $"{name}.wav" : $"{name}_{fileIndex}.wav";
                Decode(wem, Path.Combine(outDir, file));
                files.Add((JsonNode)file);
            }
            if (files.Count == 0)
                continue;
            var json = new JsonObject
            {
                ["file"] = files[0].GetValue<string>(),
                ["volume"] = MathF.Pow(10f, layer.Volume / 20f),
            };
            if (files.Count > 1)
                json["variants"] = files;
            if (layer.Pitch != 0f)
                json["pitch"] = Cents(layer.Pitch);
            if (layer.PitchMin != 0f || layer.PitchMax != 0f)
                json["pitch_random"] = new JsonArray(Cents(layer.PitchMin), Cents(layer.PitchMax));
            if (layer.VolumeMin != 0f || layer.VolumeMax != 0f)
                json["volume_random"] = new JsonArray(layer.VolumeMin, layer.VolumeMax);
            if (library.Attenuations.TryGetValue(layer.Attenuation, out var radius))
            {
                json["min_radius"] = radius.Min;
                json["max_radius"] = radius.Max;
            }
            Console.WriteLine($"  sound {name}: {string.Join(", ", files.Select(f => f.GetValue<string>()))}, "
                + $"{layer.Volume:0.#} dB, pitch {layer.Pitch:0} cents"
                + (json.ContainsKey("max_radius") ? $", radius {radius.Min:0.#}-{radius.Max:0.#} m" : ""));
            layerJson.Add(json);
        }
        if (layerJson.Count == 0)
            throw new InvalidDataException($"{eventPath}: none of its media was found");
        return new JsonObject { ["layers"] = layerJson };
    }

    // The `wwise <pcc> <filter>` inspection command: each matching event's tree, with each
    // sound's media and where it is.
    public static int Describe(string packagePath, string filter)
    {
        IMEPackage pcc = EntryScope.OpenShared(packagePath);
        Library library = Load(packagePath);
        foreach (ExportEntry e in pcc.Exports.Where(e => e.ClassName == "WwiseEvent"
            && e.InstancedFullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            uint id = EventId(e);
            Console.WriteLine($"{e.InstancedFullPath} (HIRC {id})");
            if (library.Objects.GetValueOrDefault(id) is not WwiseBankParsed.Event wevent)
            {
                Console.WriteLine("  not in any loaded bank");
                continue;
            }
            foreach (uint actionId in wevent.EventActions)
            {
                if (library.Objects.GetValueOrDefault(actionId) is WwiseBankParsed.EventAction action)
                {
                    Console.WriteLine($"  action {action.ActionType} -> {action.ReferencedObjectID}");
                    PrintNode(library, action.ReferencedObjectID, "    ", packagePath);
                }
            }
            foreach (Layer layer in Layers(library, id))
                Console.WriteLine($"  layer: {layer.Sources.Count} variant(s), {layer.Volume:0.#} dB, pitch {layer.Pitch:0} cents, "
                    + $"pitch random {layer.PitchMin:0}..{layer.PitchMax:0}, volume random {layer.VolumeMin:0.#}..{layer.VolumeMax:0.#}, "
                    + (library.Attenuations.TryGetValue(layer.Attenuation, out var r) ? $"radius {r.Min:0.#}-{r.Max:0.#} m" : "no attenuation"));
        }
        return 0;
    }

    private static void PrintNode(Library library, uint id, string indent, string packagePath)
    {
        if (!library.Nodes.TryGetValue(id, out Node node))
        {
            Console.WriteLine($"{indent}{id}: not parsed");
            return;
        }
        string media = node.Type == HIRCType.SoundSXFSoundVoice
            ? !node.IsCodec ? " (source plugin, no media)"
                : library.Media.ContainsKey(node.Source) ? $" media {node.Source} (bank)"
                : library.Streams.ContainsKey(node.Source) ? $" media {node.Source} (stream)"
                : $" media {node.Source} NOT FOUND"
            : "";
        Console.WriteLine($"{indent}{node.Type} {id}: {node.Volume:0.#} dB, pitch {node.Pitch:0}{media}");
        foreach (uint child in library.Children.GetValueOrDefault(id, []))
            PrintNode(library, child, indent + "  ", packagePath);
    }

    private static uint EventId(ExportEntry wwiseEvent) =>
        (uint)(wwiseEvent.GetProperties().GetProp<IntProperty>("Id")?.Value
            ?? throw new InvalidDataException($"{wwiseEvent.InstancedFullPath} has no Id"));

    // ---------------------------------------------------------------
    // The event's layers
    // ---------------------------------------------------------------

    private static List<Layer> Layers(Library library, uint eventId)
    {
        var layers = new List<Layer>();
        if (library.Objects.GetValueOrDefault(eventId) is not WwiseBankParsed.Event wevent)
            return layers;
        foreach (uint actionId in wevent.EventActions)
        {
            if (library.Objects.GetValueOrDefault(actionId) is not WwiseBankParsed.EventAction action)
                continue;
            if (action.ActionType is not (WwiseBankParsed.EventActionType.Play or WwiseBankParsed.EventActionType.Play_LE))
                continue;
            if (!library.Nodes.TryGetValue(action.ReferencedObjectID, out Node target))
                continue;
            // What the target's parents add on top of everything below it.
            var above = new Layer();
            for (uint parent = target.Parent; library.Nodes.TryGetValue(parent, out Node p); parent = p.Parent)
                Add(above, p);
            foreach (Layer layer in Collect(library, target, 0))
            {
                layer.Volume += above.Volume;
                layer.Pitch += above.Pitch;
                layer.PitchMin += above.PitchMin;
                layer.PitchMax += above.PitchMax;
                layer.VolumeMin += above.VolumeMin;
                layer.VolumeMax += above.VolumeMax;
                if (layer.Attenuation == 0)
                    layer.Attenuation = above.Attenuation;
                layers.Add(layer);
            }
        }
        return layers;
    }

    // The layers a node plays, its own settings included.
    private static List<Layer> Collect(Library library, Node node, int depth)
    {
        var result = new List<Layer>();
        if (depth > 16)
            return result;
        List<Node> children = library.Children.GetValueOrDefault(node.Id, [])
            .Select(c => library.Nodes.GetValueOrDefault(c)).Where(c => c != null).ToList();
        switch (node.Type)
        {
            case HIRCType.SoundSXFSoundVoice:
                if (node.IsCodec)
                {
                    var layer = new Layer();
                    layer.Sources.Add(node.Source);
                    result.Add(layer);
                }
                break;
            case HIRCType.BlendContainer:
                foreach (Node child in children)
                    result.AddRange(Collect(library, child, depth + 1));
                break;
            case HIRCType.SwitchContainer:
                if (children.Count > 0)
                    result.AddRange(Collect(library, children[0], depth + 1));
                break;
            default:
                // Random or sequence: the children are variants of one layer, with their settings
                // averaged (the game plays one layer's variants alike).
                var merged = new Layer();
                int count = 0;
                foreach (Node child in children)
                {
                    foreach (Layer sub in Collect(library, child, depth + 1))
                    {
                        merged.Sources.AddRange(sub.Sources);
                        merged.Volume += sub.Volume;
                        merged.Pitch += sub.Pitch;
                        merged.Attenuation = merged.Attenuation != 0 ? merged.Attenuation : sub.Attenuation;
                        count++;
                    }
                }
                if (count > 0)
                {
                    merged.Volume /= count;
                    merged.Pitch /= count;
                    result.Add(merged);
                }
                break;
        }
        foreach (Layer layer in result)
        {
            var own = new Layer();
            Add(own, node);
            layer.Volume += own.Volume;
            layer.Pitch += own.Pitch;
            layer.PitchMin += own.PitchMin;
            layer.PitchMax += own.PitchMax;
            layer.VolumeMin += own.VolumeMin;
            layer.VolumeMax += own.VolumeMax;
            // The nearest attenuation wins, as a child's overrides its parents'.
            if (layer.Attenuation == 0)
                layer.Attenuation = own.Attenuation;
        }
        return result;
    }

    private static void Add(Layer layer, Node node)
    {
        layer.Volume += node.Volume;
        layer.Pitch += node.Pitch;
        layer.PitchMin += node.PitchMin;
        layer.PitchMax += node.PitchMax;
        layer.VolumeMin += node.VolumeMin;
        layer.VolumeMax += node.VolumeMax;
        if (layer.Attenuation == 0)
            layer.Attenuation = node.Attenuation;
    }

    private static float Cents(float cents) => MathF.Pow(2f, cents / 1200f);

    // ---------------------------------------------------------------
    // Banks, media, and streams
    // ---------------------------------------------------------------

    private static Library Load(string packagePath)
    {
        var library = new Library();
        string cooked = Path.GetDirectoryName(packagePath);
        var paths = new List<string> { packagePath };
        paths.AddRange(SharedPackages.Select(p => Path.Combine(cooked, p)).Where(p => !p.Equals(packagePath, StringComparison.OrdinalIgnoreCase)));
        foreach (string path in paths)
        {
            IMEPackage pcc = EntryScope.OpenShared(path);
            if (pcc == null)
                continue;
            foreach (ExportEntry export in pcc.Exports)
            {
                if (export.ClassName == "WwiseBank")
                {
                    // LEX 6.5's parser reads past the end of an empty bank (6.4.1 skipped them),
                    // so check the raw bytes first.
                    if (ObjectBinary.From<WwiseBank>(export).BnkFile is not { Length: > 0 })
                        continue;
                    WwiseBankParsed bank = ObjectBinary.From<WwiseBankParsed>(export);
                    foreach (var media in bank.EmbeddedFiles)
                        library.Media.TryAdd(media.Key, media.Value);
                    foreach (var hirc in bank.HIRCObjects)
                        library.Objects.TryAdd(hirc.Key, hirc.Value);
                }
                else if (export.ClassName == "WwiseStream")
                {
                    WwiseStream stream = ObjectBinary.From<WwiseStream>(export);
                    library.Streams.TryAdd((uint)stream.Id, (export, stream));
                }
            }
        }
        foreach (WwiseBankParsed.HIRCObject hirc in library.Objects.Values)
        {
            if (hirc.Type == HIRCType.Attenuation)
            {
                if (ParseAttenuation(hirc.unparsed) is { } radius)
                    library.Attenuations[hirc.ID] = radius;
                continue;
            }
            Node node = ParseNode(hirc);
            if (node == null)
                continue;
            library.Nodes[node.Id] = node;
        }
        foreach (Node node in library.Nodes.Values)
        {
            if (node.Parent != 0 && library.Nodes.ContainsKey(node.Parent))
            {
                if (!library.Children.TryGetValue(node.Parent, out List<uint> list))
                    library.Children[node.Parent] = list = [];
                list.Add(node.Id);
            }
        }
        foreach (List<uint> list in library.Children.Values)
            list.Sort();
        return library;
    }

    private static Node ParseNode(WwiseBankParsed.HIRCObject hirc)
    {
        bool sound = hirc.Type == HIRCType.SoundSXFSoundVoice;
        if (!sound && hirc.Type is not (HIRCType.RandomOrSequenceContainer or HIRCType.BlendContainer
            or HIRCType.SwitchContainer or HIRCType.ActorMixer))
            return null;
        byte[] data = hirc.unparsed;
        if (data == null)
            return null;
        try
        {
            var node = new Node { Id = hirc.ID, Type = hirc.Type };
            int at = 0;
            if (sound)
            {
                uint plugin = BitConverter.ToUInt32(data, 0);
                node.Source = BitConverter.ToUInt32(data, 5);
                node.IsCodec = (plugin & 0xF) == 1;
                at = 14;
                if ((plugin & 0xF) == 2)
                    at += 4 + (int)BitConverter.ToUInt32(data, at);
            }
            at++; // override parent FX
            int fxCount = data[at++];
            if (fxCount > 0)
                at += 1 + fxCount * 7;
            at++; // override attachment params
            at += 4; // output bus
            node.Parent = BitConverter.ToUInt32(data, at);
            at += 4;
            at++; // priority and other flags
            int props = data[at++];
            int values = at + props;
            for (int i = 0; i < props; i++)
            {
                uint id = data[at + i];
                if (id == PropVolume)
                    node.Volume = BitConverter.ToSingle(data, values + i * 4);
                else if (id == PropPitch)
                    node.Pitch = BitConverter.ToSingle(data, values + i * 4);
                else if (id == PropAttenuation)
                    node.Attenuation = BitConverter.ToUInt32(data, values + i * 4);
            }
            at = values + props * 4;
            int ranged = data[at++];
            int pairs = at + ranged;
            for (int i = 0; i < ranged; i++)
            {
                uint id = data[at + i];
                float min = BitConverter.ToSingle(data, pairs + i * 8);
                float max = BitConverter.ToSingle(data, pairs + i * 8 + 4);
                if (id == PropVolume)
                    (node.VolumeMin, node.VolumeMax) = (min, max);
                else if (id == PropPitch)
                    (node.PitchMin, node.PitchMax) = (min, max);
            }
            return node;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Full volume out to where the volume curve first drops, its last point the farthest; meters.
    private static (float Min, float Max)? ParseAttenuation(byte[] data)
    {
        try
        {
            int at = 0;
            bool cone = data[at++] != 0;
            if (cone)
                at += 5 * 4; // inside angle, outside angle, outside volume, LPF, HPF
            at += 7; // which curve does what
            int curves = data[at++];
            if (curves == 0)
                return null;
            at++; // scaling
            int points = BitConverter.ToUInt16(data, at);
            at += 2;
            if (points < 2)
                return null;
            float firstTo = BitConverter.ToSingle(data, at + 4);
            float min = 0f, max = 0f;
            for (int i = 0; i < points; i++)
            {
                float from = BitConverter.ToSingle(data, at + i * 12);
                float to = BitConverter.ToSingle(data, at + i * 12 + 4);
                if (to >= firstTo - 0.05f)
                    min = from;
                max = from;
            }
            if (max <= 0f || max > 1e6f)
                return null;
            return (min, max);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] WemOf(Library library, uint source, string packagePath)
    {
        if (library.Media.TryGetValue(source, out byte[] wem))
            return wem;
        if (!library.Streams.TryGetValue(source, out var stream))
            return null;
        if (stream.Stream.IsPCCStored)
            return stream.Stream.EmbeddedData;
        string afc = Path.Combine(Path.GetDirectoryName(packagePath), stream.Stream.Filename + ".afc");
        if (!File.Exists(afc))
            return null;
        using FileStream file = File.OpenRead(afc);
        file.Seek(stream.Stream.DataOffset, SeekOrigin.Begin);
        byte[] data = new byte[stream.Stream.DataSize];
        file.ReadExactly(data);
        return data;
    }

    // ---------------------------------------------------------------
    // Decoding
    // ---------------------------------------------------------------

    // vgmstream-cli: VGMSTREAM_CLI if set, else vgmstream/ beside this tool (the build copies it
    // there; vgmstream.ps1 fetches the pinned release).
    private static string Decoder()
    {
        string env = Environment.GetEnvironmentVariable("VGMSTREAM_CLI");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;
        string beside = Path.Combine(AppContext.BaseDirectory, "vgmstream", "vgmstream-cli.exe");
        if (File.Exists(beside))
            return beside;
        throw new FileNotFoundException("vgmstream-cli.exe is missing (expected in the extractor's vgmstream folder).");
    }

    private static void Decode(byte[] wem, string wavPath)
    {
        string input = Path.Combine(Path.GetTempPath(), $"lexexport_{Guid.NewGuid():N}.wem");
        File.WriteAllBytes(input, wem);
        try
        {
            OutputFiles.Write(wavPath, temporary =>
            {
                var start = new ProcessStartInfo(Decoder())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add(temporary);
                start.ArgumentList.Add(input);
                using Process process = Process.Start(start);
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || !File.Exists(temporary))
                    throw new InvalidDataException($"vgmstream-cli failed on {Path.GetFileName(wavPath)}: {output.Trim()}");
            });
        }
        finally
        {
            File.Delete(input);
        }
    }
}
