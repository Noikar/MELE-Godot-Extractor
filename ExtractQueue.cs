using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LexExport;

// One shared queue over every manifest an extraction exports (ExtractCommand: the game's whole
// first run in one process; ManifestCommand: one manifest, for the developer scripts).
//
// Every manifest entry is an item with a weight: about how many cores it keeps busy on its own
// (a level ~5, a 2048 px character or weapon bake ~4, a 512 px NPC look ~2, the small kinds 1;
// measured on a 16-thread desktop CPU, 2026-11-21), and the memory it adds at its peak. Items
// start, most expensive kind first, while the running weights fit in ParallelLimits.Cores and
// their memory in what the machine has free, so the levels' mostly single-threaded steps run beside the
// bakes (two levels at once where memory allows) instead of before them, and every item's own
// Parallel loops share one process's cores (separate processes would each assume all of them).
// A finished level queues its NPC looks at once, from the looks manifest it just wrote; a look
// two levels share is queued once, and looks already extracted are skipped by ExportLook (a
// look's id is a hash of it). The tool runs on Server GC (LexExport.csproj): with the
// workstation GC every collection paused all 15 threads, 49 s of a 363 s run.
//
// Measured 2026-11-21 (16 threads, a whole first run, outputs identical but music.json's key
// order and a shared look's material names): 409-450 s as one process per manifest, 280 s here.
// Tried and dropped: capping each item's Parallel loops to its share of the cores (bakes took
// twice as long and the run 395 s), and the level's thread at above-normal priority (no gain).
//
// Output (stdout, read by the game's AssetExtractor; every line but QUEUED and DONE names its item):
//   QUEUED <total>                        items known so far (grows when a level adds its looks)
//   START <kind> <id> <label>
//   STEP <kind> <id> <n>/<total> <name>   a level's steps (EntrySteps)
//   OK <kind> <id>
//   FAILED <kind> <id> <label>: <reason>
//   DONE <ok>/<total>
// with each item's own log printed as one "[<label>, <s> s]" block when it ends (EntryScope).
public static class ExtractQueue
{
    // One manifest to export: its entries (all, or those in Only) into Out. A levels job with
    // LooksOut also exports each level's NPC looks there once the level is written.
    public sealed record Job(string Manifest, string Out, HashSet<string> Only = null, string LooksOut = null);

    private sealed class Item
    {
        public string Kind;
        public string Id;
        public string Label;
        public JsonElement Entry;
        public Job Job;
        public int Weight;
        public int Priority;
        public double MemoryGb;
    }

    // Cores an item of each kind keeps busy, the order kinds start in (lower first: levels
    // first, since their looks wait on them, then the long bakes, the small kinds filling gaps),
    // and the memory it adds at its peak over the shared caches (SharedGb). Measured 2026-11-21
    // on Server GC: a whole first run, two levels at once among the bakes, peaked at 9.7 GB.
    private static (int Weight, int Priority, double MemoryGb) Cost(string kind) => kind switch
    {
        ManifestCommand.Levels => (5, 0, 4),
        ManifestCommand.Characters => (4, 1, 1.5),
        ManifestCommand.Looks => (2, 2, 0.75),
        ManifestCommand.Weapons => (4, 3, 1.5),
        ManifestCommand.Powers => (1, 4, 0.5),
        ManifestCommand.Props => (1, 5, 0.5),
        ManifestCommand.Canon => (1, 6, 0.25),
        _ => (1, 7, 0.25),
    };

    // What the process holds between items (decoded textures, parsed shader caches, finished
    // bakes), and what's left to everything else on the machine.
    private const double SharedGb = 2;
    private const double HeadroomGb = 2;

    // Item threads only wait on their own Parallel loops, but some of the work (expression
    // trees, LEX's parsing) recurses deeply; a roomy stack costs nothing until it's touched.
    private const int ItemStackBytes = 16 * 1024 * 1024;

    // An item that leaves the heap above this, and half again above what the last compaction
    // left, is followed by a compacting collection (see Finish).
    private const long CompactAbove = 3L << 30;

    private static readonly object Gate = new object();
    private static readonly List<Item> Pending = new List<Item>();
    private static readonly List<JsonDocument> Documents = new List<JsonDocument>();
    private static readonly HashSet<string> QueuedLooks = new HashSet<string>(StringComparer.Ordinal);
    private static EntryLog _log;
    private static int _total;
    private static int _running;
    private static int _runningWeight;
    private static double _reservedGb;
    private static double _memoryBudgetGb;
    private static int _failed;
    private static long _compactedHeap;
    private static readonly List<string> FailedLabels = new List<string>();

    // Exports every job's entries; 0 only when every item exported. maxItems caps how many run at
    // once (the manifest command's --lanes; 0 for no cap but the weights).
    public static int Run(IReadOnlyList<Job> jobs, int maxItems = 0)
    {
        _log = new EntryLog(Console.Out);
        Console.SetOut(_log);
        Console.SetError(_log);
        try
        {
            foreach (Job job in jobs)
                Add(job);
            // The memory items may reserve: what's free now (plus what this process already
            // holds), less the shared caches and some room for the rest of the machine.
            using (Process self = Process.GetCurrentProcess())
                _memoryBudgetGb = (AvailablePhysicalBytes() + self.WorkingSet64) / (double)(1L << 30) - SharedGb - HeadroomGb;
            _log.Direct($"{_total} items, on {ParallelLimits.Cores} cores, with {_memoryBudgetGb:0.0} GB for them.\n");
            _log.Direct($"QUEUED {_total}\n");

            lock (Gate)
            {
                while (Pending.Count > 0 || _running > 0)
                {
                    Item next = Pending.Count > 0 && (maxItems <= 0 || _running < maxItems) ? NextStartable() : null;
                    if (next == null)
                    {
                        Monitor.Wait(Gate);
                        continue;
                    }
                    Pending.Remove(next);
                    _running++;
                    _runningWeight += next.Weight;
                    _reservedGb += next.MemoryGb;
                    Thread thread = new Thread(() => Export(next), ItemStackBytes)
                    {
                        Name = $"{next.Kind} {next.Id}",
                        IsBackground = true,
                    };
                    thread.Start();
                }
            }

            _log.Direct($"GC: {GC.GetTotalPauseDuration().TotalSeconds:0.0} s paused; collections {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}"
                + $" (gen 0 / 1 / 2), {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} GC.\n");
            int ok = _total - _failed;
            _log.Direct(_failed == 0
                ? $"DONE {ok}/{_total}\n"
                : $"DONE {ok}/{_total} (failed: {string.Join(", ", FailedLabels)})\n");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            foreach (JsonDocument document in Documents)
                document.Dispose();
            Documents.Clear();
        }
    }

    // The first pending item, in priority order, whose weight and memory fit beside what's
    // running; any item when nothing runs, so a heavy one never waits forever. Called under Gate.
    private static Item NextStartable()
    {
        foreach (Item item in Pending.OrderBy(i => i.Priority))
        {
            if (_running == 0)
                return item;
            if (_runningWeight + item.Weight <= ParallelLimits.Cores && _reservedGb + item.MemoryGb <= _memoryBudgetGb)
                return item;
        }
        return null;
    }

    // Queues a job's entries (Only's, or all of them). Called before the run and, for a level's
    // looks, from the level's thread under Gate.
    private static void Add(Job job)
    {
        (JsonDocument document, string kind, JsonElement list) = ManifestCommand.Read(job.Manifest);
        Documents.Add(document);
        (int weight, int priority, double memoryGb) = Cost(kind);
        foreach (JsonElement entry in list.EnumerateArray())
        {
            string id = entry.GetProperty("id").GetString();
            if (job.Only != null && !job.Only.Contains(id))
                continue;
            // Two levels sharing a look list it twice; it bakes once, from whichever comes first.
            if (kind == ManifestCommand.Looks && !QueuedLooks.Add(id))
                continue;
            Pending.Add(new Item
            {
                Kind = kind,
                Id = id,
                Label = ManifestCommand.Label(kind, entry),
                Entry = entry,
                Job = job,
                Weight = weight,
                Priority = priority,
                MemoryGb = memoryGb,
            });
            _total++;
        }
    }

    private static void Export(Item item)
    {
        _log.Direct($"START {item.Kind} {item.Id} {item.Label}\n");
        string result;
        bool ok = false;
        using (EntryScope scope = EntryScope.Begin())
        {
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                ManifestCommand.Export(item.Kind, item.Entry, item.Id, item.Job.Out, new EntrySteps($"{item.Kind} {item.Id}"));
                result = $"OK {item.Kind} {item.Id}";
                ok = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                result = $"FAILED {item.Kind} {item.Id} {item.Label}: {ex.Message}";
            }
            _log.Direct($"[{item.Label}, {clock.Elapsed.TotalSeconds:0.0} s]\n{scope.LogText}");
        }
        _log.Direct(result + "\n");
        Finish(item, ok);
    }

    // Frees the item's share, queues a written level's looks, and wakes the scheduler.
    private static void Finish(Item item, bool ok)
    {
        // An item's big arrays (decoded textures, bakes) are done with, and the GC lets the large
        // object heap fragment and grow on a machine with RAM to spare. Compacting after every
        // item paused every running one (55 NPC looks took 4x as long at 10 at once), so only
        // past CompactAbove and well above what the last compaction left.
        long heap = GC.GetTotalMemory(false);
        bool compact = false;
        lock (Gate)
        {
            if (heap > CompactAbove && heap > _compactedHeap * 3 / 2)
            {
                compact = true;
                _compactedHeap = long.MaxValue;
            }
        }
        if (compact)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            long after = GC.GetTotalMemory(false);
            lock (Gate)
                _compactedHeap = after;
            _log.Direct($"memory: {heap >> 20} MB after {item.Label}, compacted to {after >> 20} MB\n");
        }
        else
        {
            _log.Direct($"memory: {heap >> 20} MB after {item.Label}\n");
        }

        lock (Gate)
        {
            _running--;
            _runningWeight -= item.Weight;
            _reservedGb -= item.MemoryGb;
            if (!ok)
            {
                _failed++;
                FailedLabels.Add(item.Label);
            }
            else if (item.Kind == ManifestCommand.Levels && item.Job.LooksOut != null)
            {
                string looks = Path.Combine(item.Job.Out, item.Id + ManifestCommand.LooksSuffix);
                if (File.Exists(looks))
                {
                    try
                    {
                        Add(new Job(looks, item.Job.LooksOut));
                        _log.Direct($"QUEUED {_total}\n");
                    }
                    catch (Exception ex)
                    {
                        _failed++;
                        FailedLabels.Add($"{item.Label}'s NPC looks");
                        _log.Direct($"FAILED {item.Kind} {item.Id} {item.Label}: its looks manifest didn't read: {ex.Message}\n");
                    }
                }
            }
            Monitor.PulseAll(Gate);
        }
    }

    // The machine's free physical memory (GlobalMemoryStatusEx), or 8 GB if Windows won't say.
    private static long AvailablePhysicalBytes()
    {
        MemoryStatus status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : 8L << 30;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
