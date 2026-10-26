using System.Text;
using LegendaryExplorerCore.Sound.ISACT;

namespace LexExport;

// Lists an ISACT .isb bank's samples (indx, title, format, rate, length, and the data's
// first bytes) and, given an output folder, writes each sample's data out as stored. A sound
// event's track finds its sample by indx (the low 16 bits of BufferIndex; see MusicCommand).
public static class IsbCommand
{
    public static int Run(string path, string outDir, string filter)
    {
        using FileStream stream = File.OpenRead(path);
        var bank = new ISACTBank(stream);
        foreach (BankChunk chunk in bank.GetAllBankChunks())
        {
            BankChunk data = chunk.SubChunks?.FirstOrDefault(c => c is DataBankChunk);
            if (data == null)
                continue;
            string title = chunk.SubChunks.OfType<TitleBankChunk>().FirstOrDefault()?.Value ?? "?";
            if (filter != null && !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            string indx = chunk.SubChunks.OfType<IntBankChunk>().FirstOrDefault(i => i.ChunkName == "indx")?.Value.ToString() ?? "?";
            var compression = chunk.SubChunks.OfType<CompressionInfoBankChunk>().FirstOrDefault();
            var info = chunk.SubChunks.OfType<SampleInfoBankChunk>().FirstOrDefault();
            byte[] raw = data.RawData ?? [];
            string head = Encoding.ASCII.GetString(raw.Take(4).Select(b => b >= 32 && b < 127 ? b : (byte)'.').ToArray());
            Console.WriteLine($"{indx,5} {title} | {compression?.CurrentFormat} | {info?.SamplesPerSecond} Hz {info?.BitsPerSample} bit "
                + $"{info?.TimeLength} ms | {raw.Length} bytes '{head}'");

            if (outDir != null && raw.Length > 0)
            {
                Directory.CreateDirectory(outDir);
                string name = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllBytes(Path.Combine(outDir, $"{indx}_{name}.{(head == "OggS" ? "ogg" : "bin")}"), raw);
            }
        }
        return 0;
    }
}
