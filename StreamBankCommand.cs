using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LexExport;

// Prints the chunk tree of a BioSoundNodeWaveStreamingData's paired banks: the ISACT index
// (sound events, their tracks, and BufferIndex values) that MusicCommand follows to a sample.
// A SoundNodeWave that isn't streamed (one-shot effects, e.g. doors) carries its own pair in
// its RawData, which this prints too.
public static class StreamBankCommand
{
    public static int Run(string package, string path)
    {
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
            _ => c.RawData != null && c.RawData.Length <= 16 ? BitConverter.ToString(c.RawData) : $"{c.RawData?.Length} bytes",
        };
        Console.WriteLine($"{indent}{c.ChunkName} [{c.GetType().Name}] {value}");
        if (c.SubChunks != null)
        {
            foreach (BankChunk s in c.SubChunks)
                Print(s, indent + "  ");
        }
    }
}
