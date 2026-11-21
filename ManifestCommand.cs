using System.Text.Json;
using System.Text.Json.Nodes;

namespace LexExport;

// Exports every entry of a manifest: characters.json (character models), weapons.json (weapon
// models), levels.json (levels and their music), powers.json (powers' sounds and effects),
// canon.json (the games' own conversation lines, text and voice), props.json (static props), or
// glyphs.json (the games' controller and mouse button icons, for the HUD).
// Each is the one list behind both the developer scripts (Tools/export_characters.ps1,
// Tools/export_levels.ps1) and the game's first-run extractor (players, through ExtractCommand,
// which runs every manifest in one queue), so a new model or level goes into its manifest and
// nowhere else.
//
//   characters  -> <out>/<entry.out>               (MeshCommand, the entry's args)
//   weapons     -> <out>/<entry.out>               (MeshCommand, the entry's args)
//   levels      -> <out>/<id>.glb                   (LevelCommand, the entry's packages)
//                  <out>/<id>.looks.json            (its NPCs' looks: a looks manifest)
//                  <out>/<musicid>.ogg, music.json  (MusicCommand, the entry's music)
//   powers      -> <out>/<id>/sounds.json, .wav    (WwiseExport, each of the entry's sounds)
//                  <out>/<id>/vfx.glb               (PowerVfx, the entry's "vfx")
//   canon       -> <out>/<id>.canon, <out>/<id>/    (CanonExport: a conversation's lines and voices)
//   props       -> <out>/<entry.out>               (LevelBuilder.ExportProp, the entry's mesh)
//   glyphs      -> <out>/<glyph id>.png            (each of the entry's Texture2Ds, as stored)
//   <level>.looks.json -> <out>/<id>.glb            (LevelBuilder.ExportLook, one NPC look;
//                                                   one already there is skipped, since its
//                                                   id is a hash of everything it's made of)
//
// "--only <id>,..." exports just those entries. "--lanes <n>" runs at most n at once (by default
// ExtractQueue's weights decide). Output and scheduling: ExtractQueue.
public static class ManifestCommand
{
    // The kinds, as each manifest's list is named (and the game's ExtractedAssets kinds).
    public const string Characters = "characters";
    public const string Weapons = "weapons";
    public const string Levels = "levels";
    public const string Looks = "looks";
    public const string Powers = "powers";
    public const string Canon = "canon";
    public const string Props = "props";
    public const string Glyphs = "glyphs";

    // What a level's export writes beside its .glb: its NPCs' looks manifest.
    public const string LooksSuffix = ".looks.json";

    private static readonly string[] Kinds = [Levels, Weapons, Looks, Powers, Canon, Props, Glyphs, Characters];

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
        return ExtractQueue.Run([new ExtractQueue.Job(manifestPath, outDir, only)], lanes);
    }

    // A manifest's document (the caller disposes it), its kind, and its entries.
    public static (JsonDocument Document, string Kind, JsonElement List) Read(string manifestPath)
    {
        JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        foreach (string kind in Kinds)
        {
            if (root.TryGetProperty(kind, out JsonElement list))
                return (manifest, kind, list);
        }
        manifest.Dispose();
        throw new InvalidDataException($"{Path.GetFileName(manifestPath)} lists none of {string.Join(", ", Kinds)}.");
    }

    // "<species>: <label>" for characters ("turian: Presidium visitor"), the label otherwise.
    public static string Label(string kind, JsonElement entry)
    {
        string label = entry.GetProperty("label").GetString();
        return kind == Characters ? $"{entry.GetProperty("species").GetString()}: {label}" : label;
    }

    // Exports one entry of a manifest of that kind into outDir; throws when it fails.
    public static void Export(string kind, JsonElement entry, string id, string outDir, EntrySteps steps)
    {
        switch (kind)
        {
            case Levels:
                ExportLevel(entry, id, outDir, steps);
                break;
            case Looks:
                ExportLook(entry, id, outDir);
                break;
            case Powers:
                ExportPower(entry, id, outDir);
                break;
            case Canon:
                CanonExport.Export(entry, id, outDir);
                break;
            case Props:
                LevelBuilder.ExportProp(entry.GetProperty("package").GetString(), entry.GetProperty("mesh").GetString(),
                    Path.Combine(outDir, entry.GetProperty("out").GetString()),
                    entry.TryGetProperty("screen", out JsonElement screen) ? screen : default);
                break;
            case Glyphs:
                ExportGlyphs(entry, outDir);
                break;
            default:
                ExportModel(entry, outDir);
                break;
        }
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
        JsonObject sounds = new JsonObject();
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

    // A set of button glyphs from one package: each of the entry's "glyphs" ({ <id>: <Texture2D
    // path> }) as <out>/<id>.png, at its stored size. The entry's "tint" is the game's business
    // (it reads the manifest), not the exporter's.
    private static void ExportGlyphs(JsonElement entry, string outDir)
    {
        string package = Program.ResolvePackage(entry.GetProperty("package").GetString());
        if (!File.Exists(package))
            throw new FileNotFoundException($"{Path.GetFileName(package)} is missing from the install.");
        using LegendaryExplorerCore.Packages.IMEPackage pcc = LegendaryExplorerCore.Packages.MEPackageHandler.OpenMEPackage(package);
        Directory.CreateDirectory(outDir);
        foreach (JsonProperty glyph in entry.GetProperty("glyphs").EnumerateObject())
        {
            string path = glyph.Value.GetString();
            LegendaryExplorerCore.Packages.ExportEntry export = pcc.Exports.FirstOrDefault(e => e.ClassName == "Texture2D"
                && e.InstancedFullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{path} isn't in {Path.GetFileName(package)}.");
            LegendaryExplorerCore.Unreal.Classes.Texture2D texture = new LegendaryExplorerCore.Unreal.Classes.Texture2D(export);
            bool written = false;
            OutputFiles.Write(Path.Combine(outDir, glyph.Name + ".png"), temporary => written = texture.ExportToPNG(temporary));
            if (!written)
                throw new InvalidOperationException($"{path} didn't export.");
            Console.WriteLine($"{glyph.Name}: {path}");
        }
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

    private static void ExportLevel(JsonElement entry, string id, string outDir, EntrySteps steps)
    {
        List<string> args = entry.GetProperty("packages").EnumerateArray().Select(p => p.GetString()).ToList();
        // Start markers from another package (the location's persistent level), by tag.
        if (entry.TryGetProperty("markers", out JsonElement markers))
            args.AddRange(["--markers", markers.GetProperty("package").GetString(), markers.GetProperty("tags").GetString()]);
        // The Rapid Transit's arrival stages, likewise.
        if (entry.TryGetProperty("transit", out JsonElement transit))
            args.AddRange(["--transit", transit.GetProperty("package").GetString(), transit.GetProperty("tags").GetString()]);
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
        // Only the sublevels ME1 shows in one streaming state: { "package": <persistent level>,
        // "state": "Default", "extra": [<chunks kept besides it>] }.
        if (entry.TryGetProperty("streaming", out JsonElement streaming))
        {
            args.AddRange(["--streaming", streaming.GetProperty("package").GetString(), streaming.GetProperty("state").GetString()]);
            if (streaming.TryGetProperty("extra", out JsonElement extra))
                args.AddRange(["--streaming-extra", string.Join(",", extra.EnumerateArray().Select(c => c.GetString()))]);
        }
        // Merged chunks (LevelMerge.cs): "merge": false leaves every mesh separate, and
        // "merge_cell" sets their world cells' size in meters.
        if (entry.TryGetProperty("merge", out JsonElement merge) && merge.ValueKind == JsonValueKind.False)
            args.AddRange(["--merge-cell", "0"]);
        else if (entry.TryGetProperty("merge_cell", out JsonElement mergeCell))
            args.AddRange(["--merge-cell", mergeCell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        // Lights drawn live over static shadow maps (LE3's MP maps), as LevelBuilder.LightPasses.
        if (entry.TryGetProperty("light_passes", out JsonElement lightPasses) && lightPasses.ValueKind == JsonValueKind.True)
            args.Add("--light-passes");
        // The walkable path network and enemy spawn points, for the server's NPCs (WritePaths).
        if (entry.TryGetProperty("paths", out JsonElement paths) && paths.ValueKind == JsonValueKind.True)
            args.Add("--paths");
        // The playable area, { "min": [x, y, z], "max": [x, y, z] } in glTF space: collision
        // entirely outside it is dropped (LevelBuilder.CollisionBounds).
        static string Corner(JsonElement box, string key) => string.Join(",", box.GetProperty(key).EnumerateArray()
            .Select(v => v.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (entry.TryGetProperty("collision_bounds", out JsonElement bounds))
            args.AddRange(["--collision-bounds", Corner(bounds, "min"), Corner(bounds, "max")]);
        // Actors left out: [{ "package": ..., "actors": "<comma-separated names, wildcards allowed>" }].
        if (entry.TryGetProperty("exclude", out JsonElement exclude))
        {
            foreach (JsonElement source in exclude.EnumerateArray())
                args.AddRange(["--exclude", source.GetProperty("package").GetString(), source.GetProperty("actors").GetString()]);
        }
        // Actors borrowed from other packages: [{ "package": ..., "actors": "<comma-separated names>" }],
        // each optionally cut to a box ("clip": { "min": [x, y, z], "max": [x, y, z] } in glTF space).
        if (entry.TryGetProperty("borrow", out JsonElement borrow))
        {
            foreach (JsonElement source in borrow.EnumerateArray())
            {
                args.AddRange(["--borrow", source.GetProperty("package").GetString(), source.GetProperty("actors").GetString()]);
                if (source.TryGetProperty("clip", out JsonElement clip))
                    args.AddRange(["--borrow-clip", Corner(clip, "min"), Corner(clip, "max")]);
            }
        }
        if (LevelCommand.Run(Path.Combine(outDir, id + ".glb"), args.ToArray(), steps, laterSteps: musicIds.Length > 0 ? 1 : 0) != 0)
            throw new InvalidOperationException("level export returned an error");

        if (musicIds.Length > 0)
        {
            steps.Next("Music");
            if (MusicCommand.Run(outDir, musicIds) != 0)
                throw new InvalidOperationException("music export returned an error");
        }
    }
}
