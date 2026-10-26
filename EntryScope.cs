using System.Text;
using LegendaryExplorerCore.Packages;

namespace LexExport;

// What one manifest entry owns while it exports, reachable from anywhere in its work (its own
// Parallel loops included) through an AsyncLocal, since entries export side by side:
//   - the packages it opened to resolve imports (BIOC_Materials, an import's own package), each
//     entry with its own copies, closed when it ends, so no LEX package is shared between
//     entries' threads and none outlives its entry;
//   - its log, printed as one block when it ends (EntryLog), so entries' lines don't interleave.
// Outside a manifest run (the single-asset commands) there's no scope: packages stay open for
// the process and output goes straight to the console.
public sealed class EntryScope : IDisposable
{
    private static readonly AsyncLocal<EntryScope> CurrentScope = new();
    private static readonly Dictionary<string, IMEPackage> ProcessPackages = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IMEPackage> _packages = new(StringComparer.OrdinalIgnoreCase);

    private readonly StringWriter _log = new();

    // Written by the entry's own threads at once, so synchronized.
    public TextWriter Log { get; }

    // Everything the entry printed, ending in a line break.
    public string LogText
    {
        get
        {
            string text = _log.ToString();
            return text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
        }
    }

    private EntryScope() => Log = TextWriter.Synchronized(_log);

    public static EntryScope Current => CurrentScope.Value;

    public static EntryScope Begin()
    {
        var scope = new EntryScope();
        CurrentScope.Value = scope;
        return scope;
    }

    // A package file opened once for the current entry (or the process, outside one); null
    // when the file doesn't exist.
    public static IMEPackage OpenShared(string path)
    {
        Dictionary<string, IMEPackage> packages = Current?._packages ?? ProcessPackages;
        lock (packages)
        {
            if (!packages.TryGetValue(path, out IMEPackage package))
                packages[path] = package = File.Exists(path) ? MEPackageHandler.OpenMEPackage(path) : null;
            return package;
        }
    }

    public void Dispose()
    {
        lock (_packages)
        {
            foreach (IMEPackage package in _packages.Values)
                package?.Dispose();
            _packages.Clear();
        }
        if (CurrentScope.Value == this)
            CurrentScope.Value = null;
    }
}

// Console.Out (and Console.Error, which manifest runs send to stdout) while entries export
// side by side: writes from inside an entry go to its scope's log, the rest straight out.
public sealed class EntryLog : TextWriter
{
    private readonly TextWriter _out;

    public EntryLog(TextWriter output) => _out = TextWriter.Synchronized(output);

    public override Encoding Encoding => _out.Encoding;

    private TextWriter Target => EntryScope.Current?.Log ?? _out;

    public override void Write(char value) => Target.Write(value);
    public override void Write(string value) => Target.Write(value);
    public override void WriteLine(string value) => Target.WriteLine(value);
    public override void WriteLine() => Target.WriteLine();
    public override void Flush() => _out.Flush();

    // Straight out, from inside an entry too: the PROGRESS/OK/FAILED lines the game reads, and
    // an entry's finished log.
    public void Direct(string text)
    {
        _out.Write(text);
        _out.Flush();
    }
}
