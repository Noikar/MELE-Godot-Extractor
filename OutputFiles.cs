using System.Collections.Concurrent;

namespace LexExport;

// Writes the export's files. Each is written to "<path>.tmp" and renamed into place once
// complete, so an interrupted export never leaves a half-written file that looks finished, and
// writes of one path take turns (entries export side by side, and some share files: the
// pistols' fold sounds, a folder's music.json).
public static class OutputFiles
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static void Write(string path, Action<string> write)
    {
        path = Path.GetFullPath(path);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            string temporary = path + ".tmp";
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
    }

    public static void WriteAllBytes(string path, byte[] data) => Write(path, temporary => File.WriteAllBytes(temporary, data));

    // A read-modify-write of one file (a merged JSON), with no other writer of it in between.
    public static void Update(string path, Func<string, string> update)
    {
        path = Path.GetFullPath(path);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            string text = update(File.Exists(path) ? File.ReadAllText(path) : null);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
    }
}
