using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Exports one of the games' own conversations as "canon" lines for the game's .banter files to
// link to ({canon: <id>}): every line's text from the player's own talk files, and the voice of
// each line the conversation's owner speaks. Text and voice are EA's, so they're never shipped;
// each player's extractor writes them from their own install (canon.json, ManifestCommand).
//
//   <out>/<id>.canon      JSON: { "conversation", "lines": { "<string id>": { "text", "vo"? } } }
//   <out>/<id>/<n>.ogg    ME1: the line's sample, as stored in its ISACT bank
//   <out>/<id>/<n>.wav    LE2/LE3: the line's Wwise media, decoded (WwiseExport)
//
// The text ids are the conversation's entries and replies (srText) and its wheel paraphrases
// (each link's srParaphrase). Only the owner's entries are voiced (nSpeakerIndex -1): replies are
// Shepard's, and other speakers are squadmates (Avina's triggers hold an Ashley line).
//
// ME1's voice: a SoundNodeWave "<bank>:VO_<id>" (and "VO_<id>_M", the take for a male Shepard;
// the base take is used) names an event in the bank's index, the package's own
// BioSoundNodeWaveStreamingData <bank>; its sample is in Content/Packages/ISACT/<bank>.isb, the
// same chain as MusicCommand's. LE2's: a WwiseStream named after the line, "..._<id>_f_wav" (its
// VO_<id>_f_Play event carries no Wwise id to follow), in the same package.
public static class CanonExport
{
    public static void Export(JsonElement entry, string id, string outDir)
    {
        string packagePath = Program.ResolvePackage(entry.GetProperty("package").GetString());
        string conversationName = entry.GetProperty("conversation").GetString();
        if (!File.Exists(packagePath))
            throw new FileNotFoundException($"{Path.GetFileName(packagePath)} is missing from the install.");

        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(packagePath);
        ExportEntry conversation = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioConversation"
            && e.ObjectName.Instanced.Equals(conversationName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"no BioConversation {conversationName} in {Path.GetFileName(packagePath)}");
        Func<int, string> talk = ConversationCommand.TalkText(pcc, out _);

        // Every line's text, and which ids the owner speaks.
        SortedDictionary<int, string> texts = new SortedDictionary<int, string>();
        HashSet<int> voiced = new HashSet<int>();
        PropertyCollection props = conversation.GetProperties();
        void Read(ArrayProperty<StructProperty> lines, string linksName, bool entries)
        {
            foreach (StructProperty line in lines ?? Enumerable.Empty<StructProperty>())
            {
                int textId = line.Properties.GetProp<StringRefProperty>("srText")?.Value ?? 0;
                AddText(texts, talk, textId);
                if (entries && textId > 0 && (line.Properties.GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1) == -1)
                    voiced.Add(textId);
                foreach (StructProperty link in line.Properties.GetProp<ArrayProperty<StructProperty>>(linksName) ?? Enumerable.Empty<StructProperty>())
                    AddText(texts, talk, link.Properties.GetProp<StringRefProperty>("srParaphrase")?.Value ?? 0);
            }
        }
        Read(props.GetProp<ArrayProperty<StructProperty>>("m_EntryList"), "ReplyListNew", true);
        Read(props.GetProp<ArrayProperty<StructProperty>>("m_ReplyList"), "EntryList", false);

        string voiceDir = Path.Combine(outDir, id);
        Directory.CreateDirectory(voiceDir);
        Dictionary<int, string> voices = pcc.Game == MEGame.LE1
            ? IsactVoices(pcc, voiced, packagePath, voiceDir)
            : WwiseVoices(pcc, voiced, packagePath, voiceDir);

        JsonObject lines = new JsonObject();
        foreach ((int textId, string text) in texts)
        {
            JsonObject line = new JsonObject { ["text"] = text };
            if (voices.TryGetValue(textId, out string file))
                line["vo"] = file;
            lines[textId.ToString()] = line;
        }
        JsonObject canon = new JsonObject
        {
            ["conversation"] = conversation.InstancedFullPath,
            ["lines"] = lines,
        };
        OutputFiles.Write(Path.Combine(outDir, id + ".canon"), temporary =>
            File.WriteAllText(temporary, canon.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        Console.WriteLine($"canon {id}: {texts.Count} lines, {voices.Count} of the owner's {voiced.Count} voiced");
    }

    // ME1's talk files quote each line themselves ("\"Welcome.\""); the quotes aren't the line's.
    private static void AddText(SortedDictionary<int, string> texts, Func<int, string> talk, int id)
    {
        if (id <= 0 || texts.ContainsKey(id))
            return;
        string text = talk(id).Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();
        if (text.Length > 0)
            texts[id] = text;
    }

    // ME1: each voiced line's sample, written as <id>.ogg (Ogg Vorbis as stored).
    private static Dictionary<int, string> IsactVoices(IMEPackage pcc, HashSet<int> voiced, string packagePath, string voiceDir)
    {
        Dictionary<int, string> files = new Dictionary<int, string>();
        string isactDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(packagePath))!, "Content", "Packages", "ISACT");
        // "<bank>:VO_<id>" waves, the bank named once per conversation.
        string bankName = pcc.Exports
            .Where(e => e.ClassName == "SoundNodeWave" && e.ObjectName.Instanced.Contains(":VO_"))
            .Select(e => e.ObjectName.Instanced.Split(':'))
            .Where(parts => parts.Length == 2 && voiced.Contains(VoiceId(parts[1])))
            .Select(parts => parts[0])
            .FirstOrDefault();
        if (bankName == null)
        {
            Console.WriteLine("  no voice waves for these lines");
            return files;
        }
        ExportEntry streaming = pcc.Exports.FirstOrDefault(e => e.ClassName == "BioSoundNodeWaveStreamingData"
            && e.ObjectName.Instanced.Equals(bankName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"no streaming data for voice bank {bankName}");
        ISACTBankPair pair = ObjectBinary.From<BioSoundNodeWaveStreamingData>(streaming).BankPair;

        string isbPath = Path.Combine(isactDir, bankName + ".isb");
        if (!File.Exists(isbPath))
            throw new InvalidDataException($"no bank file {isbPath}");
        using FileStream stream = File.OpenRead(isbPath);
        List<BankChunk> samples = new ISACTBank(stream).GetAllBankChunks()
            .Where(c => c.SubChunks != null && c.SubChunks.Any(s => s is DataBankChunk))
            .ToList();
        foreach (int lineId in voiced.Order())
        {
            string title;
            try
            {
                title = MusicCommand.SampleTitle(pair, MusicCommand.SoundEvent(pair, $"VO_{lineId}"), $"VO_{lineId}");
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"  line {lineId}: no voice ({ex.Message})");
                continue;
            }
            BankChunk sample = samples.FirstOrDefault(c => c.SubChunks.OfType<TitleBankChunk>().Any(t => t.Value.Equals(title, StringComparison.OrdinalIgnoreCase)));
            if (sample == null)
            {
                Console.WriteLine($"  line {lineId}: no sample {title} in {bankName}.isb");
                continue;
            }
            CompressionInfoBankChunk.ISACTCompressionFormat? format = sample.SubChunks.OfType<CompressionInfoBankChunk>().FirstOrDefault()?.CurrentFormat;
            if (format != CompressionInfoBankChunk.ISACTCompressionFormat.OGGVORBIS)
            {
                Console.WriteLine($"  line {lineId}: {title} is {format}, not Ogg Vorbis");
                continue;
            }
            string file = $"{lineId}.ogg";
            OutputFiles.WriteAllBytes(Path.Combine(voiceDir, file), sample.SubChunks.First(s => s is DataBankChunk).RawData);
            files[lineId] = file;
        }
        return files;
    }

    // LE2/LE3: each voiced line's stream, "<conversation>_S.en_us_<...>_<id, 8 digits>_f_wav" (the
    // "_m_wav" take when there's no "_f"), decoded to <id>.wav.
    private static Dictionary<int, string> WwiseVoices(IMEPackage pcc, HashSet<int> voiced, string packagePath, string voiceDir)
    {
        Dictionary<int, string> files = new Dictionary<int, string>();
        List<ExportEntry> streams = pcc.Exports.Where(e => e.ClassName == "WwiseStream" && e.ObjectName.Instanced.StartsWith("en_us_")).ToList();
        foreach (int lineId in voiced.Order())
        {
            ExportEntry stream = streams.FirstOrDefault(e => e.ObjectName.Instanced.EndsWith($"_{lineId:D8}_f_wav", StringComparison.OrdinalIgnoreCase))
                ?? streams.FirstOrDefault(e => e.ObjectName.Instanced.EndsWith($"_{lineId:D8}_m_wav", StringComparison.OrdinalIgnoreCase));
            if (stream == null)
            {
                Console.WriteLine($"  line {lineId}: no voice stream");
                continue;
            }
            string file = $"{lineId}.wav";
            WwiseExport.ExportStream(packagePath, stream, Path.Combine(voiceDir, file));
            files[lineId] = file;
        }
        return files;
    }

    // "VO_111672", "VO_111672_M", "VO_252330_f_Play" -> 111672 / 252330 (0 when it isn't one).
    private static int VoiceId(string name)
    {
        string[] parts = name.Split('_');
        return parts.Length >= 2 && parts[0] == "VO" && int.TryParse(parts[1], out int id) ? id : 0;
    }
}
