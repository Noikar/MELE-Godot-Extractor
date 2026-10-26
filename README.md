# MELE-Godot-Extractor

**LexExport** turns Mass Effect Legendary Edition game files into glTF (`.glb`) models, levels,
textures, and music that Godot loads directly. It reads the game's packages through
[Legendary Explorer](https://github.com/ME3Tweaks/LegendaryExplorer)'s `LegendaryExplorerCore`,
and it's the first-run asset extractor of the Mass Effect Zero fan project.

**No game files are included.** You need your own copy of Mass Effect Legendary Edition. The
tool reads it from your install, so a game built on it can do the same: each player extracts the
assets from their own copy, and nobody hosts or redistributes BioWare's or EA's work.

Not affiliated with or endorsed by EA, BioWare, or ME3Tweaks.

## What it exports

- **Characters and weapons** (`mesh`): skinned meshes on a merged skeleton (body, head, hair),
  animation clips, and materials. Cooking strips ME1's material graphs, so each material is baked
  from its compiled pixel shader: diffuse, emissive, normal, and specular maps that match how the
  game draws them, including tints and per-NPC overrides.
- **Levels** (`level`): the static world of a level's sublevels in one `.glb`: mesh instances,
  lights, start markers, ME1's lightmaps, particle emitters, and player collision (hulls and
  trimeshes under a `Collision` node).
- **Music** (`music`): a level's area music as `.ogg` from the game's ISACT banks, with
  `music.json` holding each track's loop points, fade times, and volume.
- **Manifests** (`manifest`): batch exports from a JSON list. This is what a game runs on first
  launch, with machine-readable progress lines.
- **Inspection tools** for working out a new level or asset: `exports`, `props`, `texture`,
  `shaders`, `particles`, `refs`, `2da`, `isb`, `streambank`, `reflect`, `find`.

Run the tool with no arguments for the full command reference. Package paths can be written
`ME1/<file>.pcc` (or `ME2`, `ME3`), relative to the game root.

## Building

You need:

- Windows, and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Mass Effect Legendary Edition (Steam or EA app)
- A [Legendary Explorer](https://github.com/ME3Tweaks/LegendaryExplorer/releases) release,
  extracted to a folder. The tool is built and tested against **6.4.1.6065**.

```
dotnet build -c Release -p:LexDir="C:\path\to\Legendary Explorer"
dotnet run -c Release -p:LexDir="C:\path\to\Legendary Explorer" -- locate
```

`LexDir` defaults to `%USERPROFILE%\Downloads\Legendary Explorer`. At runtime the tool loads
LEX's DLLs from there, from next to its own executable (a published build), or from the
`LEX_DIR` environment variable. The developer commands find the game through `ME_LE_ROOT`
(the folder holding `Game\ME1`), and `locate` prints the installs it finds through Steam and the
EA app.

### Publishing a self-contained build

```
dotnet publish -c Release -r win-x64 --self-contained true -p:LexDir="..." -o <out folder>
```

This produces a folder players can run without .NET or Legendary Explorer installed. It holds
LEX's runtime DLLs, the GPL license, this tool's source, `THIRD-PARTY-NOTICES.txt`, and a zip of
the Legendary Explorer source matching the shipped DLLs. `lex_source.ps1` fetches that zip from
the release tag named by `LegendaryExplorerCore.dll`'s file version (it needs `git` and
PowerShell 7), and it fails the publish if LEX has no such tag.

## Manifests

A manifest is a list of entries for `manifest` to export. `characters.json`, `weapons.json`,
and `levels.json` are Mass Effect Zero's own, kept as working examples:

- **Character and weapon entries** name a `package`, an `out` path, and the `args` passed to
  `mesh` (meshes, `--mat`, `--morph`, `--override`, `--anim <AnimSequence>=<Clip>[@other.pcc]`, ...).
- **Level entries** name their sublevel `packages` (wildcards allowed), the area `music`, start
  `markers`, and which `npcs` to bake.

```
LexExport manifest characters.json "<game root>" "<out folder>" [--only <id>,...] [--lanes <n>] [--background]
```

The tool prints `PROGRESS <n>/<total> <name>` as each entry starts, `OK <id>` after each success,
and `FAILED <name>: <reason>` after each failure. The exit code is 0 only when every entry
exported. `--background` runs at below-normal priority on half the cores, for extracting while a
game is being played.

## Using the output in Godot

The `.glb` files are standard glTF 2.0, Y-up, in meters. They use two glTF extensions that
Godot's importer skips. The loaders in [`godot/`](godot/) add them:

- **`DdsExtension.gd`** reads `MSFT_texture_dds`. Every texture is DDS (ME1's own block-compressed
  data where a texture is carried unchanged, or BC1, BC3, or BC5 for baked maps) with mipmaps, so
  textures reach the GPU as stored instead of as uncompressed RGBA. BC5 normal maps hold X and Y
  only, so Z must be rebuilt from them.
- **`SpecularExtension.gd`** reads `KHR_materials_specular`. Each material's baked specular map
  (color in RGB, Phong power in alpha as `log2(power) / 8`) and its color scale are stored on the
  imported material as metadata (`me_specular`, `me_specular_scale`) for your own shader to use.

Register both before loading a file:

```gdscript
DdsExtension.ensure_registered()
SpecularExtension.ensure_registered()

var document := GLTFDocument.new()
var state := GLTFState.new()
if document.append_from_file(path, state) == OK:
	var scene: Node = document.generate_scene(state)
```

The loaders need a Godot version with `Image.load_dds_from_buffer` (4.4 and later). They're
tested on 4.8.

## License

- **The extractor** (everything outside `godot/`) is GPLv3 ([LICENSE](LICENSE)), because it links
  Legendary Explorer, which is GPLv3. [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) lists
  every bundled component's license.
- **The Godot loaders** (`godot/`) are MIT ([godot/LICENSE](godot/LICENSE)).

A game that runs the extractor as a separate program, and only reads the files it writes, is not
bound by the GPL and can use any license, closed or open. The GPL's aggregation clause (GPLv3
§5) covers that case. The loaders are MIT so that they can be compiled into such a game. If you
ship the extractor with your game, you ship GPL software. Include its license and source, as a
published build does on its own.
