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
        return result;
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
