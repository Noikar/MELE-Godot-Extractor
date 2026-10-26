using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;

namespace LexExport;

// Exports Mass Effect 1's area music by MusicID (the names its BioMusicVolumes carry, e.g.
// "Wards"), following the game's own chain:
//
//   Engine.pcc  BIOG_2DA_Music_X.Music_Music   MusicID -> default SoundCue, fade times
//   Music.pcc   SoundCue -> (SoundNodeLooping) -> SoundNodeWave "<bank>:<event>"
//   Engine.pcc  BioSoundNodeWaveStreamingData <bank>   the bank's ISACT index (ICB): the
//               event's sound track names its sample by buffer index
//   Content/Packages/ISACT/<bank>.isb          the sample itself, an Ogg Vorbis stream
//
// Writes <out>/<musicid>.ogg (lowercase) per ID, as-is, and adds to <out>/music.json describing each
// (file, loop, fade_in, fade_out, volume: the cue's VolumeMultiplier).
public static class MusicCommand
{
    public static int Run(string outDir, string[] musicIds)
    {
        string cooked = Path.GetDirectoryName(Program.ResolvePackage("ME1/Engine.pcc"))!;
        string isactDir = Path.Combine(Path.GetDirectoryName(cooked)!, "Content", "Packages", "ISACT");
        Directory.CreateDirectory(outDir);

        using IMEPackage engine = MEPackageHandler.OpenMEPackage(Path.Combine(cooked, "Engine.pcc"));
        using IMEPackage music = MEPackageHandler.OpenMEPackage(Path.Combine(cooked, "Music.pcc"));
        ExportEntry tableExport = engine.Exports.FirstOrDefault(e => e.InstancedFullPath == "BIOG_2DA_Music_X.Music_Music");
        if (tableExport == null)
        {
            Console.Error.WriteLine("No music table (BIOG_2DA_Music_X.Music_Music) in Engine.pcc.");
            return 1;
        }
        var table = new Bio2DA(tableExport);

        var tracks = new JsonObject();
        int failures = 0;
        foreach (string id in musicIds)
        {
            try
            {
                tracks[id.ToLowerInvariant()] = Export(id, table, engine, music, isactDir, outDir);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"{id}: {ex.Message}");
                failures++;
            }
        }
        // Merged into the folder's existing music.json, so levels can be exported one at a time.
        OutputFiles.Update(Path.Combine(outDir, "music.json"), existing =>
        {
            JsonObject manifest = existing != null ? JsonNode.Parse(existing) as JsonObject ?? new JsonObject() : new JsonObject();
            foreach (var (id, track) in tracks.ToList())
            {
                tracks.Remove(id);
                manifest[id] = track;
            }
            return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        });
        return failures == 0 ? 0 : 1;
    }

    private static JsonObject Export(string id, Bio2DA table, IMEPackage engine, IMEPackage music, string isactDir, string outDir)
    {
        if (!table.TryGetRowIndexByName(id, out int row))
            throw new InvalidDataException("not in the music table");
        string Cell(string column) => table[row, column]?.DisplayableValue ?? "";
        float Number(string column) => float.TryParse(Cell(column), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

        // "music.mus_wards_5": the cue, by name, in Music.pcc.
        string cueName = Cell("SoundCue");
        if (cueName is "" or "MUSIC_NONE")
            throw new InvalidDataException("no default music (MUSIC_NONE)");
        cueName = cueName[(cueName.LastIndexOf('.') + 1)..];
        ExportEntry cue = music.Exports.FirstOrDefault(e => e.ClassName == "SoundCue" && e.ObjectName.Instanced.Equals(cueName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"no SoundCue {cueName} in Music.pcc");
        float volume = cue.GetProperties().GetProp<FloatProperty>("VolumeMultiplier")?.Value ?? 1f;

        // Down the cue's node tree to its wave, noting a looping node on the way.
        bool loop = false;
        ExportEntry node = music.GetUExport(cue.GetProperties().GetProp<ObjectProperty>("FirstNode")?.Value ?? 0);
        while (node != null && node.ClassName != "SoundNodeWave")
        {
            loop |= node.ClassName == "SoundNodeLooping";
            int child = node.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("ChildNodes")?.FirstOrDefault()?.Value ?? 0;
            node = music.TryGetUExport(child, out ExportEntry next) ? next : null;
        }
        if (node == null)
            throw new InvalidDataException($"{cueName} has no SoundNodeWave");

        // "music_bank:mus_wards_5": the ISACT bank and the sound event in it.
        string[] parts = node.ObjectName.Instanced.Split(':');
        if (parts.Length != 2)
            throw new InvalidDataException($"wave {node.ObjectName.Instanced} isn't a streamed ISACT sound");
        string bankName = parts[0], eventName = parts[1];
        float duration = node.GetProperties().GetProp<FloatProperty>("Duration")?.Value ?? 0f;

        ExportEntry streaming = engine.Exports.FirstOrDefault(e => e.ClassName == "BioSoundNodeWaveStreamingData"
            && e.ObjectName.Instanced.Equals(bankName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"no streaming data for bank {bankName}");
        ISACTBankPair pair = ObjectBinary.From<BioSoundNodeWaveStreamingData>(streaming).BankPair;
        string sampleTitle = SampleTitle(pair, eventName);

        // The sample's data, from the bank file.
        string isbPath = Path.Combine(isactDir, bankName + ".isb");
        if (!File.Exists(isbPath))
            throw new InvalidDataException($"no bank file {isbPath}");
        byte[] data;
        int sampleMs;
        using (FileStream stream = File.OpenRead(isbPath))
        {
            var bank = new ISACTBank(stream);
            BankChunk sample = bank.GetAllBankChunks().FirstOrDefault(c => c.SubChunks != null
                && c.SubChunks.OfType<TitleBankChunk>().Any(t => t.Value.Equals(sampleTitle, StringComparison.OrdinalIgnoreCase))
                && c.SubChunks.Any(s => s is DataBankChunk))
                ?? throw new InvalidDataException($"no sample {sampleTitle} in {bankName}.isb");
            var format = sample.SubChunks.OfType<CompressionInfoBankChunk>().FirstOrDefault()?.CurrentFormat;
            if (format != CompressionInfoBankChunk.ISACTCompressionFormat.OGGVORBIS)
                throw new InvalidDataException($"sample {sampleTitle} is {format}, not Ogg Vorbis");
            data = sample.SubChunks.First(s => s is DataBankChunk).RawData;
            sampleMs = sample.SubChunks.OfType<SampleInfoBankChunk>().FirstOrDefault()?.TimeLength ?? 0;
        }

        string file = id.ToLowerInvariant() + ".ogg";
        OutputFiles.WriteAllBytes(Path.Combine(outDir, file), data);
        Console.WriteLine($"{id}: {cueName} -> {bankName}:{eventName} -> {sampleTitle} ({sampleMs / 1000f:0.#} s, wave says {duration:0.#} s), "
            + $"{data.Length / 1024} KB, loop {loop}, fades {Number("FadeInTime")}/{Number("FadeOutTime")} s, volume {volume}");
        return new JsonObject
        {
            ["file"] = file,
            ["loop"] = loop,
            ["fade_in"] = Number("FadeInTime"),
            ["fade_out"] = Number("FadeOutTime"),
            ["volume"] = volume,
        };
    }

    // The sample a bank's sound event plays: its sound track's buffer index (low 16 bits) is a
    // sample's resource index in the bank's own index (the ISB half of the streaming data).
    private static string SampleTitle(ISACTBankPair pair, string eventName)
    {
        BankChunk soundEvent = pair.ICBBank?.GetAllBankChunks().FirstOrDefault(c => c.SubChunks != null
            && c.SubChunks.OfType<TitleBankChunk>().Any(t => t.Value.Equals(eventName, StringComparison.OrdinalIgnoreCase))
            && c.SubChunks.Any(s => s is SoundEventSoundTracksFour))
            ?? throw new InvalidDataException($"no sound event {eventName} in the bank index");
        var tracks = soundEvent.SubChunks.OfType<SoundEventSoundTracksFour>().First().SoundTracks;
        if (tracks.Count == 0)
            throw new InvalidDataException($"sound event {eventName} has no tracks");
        // The high half flags a streamed sample (0x1003B is sample 59).
        uint buffer = tracks[0].BufferIndex & 0xFFFF;

        BankChunk sample = pair.ISBBank?.GetAllBankChunks().FirstOrDefault(c => c.SubChunks != null
            && c.SubChunks.OfType<IntBankChunk>().Any(i => i.ChunkName == "indx" && i.Value == buffer)
            && c.SubChunks.Any(s => s is SampleOffsetBankChunk))
            ?? throw new InvalidDataException($"no sample with index {buffer} for {eventName}");
        return sample.SubChunks.OfType<TitleBankChunk>().First().Value;
    }
}
