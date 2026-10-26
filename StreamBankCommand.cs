using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Prints the chunk tree of a BioSoundNodeWaveStreamingData's paired banks: the ISACT index
// (sound events, their tracks, and BufferIndex values) that MusicCommand follows to a sample.
// A SoundNodeWave that isn't streamed (one-shot effects, e.g. doors) carries its own pair in
// its RawData, which this prints too. With raw, chunks LEX doesn't parse (ISACT entities:
// secl, secp, ecac, trck, ...) print their bytes in full instead of just their length.
public static class StreamBankCommand
{
    private static bool _raw;

    public static int Run(string package, string path, bool raw = false)
    {
        _raw = raw;
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(Program.ResolvePackage(package));
        ExportEntry export = pcc.Exports.FirstOrDefault(e => e.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (export == null)
        {
            Console.Error.WriteLine($"No export {path}.");
            return 1;
        }

        ISACTBankPair pair = export.ClassName == "SoundNodeWave"
            ? ISACTHelper.GetPairedBanks(ObjectBinary.From<SoundNodeWave>(export).RawData)
            : ObjectBinary.From<BioSoundNodeWaveStreamingData>(export).BankPair;
        foreach (var (label, bank) in new[] { ("ICB", pair.ICBBank), ("ISB", pair.ISBBank) })
        {
            if (bank == null)
                continue;
            Console.WriteLine($"== {label}");
            foreach (BankChunk c in bank.BankChunks)
                Print(c, "  ");
        }
        return 0;
    }

    private static void Print(BankChunk c, string indent)
    {
        string value = c switch
        {
            TitleBankChunk t => t.Value,
            IntBankChunk i => $"{i.HumanName}={i.Value}",
            FloatBankChunk f => $"{f.HumanName}={f.Value}",
            SampleOffsetBankChunk s => $"offset={s.SampleOffset}",
            GroupBankChunk g => g.Value,
            // Each track: its sample (BufferIndex, low 16 bits), playlist chance, gain range, and
            // position (a Spatial Path, or a fixed offset from the emitter).
            SoundEventSoundTracksFour t => string.Join("; ", t.SoundTracks.Select(k =>
                $"buffer {k.BufferIndex & 0xFFFF} chance {k.Chance} gain {k.MinGain}-{k.MaxGain} pitch {k.MinPitch}-{k.MaxPitch} path {k.PathIndex} pos {k.Position}")),
            SoundEventInfoBankChunk e => $"selection={e.EventSelection} chance={e.DefaultChance} equal={e.EqualChance} flags=0x{e.Flags:X}",
            BufferDistanceBankChunk d => $"min={d.MinDistance} max={d.MaxDistance} modifier={d.DistanceLevel} flags=0x{d.DistanceFlags:X}",
            // A sound event's silence (music.md): playlist slot, chance, min ms, max ms, enabled.
            _ when c.ChunkName == "silt" && c.RawData?.Length == 20 =>
                $"slot={BitConverter.ToInt32(c.RawData, 0)} chance={BitConverter.ToInt32(c.RawData, 4)} min={BitConverter.ToInt32(c.RawData, 8)} ms max={BitConverter.ToInt32(c.RawData, 12)} ms enabled={BitConverter.ToInt32(c.RawData, 16)}",
            _ => c.RawData != null && (c.RawData.Length <= 16 || (_raw && c is not DataBankChunk)) ? BitConverter.ToString(c.RawData) : $"{c.RawData?.Length} bytes",
        };
        Console.WriteLine($"{indent}{c.ChunkName} [{c.GetType().Name}] {value}");
        if (c.SubChunks != null)
        {
            foreach (BankChunk s in c.SubChunks)
                Print(s, indent + "  ");
        }
    }
}
