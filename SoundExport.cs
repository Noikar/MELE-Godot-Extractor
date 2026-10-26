using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Exports Mass Effect 1's one-shot sound effects (doors, so far): a SoundCue in a level package,
// down its node tree to a SoundNodeWave named "<bank>:<event>" (e.g. "sta60:c_sta60_door_oprn").
// Unlike music, these waves aren't streamed: each carries its own ISACT bank pair in its
// RawData (ISACTHelper.GetPairedBanks), whose ISB half holds the one sample, an Ogg Vorbis stream.
//
// Writes <out>/<cue>.ogg (lowercase) as stored and returns what the game needs to play it:
// file, volume (the cue's VolumeMultiplier times the wave's Volume), and the attenuation's
// min_radius and max_radius in meters (0 when the cue has no SoundNodeAttenuation).
public static class SoundExport
{
    public static JsonObject Export(IMEPackage pcc, ExportEntry cue, string outDir)
    {
        PropertyCollection cueProps = cue.GetProperties();
        float volume = cueProps.GetProp<FloatProperty>("VolumeMultiplier")?.Value ?? 1f;
        float minRadius = 0f, maxRadius = 0f;

        // Down the first child of each node (attenuation, modulators) to the wave.
        ExportEntry node = pcc.TryGetUExport(cueProps.GetProp<ObjectProperty>("FirstNode")?.Value ?? 0, out ExportEntry first) ? first : null;
        while (node != null && node.ClassName != "SoundNodeWave")
        {
            PropertyCollection props = node.GetProperties();
            if (node.ClassName == "SoundNodeAttenuation")
            {
                minRadius = TableValue(props, "MinRadius", 0) * 0.01f;
                maxRadius = TableValue(props, "MaxRadius", 1) * 0.01f;
            }
            int child = props.GetProp<ArrayProperty<ObjectProperty>>("ChildNodes")?.FirstOrDefault()?.Value ?? 0;
            node = pcc.TryGetUExport(child, out ExportEntry next) ? next : null;
        }
        if (node == null)
            throw new InvalidDataException($"{cue.ObjectName.Instanced} has no SoundNodeWave in this package");
        volume *= node.GetProperties().GetProp<FloatProperty>("Volume")?.Value ?? 1f;

        byte[] raw = ObjectBinary.From<SoundNodeWave>(node).RawData;
        if (raw == null || raw.Length == 0)
            throw new InvalidDataException($"wave {node.ObjectName.Instanced} has no embedded banks (streamed?)");
        ISACTBankPair pair = ISACTHelper.GetPairedBanks(raw);
        // Most waves hold one sample; some (bullet impacts) hold several that ISACT picks from at
        // random, written as <cue>.ogg, <cue>_2.ogg, ... and listed in "variants".
        List<BankChunk> samples = pair.ISBBank?.GetAllBankChunks().Where(c => c.SubChunks != null && c.SubChunks.Any(s => s is DataBankChunk)).ToList() ?? [];
        if (samples.Count == 0)
            throw new InvalidDataException($"wave {node.ObjectName.Instanced} has no sample");

        string name = cue.ObjectName.Instanced.ToLowerInvariant();
        Directory.CreateDirectory(outDir);
        var variants = new JsonArray();
        for (int i = 0; i < samples.Count; i++)
        {
            var format = samples[i].SubChunks.OfType<CompressionInfoBankChunk>().FirstOrDefault()?.CurrentFormat;
            if (format != CompressionInfoBankChunk.ISACTCompressionFormat.OGGVORBIS)
                throw new InvalidDataException($"wave {node.ObjectName.Instanced} is {format}, not Ogg Vorbis");
            byte[] data = samples[i].SubChunks.First(s => s is DataBankChunk).RawData;
            string file = i == 0 ? name + ".ogg" : $"{name}_{i + 1}.ogg";
            OutputFiles.WriteAllBytes(Path.Combine(outDir, file), data);
            variants.Add(file);
            string title = samples[i].SubChunks.OfType<TitleBankChunk>().FirstOrDefault()?.Value ?? "?";
            Console.WriteLine($"  sound {cue.ObjectName.Instanced} -> {node.ObjectName.Instanced} -> {title}, {data.Length / 1024} KB, "
                + $"volume {volume}, radius {minRadius}-{maxRadius} m");
        }
        var result = new JsonObject
        {
            ["file"] = variants[0].GetValue<string>(),
            ["volume"] = volume,
            ["min_radius"] = minRadius,
            ["max_radius"] = maxRadius,
        };
        if (variants.Count > 1)
            result["variants"] = variants;
        float pitch = cueProps.GetProp<FloatProperty>("PitchMultiplier")?.Value ?? 1f;
        if (pitch != 1f)
            result["pitch"] = pitch;

        // ISACT's own playback rules, on top of UE3's (music.md, "ISACT playback"): the sound
        // event's silence (a playlist entry like the samples, so a cue of cars has gaps between
        // them) and the sample's 3D buffer distance (its own falloff past a minimum distance).
        string eventName = node.ObjectName.Instanced.Split(':').Last();
        BankChunk soundEvent = SoundEvent(pair, eventName);
        byte[] silt = soundEvent?.SubChunks.FirstOrDefault(c => c.ChunkName == "silt")?.RawData;
        if (silt != null && silt.Length >= 20 && BitConverter.ToInt32(silt, 16) != 0)
        {
            result["silence_min"] = BitConverter.ToInt32(silt, 8) / 1000f;
            result["silence_max"] = BitConverter.ToInt32(silt, 12) / 1000f;
        }
        int buffer = samples[0].SubChunks.OfType<IntBankChunk>().FirstOrDefault(c => c.ChunkName == "s3di")?.Value ?? -1;
        BufferDistanceBankChunk distance = pair.ISBBank.GetAllBankChunks().FirstOrDefault(c => c.SubChunks != null
            && c.SubChunks.OfType<IntBankChunk>().Any(i => i.ChunkName == "indx" && i.Value == buffer)
            && c.SubChunks.Any(s => s is BufferDistanceBankChunk))
            ?.SubChunks.OfType<BufferDistanceBankChunk>().First();
        if (distance != null)
        {
            result["isact_min_distance"] = distance.MinDistance;
            result["isact_max_distance"] = distance.MaxDistance;
            result["isact_distance_db"] = distance.DistanceLevel;
        }
        Console.WriteLine($"  sound {cue.ObjectName.Instanced}: silence {result["silence_min"]?.ToString() ?? "none"}-{result["silence_max"]?.ToString() ?? "none"} s, "
            + (distance != null ? $"ISACT distance {distance.MinDistance}-{distance.MaxDistance} m, {distance.DistanceLevel} dB" : "no ISACT distance"));

        // A sound entity: parts that play at once (Flux's casino: two slot timelines and two
        // loops), or one queue of timelines (Flux's conversations), instead of one event.
        var files = new Dictionary<int, string>();
        for (int i = 0; i < samples.Count; i++)
        {
            int index = samples[i].SubChunks.OfType<IntBankChunk>().FirstOrDefault(c => c.ChunkName == "indx")?.Value ?? -1;
            files.TryAdd(index, variants[i].GetValue<string>());
        }
        JsonArray parts = Parts(pair, eventName, files, out bool entity);
        if (parts != null)
        {
            result["parts"] = parts;
            if (entity)
                result["entity"] = true;
            foreach (JsonNode part in parts)
            {
                string what = part!["loop"] != null
                    ? $"loop {part["loop"]}"
                    : string.Join(" | ", part["timelines"]!.AsArray().Select(t => $"{t!["name"]} ({string.Join(" ", t!["lines"]!.AsArray().Select(l => $"{l!["event"]}@{l["time"]}s"))})"));
                Console.WriteLine($"  sound {cue.ObjectName.Instanced}: part gain {part["gain"]}: {what}");
            }
        }
        return result;
    }

    // LEX leaves ISACT's entities raw; read across ME1's:
    //   - The wave's event names an entity whose ecac chunk lists the parts it plays at once, each
    //     a 4-char tag and an int: "sdtl" (a timeline, by indx) or "sdqu" (a queue, by indx).
    //     Without ecac, a wave's event with a qcnt chunk is itself one queue.
    //   - A queue's qcnt holds 8-byte entries: "sdtl" + a timeline's indx (the queue plays one at
    //     random, then the next), or "samp" + a sample's indx (u16; a looping sample).
    //   - A timeline's trck holds 20-byte entries: "snde", the event's indx, the time in ms, and 8
    //     bytes (zero in every one seen). An event's tracks (sdtf) are the samples it picks from by
    //     chance; files maps a sample's indx to its exported file.
    //   - Timelines and queues carry a gain (gbst: the casino's slot timelines 0.126, its loops
    //     0.355 and 0.251).
    // Returns [{ gain, timelines: [{ name, gain, lines: [{ time (s), event, files, chances }] }] }
    // or { gain, loop: file }], or null when the wave's event is a plain event; entity is whether
    // they came from an entity's ecac (several parts at once) rather than one queue.
    private static JsonArray Parts(ISACTBankPair pair, string eventName, Dictionary<int, string> files, out bool entity)
    {
        entity = false;
        List<BankChunk> chunks = pair.ICBBank?.GetAllBankChunks().Where(c => c.SubChunks != null).ToList() ?? [];
        BankChunk named = chunks.FirstOrDefault(c => c.SubChunks.OfType<TitleBankChunk>().Any(t => t.Value.Equals(eventName, StringComparison.OrdinalIgnoreCase)));
        if (named == null)
            return null;

        static int Index(BankChunk c) => c.SubChunks.OfType<IntBankChunk>().FirstOrDefault(i => i.ChunkName == "indx")?.Value ?? -1;
        static string Title(BankChunk c) => c.SubChunks.OfType<TitleBankChunk>().FirstOrDefault()?.Value ?? "?";
        static float Gain(BankChunk c) => c.SubChunks.OfType<FloatBankChunk>().FirstOrDefault(f => f.ChunkName == "gbst")?.Value ?? 1f;
        static byte[] Raw(BankChunk c, string name) => c.SubChunks.FirstOrDefault(s => s.ChunkName == name)?.RawData;
        static string Tag(byte[] raw, int at) => System.Text.Encoding.ASCII.GetString(raw, at, 4);

        JsonObject TimelineOf(int index)
        {
            BankChunk timeline = chunks.FirstOrDefault(c => Index(c) == index && Raw(c, "trck") != null);
            if (timeline == null)
                return null;
            byte[] trck = Raw(timeline, "trck");
            var lines = new JsonArray();
            for (int t = 0; t + 20 <= trck.Length; t += 20)
            {
                if (Tag(trck, t) != "snde")
                    continue;
                int eventIndex = BitConverter.ToInt32(trck, t + 4);
                BankChunk soundEvent = chunks.FirstOrDefault(c => Index(c) == eventIndex && c.SubChunks.Any(s => s is SoundEventSoundTracksFour));
                if (soundEvent == null)
                    continue;
                var lineFiles = new JsonArray();
                var chances = new JsonArray();
                foreach (var track in soundEvent.SubChunks.OfType<SoundEventSoundTracksFour>().First().SoundTracks)
                {
                    if (files.TryGetValue((int)(track.BufferIndex & 0xFFFF), out string file))
                    {
                        lineFiles.Add(file);
                        chances.Add(track.Chance);
                    }
                }
                lines.Add(new JsonObject
                {
                    ["time"] = BitConverter.ToInt32(trck, t + 8) / 1000f,
                    ["event"] = Title(soundEvent),
                    ["files"] = lineFiles,
                    ["chances"] = chances,
                });
            }
            return new JsonObject { ["name"] = Title(timeline), ["gain"] = Gain(timeline), ["lines"] = lines };
        }

        // A queue: its timelines as one part, or a looping sample.
        JsonObject QueuePart(BankChunk queue)
        {
            byte[] qcnt = Raw(queue, "qcnt");
            var timelines = new JsonArray();
            for (int q = 0; q + 8 <= qcnt.Length; q += 8)
            {
                if (Tag(qcnt, q) == "samp" && files.TryGetValue(BitConverter.ToUInt16(qcnt, q + 4), out string file))
                    return new JsonObject { ["gain"] = Gain(queue), ["loop"] = file };
                if (Tag(qcnt, q) == "sdtl" && TimelineOf(BitConverter.ToInt32(qcnt, q + 4)) is JsonObject timeline)
                    timelines.Add(timeline);
            }
            return timelines.Count > 0 ? new JsonObject { ["gain"] = Gain(queue), ["timelines"] = timelines } : null;
        }

        var parts = new JsonArray();
        byte[] ecac = Raw(named, "ecac");
        entity = ecac != null;
        if (ecac != null)
        {
            for (int at = 0; at + 8 <= ecac.Length; at += 4)
            {
                string tag = Tag(ecac, at);
                int index = BitConverter.ToInt32(ecac, at + 4);
                if (tag == "sdtl" && TimelineOf(index) is JsonObject timeline)
                    parts.Add(new JsonObject { ["gain"] = 1f, ["timelines"] = new JsonArray(timeline) });
                else if (tag == "sdqu" && chunks.FirstOrDefault(c => Index(c) == index && Raw(c, "qcnt") != null) is BankChunk queue
                    && QueuePart(queue) is JsonObject part)
                    parts.Add(part);
            }
        }
        else if (Raw(named, "qcnt") != null && QueuePart(named) is JsonObject part)
            parts.Add(part);
        return parts.Count > 0 ? parts : null;
    }

    // The sound event a wave plays. A wave's event ("c_traffic") is often a sound entity whose
    // qcnt chunk ("snde" + an index) names the event with the tracks ("traffic", by its indx);
    // otherwise it is that event itself. Null when neither is in the bank.
    private static BankChunk SoundEvent(ISACTBankPair pair, string eventName)
    {
        List<BankChunk> chunks = pair.ICBBank?.GetAllBankChunks().Where(c => c.SubChunks != null).ToList() ?? [];
        BankChunk named = chunks.FirstOrDefault(c => c.SubChunks.OfType<TitleBankChunk>().Any(t => t.Value.Equals(eventName, StringComparison.OrdinalIgnoreCase)));
        if (named == null || named.SubChunks.Any(s => s is SoundEventSoundTracksFour))
            return named;
        byte[] qcnt = named.SubChunks.FirstOrDefault(c => c.ChunkName == "qcnt")?.RawData;
        if (qcnt == null || qcnt.Length < 8)
            return null;
        int index = BitConverter.ToInt32(qcnt, 4);
        return chunks.FirstOrDefault(c => c.SubChunks.Any(s => s is SoundEventSoundTracksFour)
            && c.SubChunks.OfType<IntBankChunk>().Any(i => i.ChunkName == "indx" && i.Value == index));
    }

    // The sounds an AnimSequence's AnimNotify_Sounds play (doors, a gun folding), each Export's
    // result plus "time", or null for none. Each cue is written once per run through `cache`
    // (keyed by cue path; null for a cue that couldn't be exported).
    public static JsonArray ClipSounds(IMEPackage pcc, ExportEntry sequence, string outDir, Dictionary<string, JsonObject> cache)
    {
        var result = new JsonArray();
        foreach (StructProperty entry in sequence.GetProperties().GetProp<ArrayProperty<StructProperty>>("Notifies") ?? Enumerable.Empty<StructProperty>())
        {
            if (!pcc.TryGetUExport(entry.Properties.GetProp<ObjectProperty>("Notify")?.Value ?? 0, out ExportEntry notify)
                || notify.ClassName != "AnimNotify_Sound"
                || !pcc.TryGetUExport(notify.GetProperties().GetProp<ObjectProperty>("SoundCue")?.Value ?? 0, out ExportEntry cue))
                continue;
            if (!cache.TryGetValue(cue.InstancedFullPath, out JsonObject sound))
            {
                try
                {
                    sound = Export(pcc, cue, outDir);
                }
                catch (InvalidDataException ex)
                {
                    Console.WriteLine($"  sound {cue.ObjectName.Instanced} left out: {ex.Message}");
                }
                cache[cue.InstancedFullPath] = sound;
            }
            if (sound == null)
                continue;
            var played = (JsonObject)sound.DeepClone();
            played["time"] = entry.Properties.GetProp<FloatProperty>("Time")?.Value ?? 0f;
            result.Add(played);
        }
        return result.Count > 0 ? result : null;
    }

    // A cooked float distribution's lookup table opens with its output range [min, max] (see
    // ParticleExport); index 0 or 1 picks one end, which for a constant is the value.
    private static float TableValue(PropertyCollection props, string name, int index)
    {
        var table = props.GetProp<StructProperty>(name)?.Properties.GetProp<ArrayProperty<FloatProperty>>("LookupTable");
        return table != null && table.Count > index ? table[index].Value : 0f;
    }
}
