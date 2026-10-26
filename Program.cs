using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Compression;
using LegendaryExplorerCore.Packages;
using SharpGLTF.Scenes;

namespace LexExport;

public static class Program
{
    // Folder of an extracted Legendary Explorer release. Baked in at build time from the
    // LexDir MSBuild property; override at runtime with the LEX_DIR environment variable.
    public static readonly string LexDir = ResolveLexDir();

    // The Legendary Edition install. The manifest command takes it as an argument (the game
    // passes the player's install); the developer commands use ME_LE_ROOT, else the first
    // install `locate` finds.
    public static string GameRoot = Environment.GetEnvironmentVariable("ME_LE_ROOT");

    // How every .glb is written. SharpGLTF merges all meshes' vertices into one shared buffer
    // view by default, and Godot's glTF parser copies an accessor's whole buffer view each time
    // it decodes one: on the Wards (one 25 MB view, about 18,000 accessors) that took 117 s of
    // a 121 s load. Unmerged, each mesh gets its own views and the same level parses in 5 s.
    public static readonly SceneBuilderSchema2Settings GltfSettings = new()
    {
        UseStridedBuffers = true,
        MergeBuffers = false,
    };

    // How every .glb is saved: without SharpGLTF's validation pass, which checks the whole model
    // (every accessor of the Wards' 800 MB) and changes nothing in the file.
    public static readonly SharpGLTF.Schema2.WriteSettings GlbSettings = new()
    {
        Validation = SharpGLTF.Validation.ValidationMode.Skip,
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // LegendaryExplorerCore and its dependencies stay in the LEX folder; load them from there.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string path = Path.Combine(LexDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        return Run(args);
    }

    // Kept separate from Main so the JIT doesn't touch LEX types before the resolver is set.
    private static int Run(string[] args)
    {
        // Finding the install needs nothing from LEX or the game.
        if (args[0] == "locate")
            return LocateCommand.Run();
        if (args[0] == "manifest" && args.Length >= 4)
        {
            GameRoot = args[2];
            // The game reads a single pipe; an unread stderr could fill up and stall us.
            Console.SetError(Console.Out);
        }
        if (string.IsNullOrEmpty(GameRoot))
            GameRoot = LocateCommand.Installs().FirstOrDefault();
        if (GameRoot == null)
        {
            Console.Error.WriteLine("No Legendary Edition install found; set ME_LE_ROOT to the folder holding Game\\ME1.");
            return 2;
        }

        // Always one core free; --background (the game extracting while it's played) takes half.
        ParallelLimits.Apply(background: args.Contains("--background"));

        // Native helpers LEX ships next to its DLLs (texture conversion, etc.).
        SetDllDirectory(LexDir);

        LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default, s => Console.Error.WriteLine($"[LEX] {s}"));

        // LE packages are Oodle-compressed; the game ships the decompressor.
        if (!OodleHelper.EnsureOodleDll(GameRoot))
        {
            Console.Error.WriteLine($"Could not load the Oodle DLL from {GameRoot}.");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "list" when args.Length >= 2 => ListCommand.Run(args[1], args.Length >= 3 ? args[2] : null),
                "mesh" when args.Length >= 4 => MeshCommand.Run(args[1], args[2], args[3..]),
                "level" when args.Length >= 3 => LevelCommand.Run(args[1], args[2..]),
                "find" when args.Length >= 5 => FindCommand.Run(args[1], args[2], args[3], args[4]),
                "manifest" when args.Length >= 4 => ManifestCommand.Run(args[1], args[3], args[4..]),
                "music" when args.Length >= 3 => MusicCommand.Run(args[1], args[2..]),
                "exports" when args.Length >= 2 => ExportsCommand.Run(args[1], args.Length >= 3 ? args[2] : null),
                "props" when args.Length >= 3 => PropsCommand.Run(args[1], args[2]),
                "texture" when args.Length >= 4 => TextureCommand.Run(args[1], args[2], args[3], args.Contains("--dds")),
                "particles" when args.Length >= 3 => ParticlesCommand.Run(args[1], args[2]),
                "shaders" when args.Length >= 3 => ShadersCommand.Run(args[1], args[2..]),
                "2da" when args.Length >= 3 => TableCommand.Run(args[1], args[2]),
                "isb" when args.Length >= 2 => IsbCommand.Run(args[1], args.Length >= 3 && args[2] != "-" ? args[2] : null,
                    args.Length >= 4 ? args[3] : null),
                "streambank" when args.Length >= 3 => StreamBankCommand.Run(args[1], args[2], args.Contains("--raw")),
                "reflect" when args.Length >= 2 => ReflectCommand.Run(args[1]),
                "decals" when args.Length >= 2 => DecalsCommand.Run(args[1]),
                "refs" when args.Length >= 3 => RefsCommand.Run(args[1], args[2]),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 3;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            LexExport — Mass Effect LE asset exporter (via LegendaryExplorerCore)

              list <package.pcc> [filter]
                  Lists SkeletalMesh / AnimSet / AnimSequence / Texture2D / Material exports.
                  [filter] is a case-insensitive substring of the export path.

              mesh <package.pcc> <out.glb> <SkeletalMesh path>... [--anim <AnimSequence path>[=Clip]]...
                  Exports the meshes into one skinned .glb on a merged skeleton (body + head),
                  with each --anim baked in as a clip. --check <AnimSequence> verifies the
                  animation conventions against the bind pose instead.
                  --mat <MaterialInstance>,... after a mesh swaps its materials (color variant).
                  --morph <BioMorphFace> adds a morph head, its hair, and the character's
                  appearance overrides. --hide <name> drops sections whose material matches.

              level <out.glb> <package.pcc>... [--max-texture <px>] [--markers <package.pcc> <tags>]
                  Exports the static world of the packages (a level's sublevels; wildcards
                  allowed, e.g. ME1/BIOA_STA60_??_LAY.pcc) into one .glb: mesh instances,
                  lights, start markers, and the player collision as hull and trimesh
                  meshes under a "Collision" node. Textures are capped at 1024 px by default.
                  --markers adds another package's start locations whose Tag matches,
                  e.g. --markers ME1/BIOA_STA00.pcc start_sta60_*

              find <folder> <file glob> <class> <filter>
                  Searches every package matching the glob for exports of <class> whose path
                  contains <filter>, e.g.: find <CookedPCConsole> BioH_*.pcc AnimSequence Idle

              manifest <characters.json|weapons.json|levels.json> <game root> <out folder> [--only <id>,...]
                  Exports every entry of the manifest (character or weapon models, or levels with their
                  music) from that install into the folder, printing "PROGRESS <n>/<total>
                  <name>" before each and "OK <id>" after each success (the game's first-run
                  extractor reads these lines). --only exports just those entries.

              music <out folder> <MusicID>...
                  Exports Mass Effect 1's area music by MusicID (the names its music volumes
                  carry, e.g. Wards): <out folder>/<musicid>.ogg, as stored in the game's ISACT
                  bank, plus music.json with each track's loop, fade times, and volume.

              locate
                  Prints "FOUND <path>" for each Legendary Edition install found through Steam
                  or the EA app.

            Inspection (for developers reading a new level or asset):

              exports <package.pcc> [filter]
                  Lists exports (index, class, path, size) whose path or class contains [filter].

              props <package.pcc> <filter>
                  Prints the properties of exports whose path contains <filter> (or whose class
                  is <filter>, e.g. SkyLightComponent).

              texture <package.pcc> <filter> <out folder> [--dds]
                  Writes each Texture2D whose path contains <filter> to <out folder> as PNG,
                  as stored (for working out a tinted material's recipe). --dds writes the
                  format and mips ME1 stores instead, as the exports carry them.

              shaders <out folder> <package.pcc>... [--filter <material>] [--skinned] [--cache <package>]
                  Writes each base Material's base-pass pixel shaders, disassembled, with its
                  uniform slots and the instances using it: <out folder>/<Material>.txt.
                  --skinned dumps the skeletal-mesh (character) variants instead. --cache reads
                  the compiled shaders from another package (a level, or RefShaderCache-PC-D3D-SM5.upk).

              2da <package.pcc> <export path>
                  Prints a Bio2DA table as tab-separated rows.

              isb <bank.isb> [out folder|-] [filter]
                  Lists an ISACT bank's samples (by indx), and with a folder, extracts them.

              streambank <package.pcc> <export path> [--raw]
                  Prints a BioSoundNodeWaveStreamingData's ISACT index as a chunk tree.

              reflect <filter>
                  Prints the public members of LegendaryExplorerCore types matching <filter>.

            Package paths may be written ME1/<file>.pcc (or ME2, ME3), relative to the game root.
            """);
        return 1;
    }

    // "ME2/BioP_ProFre.pcc" -> <game root>\Game\ME2\BioGame\CookedPCConsole\BioP_ProFre.pcc.
    // Anything else is returned unchanged (a full path, or a name resolved by the caller).
    public static string ResolvePackage(string path)
    {
        var match = Regex.Match(path, @"^(ME[123])[/\\](.+)$", RegexOptions.IgnoreCase);
        return match.Success
            ? Path.Combine(GameRoot, "Game", match.Groups[1].Value.ToUpperInvariant(), "BioGame", "CookedPCConsole", match.Groups[2].Value)
            : path;
    }

    // The reverse: <game root>\Game\ME1\BioGame\CookedPCConsole\BIOA_STA60_02_DSG.pcc ->
    // "ME1/BIOA_STA60_02_DSG.pcc", so a manifest names a package on any install.
    public static string GameRelative(string path)
    {
        var match = Regex.Match(path ?? "", @"[/\\](ME[123])[/\\]BioGame[/\\]CookedPCConsole[/\\](.+)$", RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value.ToUpperInvariant()}/{match.Groups[2].Value}" : path;
    }

    private static string ResolveLexDir()
    {
        string env = Environment.GetEnvironmentVariable("LEX_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;
        // Shipped extractor: the LEX DLLs sit next to the executable.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "LegendaryExplorerCore.dll")))
            return AppContext.BaseDirectory;
        return typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LexDir")?.Value ?? "";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
