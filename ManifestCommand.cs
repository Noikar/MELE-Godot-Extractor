using System.Text.Json;
using System.Text.Json.Nodes;

namespace LexExport;

// Exports every entry of a manifest: characters.json (character models), weapons.json (weapon
// models), levels.json (levels and their music), or powers.json (powers' sounds and effects).
// Each is the one list behind both the developer scripts (Tools/export_characters.ps1,
// Tools/export_levels.ps1) and the game's first-run extractor (players), so a new model or
// level goes into its manifest and nowhere else.
//
//   characters  -> <out>/<entry.out>               (MeshCommand, the entry's args)
//   weapons     -> <out>/<entry.out>               (MeshCommand, the entry's args)
//   levels      -> <out>/<id>.glb                   (LevelCommand, the entry's packages)
//                  <out>/<id>.looks.json            (its NPCs' looks: a looks manifest)
//                  <out>/<musicid>.ogg, music.json  (MusicCommand, the entry's music)
//   powers      -> <out>/<id>/sounds.json, .wav    (WwiseExport, each of the entry's sounds)
//                  <out>/<id>/vfx.glb               (PowerVfx, the entry's "vfx")
//   <level>.looks.json -> <out>/<id>.glb            (LevelBuilder.ExportLook, one NPC look;
//                                                   one already there is skipped, since its
//                                                   id is a hash of everything it's made of)
//
// "--only <id>,..." exports just those entries; the game passes the ones whose manifest entry
// changed since its last extraction, so a patch that adds a level doesn't redo every character.
//
// Progress goes to stdout as "PROGRESS <n>/<total> <name>" as each entry starts, "OK <id>"
// after a success, and "FAILED <name>: <reason>" after a failure, for the game to show and
// record; the exit code is 0 only when every entry exported.
//
// Entries export side by side (Lanes at a time; "--lanes <n>" overrides), each inside its own
// EntryScope, with its log printed as one block when it ends.
public static class ManifestCommand
{
    public static int Run(string manifestPath, string outDir, string[] options)
    {
        HashSet<string> only = null;
        int lanes = 0;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == "--only" && i + 1 < options.Length)
                only = options[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            else if (options[i] == "--lanes" && i + 1 < options.Length)
                lanes = int.Parse(options[++i]);
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        bool levels = root.TryGetProperty("levels", out JsonElement levelList);
        bool weapons = root.TryGetProperty("weapons", out JsonElement weaponList);
        bool looks = root.TryGetProperty("looks", out JsonElement lookList);
        bool powers = root.TryGetProperty("powers", out JsonElement powerList);
        JsonElement list = levels ? levelList : weapons ? weaponList : looks ? lookList : powers ? powerList : root.GetProperty("characters");
        JsonElement[] entries = list.EnumerateArray()
            .Where(e => only == null || only.Contains(e.GetProperty("id").GetString()))
            .ToArray();

        var log = new EntryLog(Console.Out);
        Console.SetOut(log);
        Console.SetError(log);
        if (lanes <= 0)
            lanes = Lanes(levels, looks);
        Console.WriteLine($"{entries.Length} entries, {lanes} at a time on {ParallelLimits.Cores} cores.");

        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        int started = 0;
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = lanes }, entry =>
        {
            string id = entry.GetProperty("id").GetString();
            // "<species>: <label>" for characters ("turian: Presidium visitor"), the label otherwise.
            string label = entry.GetProperty("label").GetString();
            string name = levels || weapons || looks || powers ? label : $"{entry.GetProperty("species").GetString()}: {label}";
            log.Direct($"PROGRESS {Interlocked.Increment(ref started)}/{entries.Length} {name}\n");

            string result;
            using (EntryScope scope = EntryScope.Begin())
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (levels)
                        ExportLevel(entry, id, outDir);
                    else if (looks)
                        ExportLook(entry, id, outDir);
                    else if (powers)
                        ExportPower(entry, id, outDir);
                    else
                        ExportModel(entry, outDir);
                    result = $"OK {id}";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    result = $"FAILED {name}: {ex.Message}";
                    failed.Add(name);
                }
                log.Direct($"[{name}, {clock.Elapsed.TotalSeconds:0.0} s]\n{scope.LogText}");
            }
            log.Direct(result + "\n");
            // An entry's big arrays (decoded textures, bakes) are done with, and the GC lets the
            // large object heap fragment and grow on a machine with RAM to spare: once the heap
            // passes CompactAbove, a compacting collection. (Characters: about 20% less CPU time
            // and memory with it. After every entry it paused every lane: the NPC looks' small
            // entries took 4x as long with 10 lanes.)
            if (GC.GetTotalMemory(false) > CompactAbove)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
            log.Direct($"memory: {GC.GetTotalMemory(false) >> 20} MB after {name}\n");
        });

        Console.WriteLine(failed.Count == 0
            ? $"DONE {entries.Length}"
            : $"DONE {entries.Length - failed.Count}/{entries.Length} (failed: {string.Join(", ", failed)})");
        return failed.Count == 0 ? 0 : 1;
    }

    private const long CompactAbove = 3L << 30;

    // How many entries export at once: what the free memory allows, and no more than the cores
    // keep busy, since each entry already runs its bakes in parallel. A level takes one lane;
    // characters and weapons (2048 px bakes) about 4 GB and 4 cores each, up to 3; NPC looks
    // (512 px bakes, too small to keep many cores busy alone) about 2 GB and 2 cores, up to 6.
    private static int Lanes(bool levels, bool looks)
    {
        if (levels)
            return 1;
        long freeGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30;
        return looks
            ? (int)Math.Clamp(Math.Min(freeGb / 2, ParallelLimits.Cores / 2), 1, 6)
            : (int)Math.Clamp(Math.Min(freeGb / 4, ParallelLimits.Cores / 4), 1, 3);
    }

    // A character or weapon: one MeshCommand run with the entry's args.
    private static void ExportModel(JsonElement entry, string outDir)
    {
        string package = Program.ResolvePackage(entry.GetProperty("package").GetString());
        string outPath = Path.Combine(outDir, entry.GetProperty("out").GetString());
        string[] args = entry.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        if (!File.Exists(package))
            throw new FileNotFoundException($"{Path.GetFileName(package)} is missing from the install.");
        if (MeshCommand.Run(package, outPath, args) != 0)
            throw new InvalidOperationException("export returned an error");
    }

    // A power's sounds (Wwise): <out>/<id>/<sound>.wav and sounds.json, { <sound>: { "layers" } },
    // and its visual effects (PowerVfx): <out>/<id>/vfx.glb. A sound written "<event>@<package>"
    // comes from another package than the entry's (the omni-tool's start and end live apart).
    private static void ExportPower(JsonElement entry, string id, string outDir)
    {
        string package = Program.ResolvePackage(entry.GetProperty("package").GetString());
        string folder = Path.Combine(outDir, id);
        var sounds = new JsonObject();
        foreach (JsonProperty sound in entry.GetProperty("sounds").EnumerateObject())
        {
            string[] parts = sound.Value.GetString().Split('@', 2);
            string from = parts.Length == 2 ? Program.ResolvePackage(parts[1]) : package;
            sounds[sound.Name] = WwiseExport.Export(from, parts[0], folder, sound.Name);
        }
        OutputFiles.Write(Path.Combine(folder, "sounds.json"), temporary =>
            File.WriteAllText(temporary, sounds.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        if (entry.TryGetProperty("vfx", out JsonElement vfx))
            LevelBuilder.ExportPowerVfx(package, vfx, Path.Combine(folder, "vfx.glb"));
    }

    // An NPC look, unless an earlier run (or another level's) already baked it.
    private static void ExportLook(JsonElement entry, string id, string outDir)
    {
        string outPath = Path.Combine(outDir, id + ".glb");
        if (File.Exists(outPath))
        {
            Console.WriteLine($"{id}.glb is already there.");
            return;
        }
        LevelBuilder.ExportLook(entry.GetProperty("package").GetString(), entry.GetProperty("actor").GetString(),
            entry.GetProperty("uindex").GetInt32(), id, outPath);
    }

    private static void ExportLevel(JsonElement entry, string id, string outDir)
    {
        var args = entry.GetProperty("packages").EnumerateArray().Select(p => p.GetString()).ToList();
        // Start markers from another package (the location's persistent level), by tag.
        if (entry.TryGetProperty("markers", out JsonElement markers))
            args.AddRange(["--markers", markers.GetProperty("package").GetString(), markers.GetProperty("tags").GetString()]);
        // Ambient NPCs, by Tag.
        if (entry.TryGetProperty("npcs", out JsonElement npcs))
            args.AddRange(["--npcs", npcs.GetString()]);
        string[] musicIds = entry.TryGetProperty("music", out JsonElement music)
            ? music.EnumerateArray().Select(m => m.GetString()).ToArray()
            : [];
        // The music volumes of those MusicIDs, from another package (the persistent level).
        if (entry.TryGetProperty("music_volumes", out JsonElement musicVolumes) && musicIds.Length > 0)
            args.AddRange(["--music-volumes", musicVolumes.GetString(), string.Join(",", musicIds)]);
        // The streaming volumes that show the level's areas, from the same kind of package.
        if (entry.TryGetProperty("stream_volumes", out JsonElement streamVolumes))
            args.AddRange(["--stream-volumes", streamVolumes.GetString()]);
        // Merged chunks (LevelMerge.cs): "merge": false leaves every mesh separate, and
        // "merge_cell" sets their world cells' size in meters.
        if (entry.TryGetProperty("merge", out JsonElement merge) && merge.ValueKind == JsonValueKind.False)
            args.AddRange(["--merge-cell", "0"]);
        else if (entry.TryGetProperty("merge_cell", out JsonElement mergeCell))
            args.AddRange(["--merge-cell", mergeCell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (LevelCommand.Run(Path.Combine(outDir, id + ".glb"), args.ToArray()) != 0)
            throw new InvalidOperationException("level export returned an error");

        if (musicIds.Length > 0 && MusicCommand.Run(outDir, musicIds) != 0)
            throw new InvalidOperationException("music export returned an error");
    }
}
