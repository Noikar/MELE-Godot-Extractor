using System.IO.Enumeration;
using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace LexExport;

using VERTEX = VertexBuilder<VertexPositionNormalTangent, VertexTexture2, VertexEmpty>;
using VERTEX_LIT = VertexBuilder<VertexPositionNormalTangent, VertexColor1Texture4, VertexEmpty>;
using COLLISION_VERTEX = VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>;

// Exports the static world of one or more level packages (usually a level's streamed
// sublevels) into a single .glb:
//
//   <package>/<actor>    every static mesh instance, sharing one glTF mesh per StaticMesh
//   Lights/<light>       point, spot, and directional lights (KHR_lights_punctual), with the
//                        original Unreal values in each node's extras
//   Markers/<tag>        player start locations, named by their Tag (e.g. start_sta60_01)
//   LightmapPages/Page_<n>_<map>  the original game's texture lightmaps (see AddRenderInstance)
//   Particles/<actor>    particle emitters, each with its system's description in its extras
//                        ("me_particles", see ParticleExport)
//   ParticleAssets/<asset>  what the emitters draw: a particle mesh, or a unit quad carrying a
//                        sprite material ("particle_asset": its key, in the node's extras)
//   Placeables/<package>_<actor>  skeletal placeables (BioDoors, consoles), each an empty node
//                        whose extras ("placeable") name its asset and carry its state machine
//   Decals/<package>_<actor>_<n>  static decals: one mesh per receiver, the receiver geometry
//                        the game cooked for it, lit by the receiver's lightmap (see AddDecal)
//   Pawns/<package>_<actor>  ambient NPCs (--npcs), each an empty node naming its model and clip
//                        (see LevelPawns.cs; the models are PawnAsset_<n> scene roots)
//   Vfx/<package>_<actor>_vfx<n>  placeables' mesh effects (holograms), each a group of mesh
//                        instances shown in some of its placeable's states (see AddPlaceableEffects)
//
// Beside the Level root, each skeletal placeable model is a scene root of its own,
// PlaceableAsset_<mesh> ("placeable_asset": its name in the extras): its skeleton, skinned
// mesh, and every clip of its anim sets, which the game clones per placeable. The sounds its
// clips trigger ("placeable_sounds") are written beside the .glb (or to --sounds) as .ogg files. They can't sit
// under Level: SharpGLTF 1.0.3 requires every node name in a skin's tree to be unique, and
// actor and bone names repeat across sublevels and models.
//   Collision/Hull_<n>   one convex hull each, in world space: a mesh's simplified collision
//                        or a blocking volume
//   Collision/Trimesh_<package>  per-triangle collision, in world space, of the meshes that
//                        block the player with their full geometry
//
// The game's level loader turns the Collision meshes into physics shapes and drops them.
// Which collision a mesh uses follows Unreal 3's rule for actor movement: the simplified
// hulls when the StaticMesh sets UseSimpleBoxCollision and has any, else its triangles.
//
// Each mesh instance also carries the original game's precomputed lighting, which the
// loader can draw instead of lighting the level itself (the game's original-lighting mode).
public static class LevelCommand
{
    // args: package paths (wildcards allowed in the file name, e.g. ME1/BIOA_STA60_??_LAY.pcc),
    // "--max-texture <px>" (largest texture side to embed, default 1024; 0 = full size), and
    // "--markers <package> <tag pattern>" (start locations from another package, usually the
    // persistent level that holds a whole location's, e.g. ME1/BIOA_STA00.pcc start_sta60_*),
    // "--sounds <dir>" (where placeables' and ambient sounds go; default: the .glb's folder),
    // "--npcs <tag patterns>" (comma-separated BioPawn Tags to export as NPCs, e.g. *_amb_*,Keeper),
    // and "--music-volumes <package> <MusicIDs>" (the BioMusicVolumes of another package, usually
    // the persistent level, whose MusicID is one of the comma-separated IDs; see LevelAudio.cs),
    // and "--stream-volumes <package>" (the BioTriggerStreams of another package, usually the
    // persistent level, that show the level's chunks; see LevelAudio.cs).
    public static int Run(string outPath, string[] args)
    {
        LevelBuilder.DroppedSections.Clear();
        var patterns = new List<string>();
        var markerSources = new List<(string Package, string Tags)>();
        var musicSources = new List<(string Package, string[] Ids)>();
        var streamSources = new List<string>();
        string npcs = null;
        int maxTexture = 1024;
        string soundsDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--max-texture" && i + 1 < args.Length)
                maxTexture = int.Parse(args[++i]);
            else if (args[i] == "--sounds" && i + 1 < args.Length)
                soundsDir = args[++i];
            else if (args[i] == "--markers" && i + 2 < args.Length)
                markerSources.Add((args[++i], args[++i]));
            else if (args[i] == "--music-volumes" && i + 2 < args.Length)
                musicSources.Add((args[++i], args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            else if (args[i] == "--stream-volumes" && i + 1 < args.Length)
                streamSources.Add(args[++i]);
            else if (args[i] == "--npcs" && i + 1 < args.Length)
                npcs = args[++i];
            else
                patterns.Add(args[i]);
        }

        string[] files = patterns.SelectMany(ExpandPackages).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine("No packages matched.");
            return 1;
        }

        var builder = new LevelBuilder(maxTexture, soundsDir);
        if (npcs != null)
            builder.AddPawnPatterns(npcs);
        for (int f = 0; f < files.Length; f++)
        {
            Console.WriteLine($"Reading {f + 1}/{files.Length} {Path.GetFileNameWithoutExtension(files[f])}");
            using IMEPackage pcc = MEPackageHandler.OpenMEPackage(files[f]);
            builder.AddPackage(pcc);
            ShaderAnalysis.Forget(pcc);
        }
        foreach ((string package, string tags) in markerSources)
        {
            using IMEPackage pcc = MEPackageHandler.OpenMEPackage(ExpandPackages(package).First());
            builder.AddMarkers(pcc, tags);
        }
        foreach ((string package, string[] ids) in musicSources)
        {
            using IMEPackage pcc = MEPackageHandler.OpenMEPackage(ExpandPackages(package).First());
            builder.AddMusicVolumes(pcc, ids);
        }
        var chunks = files.Select(f => LevelBuilder.Chunk(Path.GetFileNameWithoutExtension(f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string package in streamSources)
        {
            using IMEPackage pcc = MEPackageHandler.OpenMEPackage(ExpandPackages(package).First());
            builder.AddStreamVolumes(pcc, chunks);
        }
        builder.Write(outPath);
        MaterialBaker.ForgetDecoded();
        return 0;
    }

    // "ME1/BIOA_STA60_??_LAY.pcc" -> every matching package; localization packages are skipped.
    internal static IEnumerable<string> ExpandPackages(string pattern)
    {
        string path = Program.ResolvePackage(pattern);
        string name = Path.GetFileName(path);
        if (!name.Contains('*') && !name.Contains('?'))
            return File.Exists(path) ? [path] : throw new FileNotFoundException($"No package {path}.");
        return Directory.GetFiles(Path.GetDirectoryName(path)!, name)
            .Where(f => !Path.GetFileName(f).Contains("_LOC_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }
}

// Accumulates every package's actors into one scene.
public partial class LevelBuilder
{
    // A StaticMesh's LOD 0, read once and shared by every instance: its geometry in glTF
    // space and its collision in mesh-local Unreal space.
    private sealed class MeshGeometry
    {
        public string Name;
        public StaticMeshRenderData Lod;
        public Vector3[] GltfPositions;
        public Vector3[] Normals;
        public Vector4[] Tangents;               // glTF tangents (w: bitangent sign), Unreal's own tangent frame
        public Vector2[][] UVs;                  // [channel][vertex]
        public int LightmapChannel;              // the UV channel of its lightmap layout, 0 if none
        public bool LightmapInRange;             // it has one, and that layout stays within 0-1
        public bool FlipWinding;
        public MaterialBuilder[] SectionMaterials; // per LOD element, from the mesh's own package; null = not drawn

        // The same geometry drawn with other section materials (particle meshes keep additive ones).
        public MeshGeometry WithMaterials(MaterialBuilder[] materials)
        {
            var copy = (MeshGeometry)MemberwiseClone();
            copy.SectionMaterials = materials;
            return copy;
        }
        public List<Vector3[]> Hulls;            // simplified collision, when the mesh uses it
        public Vector3[] Positions;              // for per-triangle collision
        public List<(int A, int B, int C)> CollisionTriangles;
    }

    private readonly int _maxTexture;
    private readonly SceneBuilder _scene = new("Level");
    private readonly NodeBuilder _root = new("Level");
    private readonly NodeBuilder _lights;
    private readonly NodeBuilder _markers;
    private readonly NodeBuilder _collision;
    private readonly NodeBuilder _lightmapPagesNode;
    private readonly NodeBuilder _particles;
    private readonly NodeBuilder _particleAssets;
    private readonly NodeBuilder _placeablesNode;
    private readonly NodeBuilder _decalsNode;
    private readonly NodeBuilder _effectsNode;
    private readonly MaterialBuilder _collisionMaterial = new MaterialBuilder("Collision").WithUnlitShader();

    // Shared across packages: sublevels carry duplicate copies of the same meshes and textures.
    private readonly Dictionary<string, MeshGeometry> _geometry = new(StringComparer.OrdinalIgnoreCase);
    // Render meshes per StaticMesh, second UV set, and material overrides (null: nothing drawn).
    private readonly Dictionary<string, IMeshBuilder<MaterialBuilder>> _renders = new(StringComparer.OrdinalIgnoreCase);
    // Lightmap atlas pages by package and textures, or -1 when a page couldn't be decoded.
    private readonly Dictionary<string, int> _lightmapPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialBuilder> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemoryImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    // Textures the translated material shaders sample, by key (see TextureCache.CarriedTextures).
    private readonly Dictionary<string, MemoryImage?> _carriedTextures = new(StringComparer.OrdinalIgnoreCase);
    // Particle systems' emitter descriptions, and the assets they draw, by path.
    private readonly Dictionary<string, JsonArray> _particleSystems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _particleAssetKeys = new(StringComparer.OrdinalIgnoreCase);
    // Skeletal placeable models by SkeletalMesh path -> asset name.
    private readonly Dictionary<string, string> _skeletalAssets = new(StringComparer.OrdinalIgnoreCase);
    // Exported sound effects by SoundCue path (null when it couldn't be exported).
    private readonly Dictionary<string, JsonObject> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _soundsDir;
    // The current package's drawn mesh components (by export index): where each sits and its
    // original lighting, for the decals projected onto them.
    private readonly Dictionary<int, (Matrix4x4 World, JsonObject Lighting)> _receivers = new();

    private int _instances, _lightCount, _hullCount, _trimeshTriangles;
    private int _pageCount, _textureLit, _vertexLit, _placeables, _emitters, _markerCount, _skeletalPlaceables;
    private int _decals, _decalMeshes, _decalsUnlit, _effects;
    private readonly Dictionary<string, int> _skipped = new();

    // An effects export (PowerVfx): only ParticleAssets (and MaterialTextures, at the end), so
    // the game's level import never mistakes it for a level, which has Lights and Collision.
    private LevelBuilder(int maxTexture)
    {
        _maxTexture = maxTexture;
        _particleAssets = _root.CreateNode("ParticleAssets");
        _scene.AddNode(_root);
    }

    public LevelBuilder(int maxTexture, string soundsDir)
    {
        _maxTexture = maxTexture;
        _soundsDir = soundsDir;
        _lights = _root.CreateNode("Lights");
        _markers = _root.CreateNode("Markers");
        _collision = _root.CreateNode("Collision");
        _lightmapPagesNode = _root.CreateNode("LightmapPages");
        _particles = _root.CreateNode("Particles");
        _particleAssets = _root.CreateNode("ParticleAssets");
        _placeablesNode = _root.CreateNode("Placeables");
        _decalsNode = _root.CreateNode("Decals");
        _effectsNode = _root.CreateNode("Vfx");
        _scene.AddNode(_root);
    }

    public void AddPackage(IMEPackage pcc)
    {
        string packageName = Path.GetFileNameWithoutExtension(pcc.FilePath);
        ExportEntry levelExport = pcc.Exports.FirstOrDefault(e => e.ClassName == "Level");
        if (levelExport == null)
        {
            Console.WriteLine($"{packageName}: no level, skipped.");
            return;
        }

        var textures = new TextureCache(pcc, new List<string>(), _materials, _images)
        {
            MaxTextureSize = _maxTexture,
            GuessDiffuse = true,
            SurfaceShading = true,
            CarriedTextures = _carriedTextures,
        };
        NodeBuilder packageNode = _root.CreateNode(packageName);
        var trimesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>($"Trimesh_{packageName}");
        var trimeshPrimitive = trimesh.UsePrimitive(_collisionMaterial);
        int instancesBefore = _instances, lightsBefore = _lightCount, hullsBefore = _hullCount, trisBefore = _trimeshTriangles;
        _receivers.Clear();
        // Decals go last: they draw on their receivers, which may come later in the actor list.
        var decals = new List<(ExportEntry Actor, ExportEntry Component)>();

        var level = ObjectBinary.From<Level>(levelExport);
        foreach (int actorIndex in level.Actors)
        {
            if (actorIndex == 0 || !pcc.TryGetUExport(actorIndex, out ExportEntry actor))
                continue;
            try
            {
                if (actor.GetProperties().GetProp<ObjectProperty>("Decal") is ObjectProperty decalProp
                    && pcc.TryGetUExport(decalProp.Value, out ExportEntry decalComponent)
                    && decalComponent.ClassName == "DecalComponent")
                    decals.Add((actor, decalComponent));
                else
                    AddActor(pcc, actor, packageNode, textures, trimeshPrimitive);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {actor.ObjectName.Instanced} ({actor.ClassName}) failed: {(Environment.GetEnvironmentVariable("LEXEXPORT_TRACE") == "1" ? ex.ToString() : ex.Message)}");
            }
        }
        foreach ((ExportEntry actor, ExportEntry component) in decals)
        {
            try
            {
                AddDecal(pcc, packageName, actor, component, textures);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {actor.ObjectName.Instanced} (decal) failed: {(Environment.GetEnvironmentVariable("LEXEXPORT_TRACE") == "1" ? ex.ToString() : ex.Message)}");
            }
        }

        if (_trimeshTriangles > trisBefore)
            _scene.AddRigidMesh(trimesh, _collision.CreateNode(trimesh.Name));

        Console.WriteLine($"{packageName}: {_instances - instancesBefore} mesh instances, {_lightCount - lightsBefore} lights, "
            + $"{_hullCount - hullsBefore} hulls, {_trimeshTriangles - trisBefore} collision triangles.");
    }

    private void AddActor(IMEPackage pcc, ExportEntry actor, NodeBuilder packageNode, TextureCache textures,
        PrimitiveBuilder<MaterialBuilder, VertexPosition, VertexEmpty, VertexEmpty> trimesh)
    {
        PropertyCollection props = actor.GetProperties();
        string cls = actor.ClassName;

        if (cls == "StaticMeshCollectionActor")
        {
            var collection = ObjectBinary.From<StaticMeshCollectionActor>(actor);
            for (int i = 0; i < collection.Components.Count; i++)
            {
                if (pcc.TryGetUExport(collection.Components[i], out ExportEntry component))
                    AddMeshInstance(pcc, actor, null, component, collection.LocalToWorldTransforms[i], packageNode, textures, trimesh);
            }
            return;
        }
        if (cls == "StaticLightCollectionActor")
        {
            var collection = ObjectBinary.From<StaticLightCollectionActor>(actor);
            for (int i = 0; i < collection.Components.Count; i++)
            {
                if (pcc.TryGetUExport(collection.Components[i], out ExportEntry component))
                    AddLight(component, collection.LocalToWorldTransforms[i]);
            }
            return;
        }

        Matrix4x4 actorToWorld = UnrealTransform.ActorToWorld(props);

        if (props.GetProp<ObjectProperty>("StaticMeshComponent") is ObjectProperty meshProp
            && pcc.TryGetUExport(meshProp.Value, out ExportEntry meshComponent))
        {
            Matrix4x4 world = UnrealTransform.ComponentToActor(meshComponent.GetProperties()) * actorToWorld;
            AddMeshInstance(pcc, actor, props, meshComponent, world, packageNode, textures, trimesh);
        }
        else if (props.GetProp<ObjectProperty>("PlaceableMesh") is ObjectProperty placeableProp
            && pcc.TryGetUExport(placeableProp.Value, out ExportEntry placeableComponent)
            && placeableComponent.ClassName == "StaticMeshComponent")
        {
            // BioWare's placeables (BioInert props, BioUseable terminals and lockers) draw their
            // intact state from PlaceableMesh; DestroyedMesh only shows once they're destroyed.
            Matrix4x4 world = UnrealTransform.ComponentToActor(placeableComponent.GetProperties()) * actorToWorld;
            AddMeshInstance(pcc, actor, props, placeableComponent, world, packageNode, textures, trimesh);
            _placeables++;
            AddPlaceableEffects(pcc, actor, props, actorToWorld, textures);
        }
        else if (props.GetProp<ObjectProperty>("PlaceableMesh") is ObjectProperty skeletalProp
            && pcc.TryGetUExport(skeletalProp.Value, out ExportEntry skeletalComponent)
            && skeletalComponent.ClassName == "SkeletalMeshComponent")
        {
            AddSkeletalPlaceable(pcc, actor, props, skeletalComponent, actorToWorld, textures);
            AddPlaceableEffects(pcc, actor, props, actorToWorld, textures);
        }
        else if (props.GetProp<ObjectProperty>("LightComponent") is ObjectProperty lightProp
            && pcc.TryGetUExport(lightProp.Value, out ExportEntry lightComponent))
        {
            AddLight(lightComponent, UnrealTransform.WithoutScale(actorToWorld));
        }
        else if (props.GetProp<ObjectProperty>("ParticleSystemComponent") is ObjectProperty particleProp
            && pcc.TryGetUExport(particleProp.Value, out ExportEntry particleComponent))
        {
            AddEmitter(pcc, actor, particleComponent, actorToWorld, textures);
        }
        else if (cls.Contains("BlockingVolume"))
        {
            AddBlockingVolume(pcc, actor, props, actorToWorld);
        }
        else if (IsAmbientSound(actor, props))
        {
            AddAmbientSound(pcc, actor, props, actorToWorld);
        }
        else if (IsStartActor(cls))
        {
            AddMarker(actor, props, actorToWorld);
        }
        else if (cls == "BioPawn" && TryAddPawn(pcc, actor))
        {
        }
        else
        {
            _skipped[cls] = _skipped.GetValueOrDefault(cls) + 1;
        }
    }

    private static bool IsStartActor(string cls) => cls is "PlayerStart" or "BioStartLocation" or "BioStartPoint";

    // Only the start locations of a package whose Tag matches the pattern (wildcards allowed).
    // A location's starts usually live in its persistent level (BIOA_STA00 for the Citadel),
    // tagged by the sublevel they belong to (start_sta60_01 is in the Wards).
    public void AddMarkers(IMEPackage pcc, string tagPattern)
    {
        ExportEntry levelExport = pcc.Exports.FirstOrDefault(e => e.ClassName == "Level");
        if (levelExport == null)
            return;
        int before = _markerCount;
        foreach (int actorIndex in ObjectBinary.From<Level>(levelExport).Actors)
        {
            if (actorIndex == 0 || !pcc.TryGetUExport(actorIndex, out ExportEntry actor) || !IsStartActor(actor.ClassName))
                continue;
            PropertyCollection props = actor.GetProperties();
            if (FileSystemName.MatchesSimpleExpression(tagPattern, StartTag(actor, props), ignoreCase: true))
                AddMarker(actor, props, UnrealTransform.ActorToWorld(props));
        }
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(pcc.FilePath)}: {_markerCount - before} start markers matching {tagPattern}.");
    }

    // Start actors are found by Tag (what ME1's level transitions name), else by object name.
    private static string StartTag(ExportEntry actor, PropertyCollection props)
    {
        string tag = props.GetProp<NameProperty>("Tag")?.Value.Instanced;
        return string.IsNullOrEmpty(tag) || tag == "None" ? actor.ObjectName.Instanced : tag;
    }

    private void AddMarker(ExportEntry actor, PropertyCollection props, Matrix4x4 actorToWorld)
    {
        NodeBuilder marker = _markers.CreateNode(StartTag(actor, props));
        marker.LocalMatrix = UnrealTransform.ToGltf(UnrealTransform.WithoutScale(actorToWorld));
        _markerCount++;
    }

    // A BioWare placeable drawn by a SkeletalMeshComponent (doors, elevator consoles, bank
    // machines): an empty node at the component's transform, whose extras name its model (a
    // PlaceableAsset_<mesh> scene root) and carry what the game needs to run it (see PlaceableExtras).
    private void AddSkeletalPlaceable(IMEPackage pcc, ExportEntry actor, PropertyCollection props, ExportEntry component,
        Matrix4x4 actorToWorld, TextureCache levelTextures)
    {
        PropertyCollection componentProps = component.GetProperties();
        if (componentProps.GetProp<ObjectProperty>("SkeletalMesh") is not ObjectProperty meshRef
            || !pcc.TryGetUExport(meshRef.Value, out ExportEntry meshExport))
        {
            _skipped[actor.ClassName + " (no mesh)"] = _skipped.GetValueOrDefault(actor.ClassName + " (no mesh)") + 1;
            return;
        }

        JsonObject placeable = PlaceableExtras(pcc, actor, props);
        placeable["asset"] = SkeletalAsset(pcc, meshExport, componentProps, levelTextures);
        // Whether this one blocks at all; what blocks is its model's physics bodies.
        placeable["blocks"] = BlocksPlayer(props, componentProps);
        // Actor names repeat across sublevels (BioDoor_21 is in two), and the game builds
        // networked nodes from these names, so they must be unique.
        string packageName = Path.GetFileNameWithoutExtension(pcc.FilePath);
        // Which lights reach it (a light's "ue_levels").
        placeable["package"] = packageName;
        var node = new NodeBuilder($"{packageName}_{actor.ObjectName.Instanced}", new JsonObject { ["placeable"] = placeable });
        _placeablesNode.AddNode(node);
        node.LocalMatrix = UnrealTransform.ToGltf(UnrealTransform.ComponentToActor(componentProps) * actorToWorld);
        _skeletalPlaceables++;
    }

    // A placeable's visual effects that are made of meshes: its appearance (actor type's
    // m_oApperance) lists effects per state (VisualEffects: BVFX_SPAWN shows one on entering
    // a state, BVFX_DEATH removes it), and an effect whose BioVFXTemplate has a prefab spawns
    // that prefab's actors at the placeable (the Rapid Transit kiosk's Cab_Holo, a Keeper
    // console's holo panels). ME1 never places these in the level. Each effect becomes a
    // Vfx/<package>_<actor>_vfx<n> group (its name starts with the package, which is what lights'
    // level restrictions match) holding the prefab's meshes in world space, whose extras
    // ("placeable_vfx") say which placeable runs it and in which states it shows. Prefab
    // emitters and material-curve effects (the status-light flashes) are left out.
    private void AddPlaceableEffects(IMEPackage pcc, ExportEntry actor, PropertyCollection props, Matrix4x4 actorToWorld,
        TextureCache textures)
    {
        PropertyCollection Props(int index) => pcc.TryGetUExport(index, out ExportEntry e) ? e.GetProperties() : null;

        pcc.TryGetUExport(props.GetProp<ObjectProperty>("m_oBehavior")?.Value ?? 0, out ExportEntry behavior);
        PropertyCollection type = Props(behavior == null ? 0 : InheritedProp<ObjectProperty>(behavior, "m_oActorType")?.Value ?? 0);
        PropertyCollection appearance = Props(type?.GetProp<ObjectProperty>("m_oApperance")?.Value ?? 0);
        var effects = appearance?.GetProp<ArrayProperty<StructProperty>>("VisualEffects");
        if (effects == null)
            return;

        string packageName = Path.GetFileNameWithoutExtension(pcc.FilePath);
        string placeableName = $"{packageName}_{actor.ObjectName.Instanced}";
        string starting = InheritedProp<NameProperty>(behavior, "StartingState")?.Value.Instanced;
        if (string.IsNullOrEmpty(starting) || starting == "None")
            starting = type.GetProp<NameProperty>("DefaultStartingState")?.Value.Instanced;
        // The effect follows the actor, not its scale.
        Matrix4x4 placeableToWorld = UnrealTransform.WithoutScale(actorToWorld);

        for (int i = 0; i < effects.Count; i++)
        {
            PropertyCollection template = Props(effects[i].Properties.GetProp<ObjectProperty>("m_oEffectTemplate")?.Value ?? 0);
            if (template?.GetProp<ObjectProperty>("oPrefab") is not ObjectProperty prefabRef
                || !pcc.TryGetUExport(prefabRef.Value, out ExportEntry prefab))
                continue;

            var spawn = new JsonArray();
            var death = new JsonArray();
            foreach (StructProperty state in effects[i].Properties.GetProp<ArrayProperty<StructProperty>>("m_States") ?? Enumerable.Empty<StructProperty>())
            {
                string name = state.Properties.GetProp<NameProperty>("m_nmAPLState")?.Value.Instanced;
                string when = state.Properties.GetProp<EnumProperty>("m_VFXState")?.Value.Instanced
                    ?? state.Properties.GetProp<ByteProperty>("m_VFXState")?.Value.ToString();
                if (when == "BVFX_SPAWN")
                    spawn.Add(name);
                else if (when == "BVFX_DEATH")
                    death.Add(name);
            }
            if (spawn.Count == 0)
                continue;

            var group = new NodeBuilder($"{placeableName}_vfx{i}", new JsonObject
            {
                ["placeable_vfx"] = new JsonObject
                {
                    ["placeable"] = placeableName,
                    ["effect"] = prefab.ObjectName.Instanced,
                    ["starting_state"] = starting,
                    ["spawn"] = spawn,
                    ["death"] = death,
                },
            });
            int meshes = 0;
            var leftOut = new List<string>();
            foreach (ObjectProperty memberRef in prefab.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("PrefabArchetypes") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (!pcc.TryGetUExport(memberRef.Value, out ExportEntry member))
                    continue;
                PropertyCollection memberProps = member.GetProperties();
                if (memberProps.GetProp<ObjectProperty>("StaticMeshComponent") is not ObjectProperty componentRef
                    || !pcc.TryGetUExport(componentRef.Value, out ExportEntry component))
                {
                    leftOut.Add(member.ClassName);
                    continue;
                }
                PropertyCollection componentProps = component.GetProperties();
                if (componentProps.GetProp<ObjectProperty>("StaticMesh") is not ObjectProperty meshRef
                    || !pcc.TryGetUExport(meshRef.Value, out ExportEntry meshExport))
                    continue;
                if (!_geometry.TryGetValue(meshExport.InstancedFullPath, out MeshGeometry geometry))
                    _geometry[meshExport.InstancedFullPath] = geometry = ReadGeometry(pcc, meshExport, textures);
                // Prefab members sit relative to the prefab's origin, here the placeable.
                Matrix4x4 world = UnrealTransform.ComponentToActor(componentProps) * UnrealTransform.ActorToWorld(memberProps) * placeableToWorld;
                AddRenderInstance(pcc, member, component, componentProps, meshExport, geometry, world, group, textures,
                    $"{group.Name}_{meshes}");
                meshes++;
            }
            if (meshes == 0)
                continue;
            _effectsNode.AddNode(group);
            _effects++;
            string note = leftOut.Count > 0 ? $"; left out {string.Join(", ", leftOut)}" : "";
            Console.WriteLine($"  effect {prefab.ObjectName.Instanced} on {placeableName}: {meshes} mesh(es), shown in {string.Join("/", spawn)}{note}");
        }
    }

    // A property of an export, or of the archetype it was made from when it doesn't override it:
    // actors placed from a prefab (a Keeper's console) keep most of theirs on the prefab's
    // archetypes, e.g. a placeable behavior's m_oActorType.
    private static T InheritedProp<T>(ExportEntry export, string name) where T : Property
    {
        for (ExportEntry e = export; e != null; e = e.Archetype as ExportEntry)
        {
            if (e.GetProperties().GetProp<T>(name) is T found)
                return found;
        }
        return null;
    }

    // A skeletal placeable's model, exported once per SkeletalMesh: its skeleton under a
    // PlaceableAsset_<mesh> scene root, the skinned mesh (materials baked from their compiled shaders, as
    // characters' are, except unlit ones such as a door's glass, which are the level's own), and every clip in the component's anim sets under its SequenceName
    // (state_closed, trans_closed_opened, ...). Placeables move whole bones (a door's panels
    // slide), so every bone keeps its translation keys. The sounds each clip's AnimNotify_Sounds
    // play go in the root's extras as "placeable_sounds": clip -> [{ time, file, volume,
    // min_radius, max_radius }] (see SoundExport).
    private string SkeletalAsset(IMEPackage pcc, ExportEntry meshExport, PropertyCollection componentProps, TextureCache levelTextures)
    {
        if (_skeletalAssets.TryGetValue(meshExport.InstancedFullPath, out string existing))
            return existing;

        string name = meshExport.ObjectName.Instanced;
        if (_skeletalAssets.ContainsValue(name))
            name += "_" + _skeletalAssets.Count;
        _skeletalAssets[meshExport.InstancedFullPath] = name;

        var mesh = ObjectBinary.From<SkeletalMesh>(meshExport);
        var skeleton = new MergedSkeleton();
        skeleton.Add(mesh.RefSkeleton);
        NodeBuilder[] joints = skeleton.BuildNodes(out NodeBuilder armature);
        // Filled in below; the extras are read when the scene is written.
        var sounds = new JsonObject();
        var assetExtras = new JsonObject { ["placeable_asset"] = name };
        var assetNode = new NodeBuilder($"PlaceableAsset_{name}", assetExtras);
        assetNode.AddNode(armature);
        _scene.AddNode(assetNode);

        var clips = new List<string>();
        foreach (ObjectProperty setRef in componentProps.GetProp<ArrayProperty<ObjectProperty>>("AnimSets") ?? Enumerable.Empty<ObjectProperty>())
        {
            if (!pcc.TryGetUExport(setRef.Value, out ExportEntry animSet))
                continue;
            foreach (ObjectProperty sequenceRef in animSet.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("Sequences") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (!pcc.TryGetUExport(sequenceRef.Value, out ExportEntry sequence))
                    continue;
                string clip = sequence.GetProperties().GetProp<NameProperty>("SequenceName")?.Value.Instanced ?? sequence.ObjectName.Instanced;
                AnimationBaker.Bake(pcc, sequence.InstancedFullPath, clip, skeleton, joints, inPlace: false);
                clips.Add(clip);
                if (ClipSounds(pcc, sequence) is JsonArray clipSounds)
                    sounds[clip] = clipSounds;
            }
        }
        if (sounds.Count > 0)
            assetExtras["placeable_sounds"] = sounds;
        JsonArray collision = PhysicsBodies(pcc, componentProps, skeleton, out string collisionLog);
        if (collision.Count > 0)
            assetExtras["placeable_collision"] = collision;

        var textures = new TextureCache(pcc, new List<string>()) { BakeShaders = true, MaxTextureSize = _maxTexture, UnlitSource = levelTextures };
        _scene.AddSkinnedMesh(MeshCommand.BuildMesh(name, mesh, skeleton, textures), Matrix4x4.Identity, joints);
        Console.WriteLine($"  placeable model {name}: {skeleton.Bones.Count} bones, clips {string.Join(", ", clips)}; collision {collisionLog}.");
        return name;
    }

    // A skeletal placeable's collision: its PhysicsAsset's bodies that block the player (UE3's
    // extent checks against a skeletal mesh skip bodies with bBlockNonZeroExtent off, whatever
    // bNoCollision says; that one is only for rigid-body physics). Each body is
    // { bone, hulls: [[x, y, z, ...], ...] }, its points in the bone's frame in glTF space, so
    // the game can attach it to the bone and have it follow the clips (a door's sliding panels).
    private static JsonArray PhysicsBodies(IMEPackage pcc, PropertyCollection componentProps, MergedSkeleton skeleton, out string log)
    {
        var bodies = new JsonArray();
        if (componentProps.GetProp<ObjectProperty>("PhysicsAsset") is not ObjectProperty assetRef
            || !pcc.TryGetUExport(assetRef.Value, out ExportEntry physicsAsset))
        {
            log = "none (no physics asset)";
            return bodies;
        }

        int total = 0, passThrough = 0, hullCount = 0;
        foreach (ObjectProperty bodyRef in physicsAsset.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("BodySetup") ?? Enumerable.Empty<ObjectProperty>())
        {
            if (!pcc.TryGetUExport(bodyRef.Value, out ExportEntry body))
                continue;
            total++;
            PropertyCollection bodyProps = body.GetProperties();
            string bone = bodyProps.GetProp<NameProperty>("BoneName")?.Value.Instanced;
            if (bodyProps.GetProp<BoolProperty>("bBlockNonZeroExtent")?.Value == false || bone == null || !skeleton.Contains(bone))
            {
                passThrough++;
                continue;
            }
            if (bodyProps.GetProp<StructProperty>("AggGeom") is not StructProperty agg)
                continue;
            var hulls = new JsonArray();
            foreach (Vector3[] hull in ReadAggGeom(agg))
            {
                var points = new JsonArray();
                foreach (Vector3 point in hull)
                {
                    Vector3 p = UnrealSpace.Position(point);
                    points.Add(p.X);
                    points.Add(p.Y);
                    points.Add(p.Z);
                }
                hulls.Add(points);
            }
            if (hulls.Count == 0)
                continue;
            hullCount += hulls.Count;
            bodies.Add(new JsonObject { ["bone"] = bone, ["hulls"] = hulls });
        }
        log = $"{bodies.Count} of {total} bodies ({hullCount} hulls, {passThrough} pass-through) from {physicsAsset.ObjectName.Instanced}";
        return bodies;
    }

    // What the game needs to run a placeable, from its BioArtPlaceableBehavior and its actor
    // type: its class, starting state, the state pair a use toggles between (m_State0/1; a door
    // whose pair is Closed/Closed is locked shut), its use case, and its state machine (BioFSM:
    // each state's looping clip and the clip that plays on the way to each next state).
    private static JsonObject PlaceableExtras(IMEPackage pcc, ExportEntry actor, PropertyCollection props)
    {
        static string NameOf(PropertyCollection p, string name)
        {
            string value = p?.GetProp<NameProperty>(name)?.Value.Instanced;
            return string.IsNullOrEmpty(value) || value == "None" ? null : value;
        }
        static string EnumOf(PropertyCollection p, string name)
        {
            string value = p?.GetProp<EnumProperty>(name)?.Value.Instanced;
            return string.IsNullOrEmpty(value) || value == "None" ? null : value;
        }
        PropertyCollection Props(ObjectProperty reference) =>
            reference != null && pcc.TryGetUExport(reference.Value, out ExportEntry e) ? e.GetProperties() : null;

        var placeable = new JsonObject { ["class"] = actor.ClassName };
        PropertyCollection behavior = Props(props.GetProp<ObjectProperty>("m_oBehavior"));
        PropertyCollection type = pcc.TryGetUExport(props.GetProp<ObjectProperty>("m_oBehavior")?.Value ?? 0, out ExportEntry behaviorExport)
            ? Props(InheritedProp<ObjectProperty>(behaviorExport, "m_oActorType")) : null;
        string starting = NameOf(behavior, "StartingState") ?? NameOf(type, "DefaultStartingState");
        string state0 = NameOf(behavior, "m_State0"), state1 = NameOf(behavior, "m_State1");
        string useCase = EnumOf(behavior, "m_eUseCase") ?? EnumOf(type, "m_eUseCase");
        if (starting != null)
            placeable["starting_state"] = starting;
        if (state0 != null || state1 != null)
            placeable["use_states"] = new JsonArray(state0, state1);
        if (useCase != null)
            placeable["use_case"] = useCase;
        placeable["locked"] = state0 != null && state0 == state1;

        PropertyCollection fsm = Props(Props(props.GetProp<ObjectProperty>("FSM"))?.GetProp<ObjectProperty>("FSMData"));
        var states = new JsonObject();
        foreach (ObjectProperty stateRef in fsm?.GetProp<ArrayProperty<ObjectProperty>>("PlaceableStates") ?? Enumerable.Empty<ObjectProperty>())
        {
            PropertyCollection state = Props(stateRef);
            string stateName = NameOf(state, "StateName");
            if (stateName == null)
                continue;
            var transitions = new JsonObject();
            foreach (ObjectProperty transitionRef in state.GetProp<ArrayProperty<ObjectProperty>>("StateTransitions") ?? Enumerable.Empty<ObjectProperty>())
            {
                PropertyCollection transition = Props(transitionRef);
                if (NameOf(transition, "TransitionToState") is string to)
                    transitions[to] = NameOf(transition, "AnimationName");
            }
            states[stateName] = new JsonObject { ["anim"] = NameOf(state, "AnimationName"), ["transitions"] = transitions };
        }
        if (states.Count > 0)
            placeable["states"] = states;
        return placeable;
    }

    // The sounds an AnimSequence's AnimNotify_Sounds play, with their times, or null for none.
    private JsonArray ClipSounds(IMEPackage pcc, ExportEntry sequence) =>
        SoundExport.ClipSounds(pcc, sequence, _soundsDir, _sounds);

    // A particle emitter actor: its system's supported emitters (ParticleExport) in the node's
    // extras, and what they draw under ParticleAssets. Components that don't start active
    // (effects that Kismet turns on) are left out.
    private void AddEmitter(IMEPackage pcc, ExportEntry actor, ExportEntry component, Matrix4x4 actorToWorld, TextureCache textures)
    {
        PropertyCollection componentProps = component.GetProperties();
        if (componentProps.GetProp<BoolProperty>("bAutoActivate")?.Value == false
            || componentProps.GetProp<ObjectProperty>("Template") is not ObjectProperty templateRef
            || !pcc.TryGetUExport(templateRef.Value, out ExportEntry system))
        {
            _skipped[actor.ClassName + " (inactive)"] = _skipped.GetValueOrDefault(actor.ClassName + " (inactive)") + 1;
            return;
        }
        if (!_particleSystems.TryGetValue(system.InstancedFullPath, out JsonArray emitters))
        {
            var notes = new List<string>();
            emitters = new JsonArray();
            foreach (ParticleExport.Emitter emitter in ParticleExport.Read(pcc, system, notes))
            {
                string asset = ParticleAsset(pcc, emitter, textures);
                if (asset == null)
                {
                    notes.Add($"{emitter.Json["name"]}: nothing to draw");
                    continue;
                }
                emitter.Json["asset"] = asset;
                emitters.Add(emitter.Json);
            }
            _particleSystems[system.InstancedFullPath] = emitters;
            Console.WriteLine($"  particles {system.ObjectName.Instanced}: {emitters.Count} emitter(s)"
                + (notes.Count > 0 ? $"; {string.Join("; ", notes)}" : ""));
        }
        if (emitters.Count == 0)
            return;

        var extras = new JsonObject
        {
            ["me_particles"] = new JsonObject { ["system"] = system.InstancedFullPath, ["emitters"] = emitters.DeepClone() },
        };
        var node = new NodeBuilder(actor.ObjectName.Instanced, extras);
        _particles.AddNode(node);
        node.LocalMatrix = UnrealTransform.ToGltf(UnrealTransform.ComponentToActor(componentProps) * actorToWorld);
        _emitters++;
    }

    // The key of what an emitter draws, exported once: a mesh emitter's mesh with its materials
    // (or the emitter's), or a unit quad (glTF's XY plane, facing +Z) with a sprite material.
    // Null when there's nothing to draw.
    private string ParticleAsset(IMEPackage pcc, ParticleExport.Emitter emitter, TextureCache textures)
    {
        string materialKey = emitter.Material?.InstancedFullPath ?? "";
        string key = emitter.Mesh != null
            ? emitter.Mesh.InstancedFullPath + (emitter.OverrideMaterial ? "|" + materialKey : "")
            : "sprite|" + materialKey;
        if (_particleAssetKeys.Contains(key))
            return key;

        IMeshBuilder<MaterialBuilder> render;
        if (emitter.Mesh != null)
        {
            // An effect's mesh may come from a shared package (SFXGame), along with its materials.
            IMEPackage owner = emitter.Mesh.FileRef;
            if (!_geometry.TryGetValue(emitter.Mesh.InstancedFullPath, out MeshGeometry geometry))
                _geometry[emitter.Mesh.InstancedFullPath] = geometry = ReadGeometry(owner, emitter.Mesh, textures);
            MaterialBuilder overrideMaterial = emitter.OverrideMaterial && emitter.Material != null
                ? textures.MaterialFor(emitter.Material, emitter.Material.ObjectName.Instanced) : null;
            var materials = geometry.Lod.Elements
                .Select(e => overrideMaterial ?? (owner == pcc
                    ? textures.MaterialFor(e.Material, $"{geometry.Name}_mat{e.MaterialIndex}")
                    : textures.MaterialFor(TextureCache.Resolve(owner, e.Material), $"{geometry.Name}_mat{e.MaterialIndex}")))
                .ToArray();
            render = BuildRender(geometry.WithMaterials(materials), [], textures, 0, null);
        }
        else
        {
            if (emitter.Material == null)
                return null;
            var quad = new MeshBuilder<VertexPositionNormalTangent, VertexTexture2, VertexEmpty>("ParticleQuad");
            var primitive = quad.UsePrimitive(textures.MaterialFor(emitter.Material, emitter.Material.ObjectName.Instanced));
            VERTEX Corner(float x, float y) => new(
                new VertexPositionNormalTangent(new Vector3(x, y, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f)),
                new VertexTexture2(new Vector2(x + 0.5f, 0.5f - y), new Vector2(x + 0.5f, 0.5f - y)));
            primitive.AddQuadrangle(Corner(-0.5f, -0.5f), Corner(0.5f, -0.5f), Corner(0.5f, 0.5f), Corner(-0.5f, 0.5f));
            render = quad;
        }
        if (render == null)
            return null;
        var node = new NodeBuilder($"Asset_{_particleAssetKeys.Count}", new JsonObject { ["particle_asset"] = key });
        _particleAssets.AddNode(node);
        _scene.AddRigidMesh(render, node);
        _particleAssetKeys.Add(key);
        return key;
    }

    private void AddMeshInstance(IMEPackage pcc, ExportEntry actor, PropertyCollection actorProps, ExportEntry component,
        Matrix4x4 world, NodeBuilder packageNode, TextureCache textures,
        PrimitiveBuilder<MaterialBuilder, VertexPosition, VertexEmpty, VertexEmpty> trimesh)
    {
        PropertyCollection componentProps = component.GetProperties();
        if (componentProps.GetProp<ObjectProperty>("StaticMesh") is not ObjectProperty meshRef
            || !pcc.TryGetUExport(meshRef.Value, out ExportEntry meshExport))
            return;
        if (!_geometry.TryGetValue(meshExport.InstancedFullPath, out MeshGeometry geometry))
            _geometry[meshExport.InstancedFullPath] = geometry = ReadGeometry(pcc, meshExport, textures);

        // BioLedgeMeshActors trace walkway edges for gameplay; their meshes have no material
        // and the game never draws them, but they still collide.
        // Placeables using biog_apl_invisible_p's mesh are invisible use and target points.
        bool hidden = actorProps?.GetProp<BoolProperty>("bHidden")?.Value == true
            || componentProps.GetProp<BoolProperty>("HiddenGame")?.Value == true
            || actor.ClassName == "BioLedgeMeshActor"
            || meshExport.InstancedFullPath == "biog_apl_invisible_p.Invisible";
        if (!hidden)
            AddRenderInstance(pcc, actor, component, componentProps, meshExport, geometry, world, packageNode, textures);

        if (!BlocksPlayer(actorProps, componentProps))
            return;
        if (geometry.Hulls != null)
        {
            foreach (Vector3[] hull in geometry.Hulls)
                AddHull(hull, world);
        }
        else if (geometry.CollisionTriangles != null)
        {
            // Mirrored instances reverse the apparent winding, which one-sided trimesh
            // collision cares about.
            bool flip = geometry.FlipWinding ^ (world.GetDeterminant() < 0);
            var transformed = geometry.Positions.Select(p => new COLLISION_VERTEX(UnrealSpace.Position(Vector3.Transform(p, world)))).ToArray();
            foreach (var (a, b, c) in geometry.CollisionTriangles)
            {
                if (flip)
                    trimesh.AddTriangle(transformed[a], transformed[c], transformed[b]);
                else
                    trimesh.AddTriangle(transformed[a], transformed[b], transformed[c]);
                _trimeshTriangles++;
            }
        }
    }

    // One drawn instance, with the original game's precomputed lighting for it in its extras
    // ("me_lightmap"). ME lights each static mesh in one of two ways, and the loader's lightmap
    // shader decodes both the way the game's own base-pass shaders do:
    //   - texture: a region of a shared atlas page (a color map and a directional map) at
    //     CoordinateScale/CoordinateBias within the page, through the mesh's lightmap UVs.
    //   - vertex: the same two maps as two samples per vertex. They go into the mesh itself
    //     (COLOR_0, and TEXCOORD_2/3, which Godot reads as CUSTOM0), so the instance gets its
    //     own copy of the mesh.
    // Both are stored quantized; scale0 and scale1 (the light-map scales) bring them back.
    private void AddRenderInstance(IMEPackage pcc, ExportEntry actor, ExportEntry component, PropertyCollection componentProps,
        ExportEntry meshExport, MeshGeometry geometry, Matrix4x4 world, NodeBuilder packageNode, TextureCache textures,
        string nodeName = null)
    {
        // The component's material overrides, per element.
        int[] overrides = componentProps.GetProp<ArrayProperty<ObjectProperty>>("Materials")?.Select(o => o.Value).ToArray() ?? [];
        string overrideKey = overrides.Length > 0 ? "|" + string.Join(",", overrides.Select(o => pcc.GetEntry(o)?.InstancedFullPath)) : "";

        LightMap lightMap = ReadLightMap(component);
        var lighting = new JsonObject { ["type"] = "none" };
        IMeshBuilder<MaterialBuilder> render;
        var vertexMap = lightMap as LightMap_1D;
        byte[] vertexSamples = vertexMap != null ? ReadVertexSamples(component, geometry.GltfPositions.Length) : null;
        if (vertexSamples != null)
        {
            render = BuildRender(geometry, overrides, textures, geometry.LightmapInRange ? geometry.LightmapChannel : 0, vertexSamples);
            lighting = new JsonObject
            {
                ["type"] = "vertex",
                ["scale0"] = Json(vertexMap.ScaleVector1),
                ["scale1"] = Json(vertexMap.ScaleVector2),
            };
        }
        else
        {
            var textureMap = lightMap as LightMap_2D;
            int page = textureMap != null && geometry.LightmapChannel > 0 ? LightmapPage(pcc, textureMap) : -1;
            // The second UV set is the mesh's lightmap layout when it has a usable one. A texture
            // lightmap needs that layout even when it strays outside 0-1, which the game allows
            // but Godot's lightmapper doesn't (lightmap_uv: false below keeps it out of bakes).
            int uv2 = page >= 0 || geometry.LightmapInRange ? geometry.LightmapChannel : 0;
            string key = $"{meshExport.InstancedFullPath}|{uv2}{overrideKey}";
            if (!_renders.TryGetValue(key, out render))
                _renders[key] = render = BuildRender(geometry, overrides, textures, uv2, null);
            if (page >= 0)
            {
                lighting = new JsonObject
                {
                    ["type"] = "texture",
                    ["page"] = page,
                    ["scale_bias"] = new JsonArray(textureMap.CoordinateScale.X, textureMap.CoordinateScale.Y,
                        textureMap.CoordinateBias.X, textureMap.CoordinateBias.Y),
                    ["scale0"] = Json(textureMap.ScaleVector1),
                    ["scale1"] = Json(textureMap.ScaleVector2),
                };
            }
        }
        if (render == null)
            return;

        // Meshes without their own 0-1 lightmap UVs carry other UVs (often their tiling shading
        // UVs) in the second set; the loader keeps those out of Godot lightmap bakes.
        var extras = new JsonObject { ["me_lightmap"] = lighting };
        if (!geometry.LightmapInRange)
            extras["lightmap_uv"] = false;
        var node = new NodeBuilder(nodeName ?? actor.ObjectName.Instanced, extras);
        packageNode.AddNode(node);
        node.LocalMatrix = UnrealTransform.ToGltf(world);
        _scene.AddRigidMesh(render, node);
        _instances++;
        _receivers[component.UIndex] = (world, lighting);
        string type = lighting["type"].GetValue<string>();
        if (type == "texture")
            _textureLit++;
        else if (type == "vertex")
            _vertexLit++;
    }

    private static LightMap ReadLightMap(ExportEntry component)
    {
        try
        {
            var binary = ObjectBinary.From<StaticMeshComponent>(component);
            return binary.LODData.Length > 0 ? binary.LODData[0].LightMap : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A vertex lightmap's raw samples, 8 bytes per vertex: the color map, then the directional
    // map, each an FColor (bytes B, G, R, A). Null unless there is one sample per vertex.
    // LEX 6.4.1's LightMap_1D parser misreads LE1's samples (Coefficient1 comes back all zero
    // and the color map lands in Coefficient2), so they're read from the component's binary:
    // the samples' bulk data header is flags 0, element count, byte size, file offset.
    private static byte[] ReadVertexSamples(ExportEntry component, int vertexCount)
    {
        int from = 4;
        return FindVertexSamples(component.GetBinaryData(), vertexCount, ref from);
    }

    // The first vertex-lightmap sample block for vertexCount vertices at or after from, which
    // then moves past it (a decal's receivers each carry their own, in order).
    private static byte[] FindVertexSamples(byte[] data, int vertexCount, ref int from)
    {
        int size = vertexCount * 8;
        for (int o = Math.Max(from, 4); o + 12 + size <= data.Length; o++)
        {
            if (BitConverter.ToInt32(data, o) != vertexCount || BitConverter.ToInt32(data, o + 4) != size
                || BitConverter.ToInt32(data, o - 4) != 0)
                continue;
            from = o + 12 + size;
            return data.AsSpan(o + 12, size).ToArray();
        }
        return null;
    }

    // How far a decal sits off its receiver (centimeters), so the two don't z-fight.
    private const float DecalLift = 0.5f;

    // A static decal, drawn the way UE3 drew it: on the receiver geometry its DecalComponent
    // cooked (StaticReceivers: each receiver's triangles inside the decal's frustum, in the
    // receiver's local space, with its lightmap coordinates), with the decal material's own
    // shader over the receiver's lightmap. A vertex-lit receiver comes with the decal's own
    // samples, one per decal vertex. One mesh per receiver, placed like the receiver. Receivers
    // that aren't drawn, or have no original lighting, are left out.
    // The cooked decal UVs are all zero (UE3 projects them as the level loads), so they're
    // projected here from the decal's hit frame: Width along HitTangent and Height along
    // HitBinormal, centered on HitLocation, with the texture reading upright and unmirrored when
    // seen along the decal actor's facing, as through a projector.
    private void AddDecal(IMEPackage pcc, string packageName, ExportEntry actor, ExportEntry component, TextureCache textures)
    {
        PropertyCollection props = component.GetProperties();
        int materialRef = props.GetProp<ObjectProperty>("DecalMaterial")?.Value ?? 0;
        if (materialRef == 0 || props.GetProp<StructProperty>("HitLocation") is not StructProperty hit
            || props.GetProp<StructProperty>("HitTangent") is not StructProperty hitTangent
            || props.GetProp<StructProperty>("HitBinormal") is not StructProperty hitBinormal)
            return;
        float Float(string name, float fallback) => props.GetProp<FloatProperty>(name)?.Value ?? fallback;
        float width = Float("Width", 200f), height = Float("Height", 200f);
        float tileX = Float("TileX", 1f), tileY = Float("TileY", 1f);
        float offsetX = Float("OffsetX", 0f), offsetY = Float("OffsetY", 0f);
        Vector3 origin = UnrealTransform.Vector(hit);
        Vector3 tangent = UnrealTransform.Vector(hitTangent);
        Vector3 binormal = UnrealTransform.Vector(hitBinormal);
        // DecalRotation (degrees) turns the decal about its normal.
        float rotation = Float("DecalRotation", 0f) * MathF.PI / 180f;
        if (rotation != 0f)
        {
            (float sin, float cos) = MathF.SinCos(rotation);
            (tangent, binormal) = (tangent * cos + binormal * sin, binormal * cos - tangent * sin);
        }
        Vector2 Project(Vector3 world)
        {
            Vector3 d = world - origin;
            return new Vector2(0.5f + offsetX - Vector3.Dot(d, tangent) * tileX / width,
                0.5f + offsetY - Vector3.Dot(d, binormal) * tileY / height);
        }

        MaterialBuilder material = textures.MaterialFor(materialRef, $"{actor.ObjectName.Instanced}_decal");
        var decal = ObjectBinary.From<DecalComponent>(component);
        byte[] binary = component.GetBinaryData();
        int samplesFrom = 4;
        int meshes = 0;
        Vector2 uvMin = new(float.MaxValue), uvMax = new(float.MinValue);
        foreach (StaticReceiverData receiver in decal.StaticReceivers)
        {
            int count = receiver.Vertices.Length;
            // Read in order for every vertex-lit receiver, drawn or not.
            var vertexMap = receiver.LightMap as LightMap_1D;
            byte[] samples = vertexMap != null ? FindVertexSamples(binary, count, ref samplesFrom) : null;
            if (count == 0 || !_receivers.TryGetValue(receiver.PrimitiveComponent, out var on))
                continue;
            JsonObject lighting;
            if (samples != null)
                lighting = new JsonObject { ["type"] = "vertex", ["scale0"] = Json(vertexMap.ScaleVector1), ["scale1"] = Json(vertexMap.ScaleVector2) };
            else if (on.Lighting["type"]?.GetValue<string>() == "texture")
                lighting = (JsonObject)on.Lighting.DeepClone();
            else
            {
                _decalsUnlit++;
                continue;
            }

            var positions = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector4[count];
            var uvs = new Vector2[count];
            for (int v = 0; v < count; v++)
            {
                DecalVertex vertex = receiver.Vertices[v];
                Vector3 normal = MeshCommand.SafeNormalize(MeshCommand.Unpack(vertex.TangentZ));
                positions[v] = UnrealSpace.Position(vertex.Position + normal * DecalLift);
                normals[v] = MeshCommand.SafeNormalize(UnrealSpace.Direction(normal));
                tangents[v] = Tangent(normals[v], vertex.TangentX, vertex.TangentZ);
                uvs[v] = Project(Vector3.Transform(vertex.Position, on.World));
                uvMin = Vector2.Min(uvMin, uvs[v]);
                uvMax = Vector2.Max(uvMax, uvs[v]);
            }
            int triangles = Math.Min((int)receiver.NumTriangles, receiver.Indices.Length / 3);
            bool flip = MeshCommand.ShouldFlipWinding(receiver.Indices.Select(i => (uint)i).ToArray(), positions, normals);
            string name = $"{packageName}_{actor.ObjectName.Instanced}_{meshes}";

            IMeshBuilder<MaterialBuilder> mesh;
            if (samples == null)
            {
                var vertices = new VERTEX[count];
                for (int v = 0; v < count; v++)
                    vertices[v] = new VERTEX(new VertexPositionNormalTangent(positions[v], normals[v], tangents[v]),
                        new VertexTexture2(uvs[v], receiver.Vertices[v].LightMapCoordinate));
                mesh = DecalMesh(name, material, vertices, receiver.Indices, triangles, flip);
            }
            else
            {
                var vertices = new VERTEX_LIT[count];
                for (int v = 0; v < count; v++)
                {
                    Vector4 color = Unit(samples, v * 8);
                    Vector4 directional = Unit(samples, v * 8 + 4);
                    vertices[v] = new VERTEX_LIT(new VertexPositionNormalTangent(positions[v], normals[v], tangents[v]),
                        new VertexColor1Texture4(color, uvs[v], uvs[v], new Vector2(directional.X, directional.Y), new Vector2(directional.Z, 0f)));
                }
                mesh = DecalMesh(name, material, vertices, receiver.Indices, triangles, flip);
            }
            if (mesh == null)
                continue;

            // Decals take no part in Godot lightmap bakes (lightmap_uv: false).
            var node = new NodeBuilder(name, new JsonObject { ["me_lightmap"] = lighting, ["decal"] = true, ["lightmap_uv"] = false });
            _decalsNode.AddNode(node);
            node.LocalMatrix = UnrealTransform.ToGltf(on.World);
            _scene.AddRigidMesh(mesh, node);
            meshes++;
        }
        Console.WriteLine($"  decal {actor.ObjectName.Instanced}: {pcc.GetEntry(materialRef)?.ObjectName.Instanced}, {meshes} of {decal.StaticReceivers.Length} receivers"
            + (meshes > 0 ? $", UVs ({uvMin.X:0.##}, {uvMin.Y:0.##}) to ({uvMax.X:0.##}, {uvMax.Y:0.##})" : ""));
        _decals++;
        _decalMeshes += meshes;
    }

    // One decal receiver's triangles; null when none survive (all degenerate).
    private static IMeshBuilder<MaterialBuilder> DecalMesh<TvM>(string name, MaterialBuilder material,
        VertexBuilder<VertexPositionNormalTangent, TvM, VertexEmpty>[] vertices, ushort[] indices, int triangles, bool flip)
        where TvM : struct, IVertexMaterial
    {
        var mesh = new MeshBuilder<VertexPositionNormalTangent, TvM, VertexEmpty>(name);
        var primitive = mesh.UsePrimitive(material);
        for (int t = 0; t < triangles; t++)
        {
            var a = vertices[indices[t * 3]];
            var b = vertices[indices[t * 3 + 1]];
            var c = vertices[indices[t * 3 + 2]];
            if (flip)
                primitive.AddTriangle(a, c, b);
            else
                primitive.AddTriangle(a, b, c);
        }
        return primitive.Triangles.Count > 0 ? mesh : null;
    }

    private static JsonArray Json(Vector3 v) => new JsonArray(v.X, v.Y, v.Z);

    // Unreal 3 blocks a moving actor when both the actor and its component collide and block,
    // and the component blocks non-zero-extent (capsule) traces.
    private static bool BlocksPlayer(PropertyCollection actorProps, PropertyCollection componentProps)
    {
        if (actorProps != null)
        {
            if (actorProps.GetProp<BoolProperty>("bCollideActors")?.Value == false
                || actorProps.GetProp<BoolProperty>("bBlockActors")?.Value == false)
                return false;
            string collisionType = actorProps.GetProp<EnumProperty>("CollisionType")?.Value.Instanced;
            if (collisionType is "COLLIDE_NoCollision" or "COLLIDE_TouchAll" or "COLLIDE_TouchWeapons" or "COLLIDE_BlockWeapons")
                return false;
        }
        return componentProps.GetProp<BoolProperty>("CollideActors")?.Value != false
            && componentProps.GetProp<BoolProperty>("BlockActors")?.Value != false
            && componentProps.GetProp<BoolProperty>("BlockNonZeroExtent")?.Value != false;
    }

    private static MeshGeometry ReadGeometry(IMEPackage pcc, ExportEntry meshExport, TextureCache textures)
    {
        var mesh = ObjectBinary.From<StaticMesh>(meshExport);
        PropertyCollection meshProps = meshExport.GetProperties();
        StaticMeshRenderData lod = mesh.LODModels[0];
        Vector3[] positions = lod.PositionVertexBuffer.VertexData;
        var vertexData = lod.VertexBuffer.VertexData;
        ushort[] indices = lod.IndexBuffer;

        int uvCount = (int)lod.VertexBuffer.NumTexCoords;
        bool fullUVs = lod.VertexBuffer.bUseFullPrecisionUVs;
        var uvs = new Vector2[uvCount][];
        for (int channel = 0; channel < uvCount; channel++)
        {
            uvs[channel] = new Vector2[positions.Length];
            for (int v = 0; v < positions.Length; v++)
            {
                uvs[channel][v] = fullUVs
                    ? vertexData[v].FullPrecisionUVs[channel]
                    : new Vector2(vertexData[v].HalfPrecisionUVs[channel].X, vertexData[v].HalfPrecisionUVs[channel].Y);
            }
        }

        // UV channel 0 shades. An unset LightMapCoordinateIndex means channel 1, which usually
        // carries a padded 0-1 lightmap layout. Not always, though (movers, which Unreal never
        // lightmaps, can tile there). Godot rasters every mesh into the shared atlas, so one
        // layout outside 0-1 covers the whole atlas and blocks every other mesh from the bake.
        int lightmapChannel = meshProps.GetProp<IntProperty>("LightMapCoordinateIndex")?.Value ?? 1;
        if (lightmapChannel <= 0 || lightmapChannel >= uvCount)
            lightmapChannel = 0;
        bool inRange = lightmapChannel > 0
            && uvs[lightmapChannel].All(uv => uv.X >= -0.01f && uv.X <= 1.01f && uv.Y >= -0.01f && uv.Y <= 1.01f);

        var gltfPositions = new Vector3[positions.Length];
        var normals = new Vector3[positions.Length];
        var tangents = new Vector4[positions.Length];
        for (int v = 0; v < positions.Length; v++)
        {
            gltfPositions[v] = UnrealSpace.Position(positions[v]);
            normals[v] = MeshCommand.SafeNormalize(UnrealSpace.Direction(MeshCommand.Unpack(vertexData[v].TangentZ)));
            tangents[v] = Tangent(normals[v], vertexData[v].TangentX, vertexData[v].TangentZ);
        }
        uint[] wideIndices = indices.Select(i => (uint)i).ToArray();

        var geometry = new MeshGeometry
        {
            Name = meshExport.ObjectName.Instanced,
            Lod = lod,
            GltfPositions = gltfPositions,
            Normals = normals,
            Tangents = tangents,
            UVs = uvs,
            LightmapChannel = lightmapChannel,
            LightmapInRange = inRange,
            FlipWinding = MeshCommand.ShouldFlipWinding(wideIndices, gltfPositions, normals),
            // A mesh from another package (an effect's) gets its materials from its caller.
            SectionMaterials = textures.Package == pcc
                ? lod.Elements.Select(e => DrawnMaterial(textures, e.Material, $"{meshExport.ObjectName.Instanced}_mat{e.MaterialIndex}")).ToArray()
                : new MaterialBuilder[lod.Elements.Length],
            Positions = positions,
        };

        bool useSimple = meshProps.GetProp<BoolProperty>("UseSimpleBoxCollision")?.Value == true;
        List<Vector3[]> hulls = ReadBodySetup(pcc, meshProps);
        if (useSimple && hulls.Count > 0)
        {
            geometry.Hulls = hulls;
        }
        else
        {
            var tree = mesh.kDOPTreeME3UDKLE?.Triangles;
            geometry.CollisionTriangles = tree != null
                ? tree.Select(t => ((int)t.Vertex1, (int)t.Vertex2, (int)t.Vertex3)).ToList()
                : Enumerable.Range(0, indices.Length / 3).Select(t => ((int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])).ToList();
        }
        return geometry;
    }

    // A render mesh: shading UVs, the given channel as the second UV set, and, for a vertex
    // lightmap, its samples (raw quantized bytes; see AddRenderInstance). Null when none of
    // its sections are drawn.
    private static IMeshBuilder<MaterialBuilder> BuildRender(MeshGeometry g, int[] overrides, TextureCache textures,
        int uv2, byte[] samples)
    {
        int count = g.GltfPositions.Length;
        if (samples == null)
        {
            var vertices = new VERTEX[count];
            for (int v = 0; v < count; v++)
                vertices[v] = new VERTEX(new VertexPositionNormalTangent(g.GltfPositions[v], g.Normals[v], g.Tangents[v]), new VertexTexture2(g.UVs[0][v], g.UVs[uv2][v]));
            return AddSections(new MeshBuilder<VertexPositionNormalTangent, VertexTexture2, VertexEmpty>(g.Name), vertices, g, overrides, textures);
        }

        var lit = new VERTEX_LIT[count];
        for (int v = 0; v < count; v++)
        {
            Vector4 color = Unit(samples, v * 8);
            Vector4 directional = Unit(samples, v * 8 + 4);
            lit[v] = new VERTEX_LIT(new VertexPositionNormalTangent(g.GltfPositions[v], g.Normals[v], g.Tangents[v]),
                new VertexColor1Texture4(color, g.UVs[0][v], g.UVs[uv2][v],
                    new Vector2(directional.X, directional.Y), new Vector2(directional.Z, 0f)));
        }
        return AddSections(new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture4, VertexEmpty>(g.Name), lit, g, overrides, textures);
    }

    // A vertex's glTF tangent from Unreal's TangentX and TangentZ (whose W holds the bitangent
    // sign), so the translated shaders see the original tangent frame: Unreal's bitangent is
    // cross(TangentZ, TangentX) * sign, and the axis swap to glTF is a reflection, which flips
    // cross products, so glTF's bitangent (cross(normal, tangent) * w) needs w = -sign.
    private static Vector4 Tangent(Vector3 normal, PackedNormal tangentX, PackedNormal tangentZ)
    {
        Vector3 t = UnrealSpace.Direction(MeshCommand.Unpack(tangentX));
        t -= normal * Vector3.Dot(normal, t);
        t = t.LengthSquared() > 1e-8f ? Vector3.Normalize(t) : AnyPerpendicular(normal);
        float sign = tangentZ.W >= 128 ? 1f : -1f;
        return new Vector4(t, -sign);
    }

    private static Vector3 AnyPerpendicular(Vector3 n) =>
        Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));

    // An FColor (stored B, G, R, A) as 0-1 RGB.
    private static Vector4 Unit(byte[] bgra, int offset) =>
        new Vector4(bgra[offset + 2] / 255f, bgra[offset + 1] / 255f, bgra[offset] / 255f, 1f);

    // A section's material, or null for modulated sections (decals) and lit additive ones,
    // which without their shaders would be opaque cards. Unlit additive sections (the neon
    // signs) stay: MaterialFor makes them additive, with their translated shader.
    private static MaterialBuilder DrawnMaterial(TextureCache textures, int material, string fallbackName)
    {
        string blend = textures.BlendMode(material);
        bool unlitAdditive = blend == "BLEND_Additive" && textures.LightingModel(material) == "MLM_Unlit";
        if (unlitAdditive || blend is not ("BLEND_Additive" or "BLEND_Modulate"))
            return textures.MaterialFor(material, fallbackName);
        // Listed in the summary, once per mesh section, so what goes missing is visible.
        string dropped = $"{textures.MaterialName(material) ?? fallbackName} ({blend["BLEND_".Length..]})";
        DroppedSections.AddOrUpdate(dropped, 1, (_, n) => n + 1);
        return null;
    }

    // Material (blend) -> how many mesh sections DrawnMaterial dropped, for this run's level
    // (emptied when one starts).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> DroppedSections = new();

    private static IMeshBuilder<MaterialBuilder> AddSections<TvM>(MeshBuilder<VertexPositionNormalTangent, TvM, VertexEmpty> builder,
        VertexBuilder<VertexPositionNormalTangent, TvM, VertexEmpty>[] vertices, MeshGeometry g, int[] overrides, TextureCache textures)
        where TvM : struct, IVertexMaterial
    {
        ushort[] indices = g.Lod.IndexBuffer;
        int drawn = 0;
        for (int s = 0; s < g.Lod.Elements.Length; s++)
        {
            StaticMeshElement element = g.Lod.Elements[s];
            // Overrides come from the instance's own package; the mesh's own materials were
            // resolved in the package it was read from.
            MaterialBuilder material = g.SectionMaterials[s];
            if (element.MaterialIndex < overrides.Length && overrides[element.MaterialIndex] != 0)
                material = DrawnMaterial(textures, overrides[element.MaterialIndex], $"{g.Name}_mat{element.MaterialIndex}");
            if (material == null)
                continue;
            var primitive = builder.UsePrimitive(material);
            for (int t = 0; t < element.NumTriangles; t++)
            {
                long i = element.FirstIndex + t * 3L;
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                if (g.FlipWinding)
                    primitive.AddTriangle(a, c, b);
                else
                    primitive.AddTriangle(a, b, c);
            }
            drawn++;
        }
        return drawn > 0 ? builder : null;
    }

    // A texture lightmap's atlas page, shared by many meshes, goes out once. glTF has no
    // free-standing textures, so each of its two maps is the base color of an unlit material
    // on a placeholder triangle (LightmapPages/Page_<n>_<map>, with "lightmap_page" and "map"
    // in its extras); the loader takes the textures and drops the node.
    private int LightmapPage(IMEPackage pcc, LightMap_2D map)
    {
        string key = $"{pcc.FilePath}|{map.Texture1}|{map.Texture2}";
        if (_lightmapPages.TryGetValue(key, out int page))
            return page;
        MemoryImage? color = DecodeLightmap(pcc, map.Texture1);
        MemoryImage? directional = DecodeLightmap(pcc, map.Texture2);
        page = color != null && directional != null ? _pageCount++ : -1;
        _lightmapPages[key] = page;
        if (page >= 0)
        {
            AddLightmapTexture(page, "color", color.Value);
            AddLightmapTexture(page, "directional", directional.Value);
        }
        return page;
    }

    // Lightmaps are small enough to keep at full size.
    private static MemoryImage? DecodeLightmap(IMEPackage pcc, int uIndex)
    {
        if (!pcc.TryGetUExport(uIndex, out ExportEntry export))
            return null;
        try
        {
            var texture = new LegendaryExplorerCore.Unreal.Classes.Texture2D(export);
            var top = texture.Mips.First(m => m.storageType != StorageTypes.empty);
            // ME1's own data and mips; encoded by us only if its format has no DDS mapping.
            MemoryImage? image = DdsTextures.Original(export, top.width);
            if (image == null && MaterialBaker.Pixels.MipBgra(export, texture, top) is byte[] bgra)
                image = DdsTextures.Encode(bgra, top.width, top.height, normalMap: false);
            if (image == null)
                Console.Error.WriteLine($"  could not decode lightmap {export.InstancedFullPath}");
            return image;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not decode lightmap {export.InstancedFullPath}: {ex.Message}");
            return null;
        }
    }

    private void AddLightmapTexture(int page, string map, MemoryImage image)
    {
        string name = $"Page_{page}_{map}";
        var material = new MaterialBuilder($"LightmapPage_{page}_{map}").WithUnlitShader().WithChannelImage(KnownChannel.BaseColor, image);
        var mesh = new MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty>(name);
        mesh.UsePrimitive(material).AddTriangle(
            new(new VertexPosition(0f, 0f, 0f), new VertexTexture1(new Vector2(0f, 0f))),
            new(new VertexPosition(0.01f, 0f, 0f), new VertexTexture1(new Vector2(1f, 0f))),
            new(new VertexPosition(0f, 0f, 0.01f), new VertexTexture1(new Vector2(0f, 1f))));
        var node = new NodeBuilder(name, new JsonObject { ["lightmap_page"] = page, ["map"] = map });
        _lightmapPagesNode.AddNode(node);
        _scene.AddRigidMesh(mesh, node);
    }

    // The textures the translated material shaders sample, carried like the lightmap pages:
    // MaterialTextures/Texture_<n>, with "material_texture" (its key) in the node's extras.
    private void AddMaterialTextures()
    {
        NodeBuilder carrier = _root.CreateNode("MaterialTextures");
        int count = 0;
        foreach (var (key, image) in _carriedTextures.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!image.HasValue)
                continue;
            string name = $"Texture_{count++}";
            var material = new MaterialBuilder(name).WithUnlitShader().WithChannelImage(KnownChannel.BaseColor, image.Value);
            var mesh = new MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty>(name);
            mesh.UsePrimitive(material).AddTriangle(
                new(new VertexPosition(0f, 0f, 0f), new VertexTexture1(new Vector2(0f, 0f))),
                new(new VertexPosition(0.01f, 0f, 0f), new VertexTexture1(new Vector2(1f, 0f))),
                new(new VertexPosition(0f, 0f, 0.01f), new VertexTexture1(new Vector2(0f, 1f))));
            var node = new NodeBuilder(name, new JsonObject { ["material_texture"] = key });
            carrier.AddNode(node);
            _scene.AddRigidMesh(mesh, node);
        }
        Console.WriteLine($"Material textures: {count} carried for the translated shaders.");
    }

    // A StaticMesh's simplified collision (its RB_BodySetup's AggGeom), as point sets in
    // mesh-local Unreal space. Boxes become their 8 corners; spheres and capsules their
    // bounding boxes.
    private static List<Vector3[]> ReadBodySetup(IMEPackage pcc, PropertyCollection meshProps)
    {
        var hulls = new List<Vector3[]>();
        if (meshProps.GetProp<ObjectProperty>("BodySetup") is not ObjectProperty bodyRef
            || !pcc.TryGetUExport(bodyRef.Value, out ExportEntry body))
            return hulls;
        if (body.GetProperties().GetProp<StructProperty>("AggGeom") is StructProperty agg)
            hulls.AddRange(ReadAggGeom(agg));
        return hulls;
    }

    internal static List<Vector3[]> ReadAggGeom(StructProperty agg)
    {
        var hulls = new List<Vector3[]>();
        foreach (StructProperty convex in agg.GetProp<ArrayProperty<StructProperty>>("ConvexElems") ?? Enumerable.Empty<StructProperty>())
        {
            Vector3[] points = (convex.GetProp<ArrayProperty<StructProperty>>("VertexData") ?? Enumerable.Empty<StructProperty>())
                .Select(UnrealTransform.Vector).ToArray();
            if (points.Length >= 4)
                hulls.Add(points);
        }
        foreach (StructProperty box in agg.GetProp<ArrayProperty<StructProperty>>("BoxElems") ?? Enumerable.Empty<StructProperty>())
        {
            Vector3 half = new Vector3(
                box.GetProp<FloatProperty>("X")?.Value ?? 0f,
                box.GetProp<FloatProperty>("Y")?.Value ?? 0f,
                box.GetProp<FloatProperty>("Z")?.Value ?? 0f) * 0.5f;
            hulls.Add(BoxCorners(half, UnrealTransform.Matrix(box.GetProp<StructProperty>("TM"))));
        }
        foreach (StructProperty sphere in agg.GetProp<ArrayProperty<StructProperty>>("SphereElems") ?? Enumerable.Empty<StructProperty>())
        {
            float r = sphere.GetProp<FloatProperty>("Radius")?.Value ?? 0f;
            hulls.Add(BoxCorners(new Vector3(r), UnrealTransform.Matrix(sphere.GetProp<StructProperty>("TM"))));
        }
        foreach (StructProperty capsule in agg.GetProp<ArrayProperty<StructProperty>>("SphylElems") ?? Enumerable.Empty<StructProperty>())
        {
            float r = capsule.GetProp<FloatProperty>("Radius")?.Value ?? 0f;
            float length = capsule.GetProp<FloatProperty>("Length")?.Value ?? 0f;
            hulls.Add(BoxCorners(new Vector3(r, r, length * 0.5f + r), UnrealTransform.Matrix(capsule.GetProp<StructProperty>("TM"))));
        }
        return hulls;
    }

    private static Vector3[] BoxCorners(Vector3 half, Matrix4x4 tm)
    {
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
            corners[i] = Vector3.Transform(local, tm);
        }
        return corners;
    }

    private void AddBlockingVolume(IMEPackage pcc, ExportEntry actor, PropertyCollection props, Matrix4x4 actorToWorld)
    {
        if (props.GetProp<ObjectProperty>("BrushComponent") is not ObjectProperty brushRef
            || !pcc.TryGetUExport(brushRef.Value, out ExportEntry brush))
            return;
        PropertyCollection brushProps = brush.GetProperties();
        if (!BlocksPlayer(props, brushProps))
            return;
        if (brushProps.GetProp<StructProperty>("BrushAggGeom") is StructProperty agg)
        {
            foreach (Vector3[] hull in ReadAggGeom(agg))
                AddHull(hull, actorToWorld);
        }
    }

    // A hull goes out as a fan of triangles over its points: the loader only reads the points
    // (Godot builds the convex shape from them), and a fan keeps every point in the mesh.
    private void AddHull(Vector3[] localPoints, Matrix4x4 world)
    {
        var points = localPoints.Select(p => new COLLISION_VERTEX(UnrealSpace.Position(Vector3.Transform(p, world)))).ToArray();
        var hull = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>($"Hull_{_hullCount}");
        var primitive = hull.UsePrimitive(_collisionMaterial);
        for (int i = 1; i + 1 < points.Length; i++)
            primitive.AddTriangle(points[0], points[i], points[i + 1]);
        _scene.AddRigidMesh(hull, _collision.CreateNode(hull.Name));
        _hullCount++;
    }

    // Unreal light components become KHR_lights_punctual lights. Intensities are only a
    // starting point; the raw Unreal values ride along in the node's extras for the loader.
    private void AddLight(ExportEntry component, Matrix4x4 world)
    {
        PropertyCollection props = component.GetProperties();
        if (props.GetProp<BoolProperty>("bEnabled")?.Value == false)
            return;

        float brightness = props.GetProp<FloatProperty>("Brightness")?.Value ?? 1f;
        float radius = props.GetProp<FloatProperty>("Radius")?.Value ?? 1024f;
        float falloff = props.GetProp<FloatProperty>("FalloffExponent")?.Value ?? 2f;
        Vector3 color = UnrealTransform.SrgbColor(props.GetProp<StructProperty>("LightColor"));

        // A sky light is a level's uniform ambient fill (upper and lower hemisphere). glTF has
        // no such light, so it goes out as an empty node whose extras hold its values.
        if (component.ClassName == "SkyLightComponent")
        {
            Vector3 lower = UnrealTransform.SrgbColor(props.GetProp<StructProperty>("LowerColor"));
            _lights.AddNode(new NodeBuilder($"SkyLight_{_lightCount++}", new JsonObject
            {
                ["ue_class"] = component.ClassName,
                ["ue_brightness"] = brightness,
                ["ue_color"] = new JsonArray(color.X, color.Y, color.Z),
                ["ue_lower_brightness"] = props.GetProp<FloatProperty>("LowerBrightness")?.Value ?? 0f,
                ["ue_lower_color"] = new JsonArray(lower.X, lower.Y, lower.Z),
            }));
            Console.WriteLine($"  sky light: brightness {brightness}, color {color}");
            return;
        }

        LightBuilder light;
        switch (component.ClassName)
        {
            case "PointLightComponent":
            case "DominantPointLightComponent":
                light = new LightBuilder.Point { Range = radius * UnrealSpace.UnitsToMeters };
                break;
            case "SpotLightComponent":
            case "DominantSpotLightComponent":
                // glTF needs 0 <= inner < outer <= 90 degrees, exclusive at 90 for the inner cone.
                float outer = Math.Clamp(props.GetProp<FloatProperty>("OuterConeAngle")?.Value ?? 44f, 1f, 89.9f);
                float inner = Math.Clamp(props.GetProp<FloatProperty>("InnerConeAngle")?.Value ?? 0f, 0f, outer - 0.1f);
                // SharpGLTF leaves out cone angles equal to glTF's defaults (inner 0, outer 45°),
                // and Godot 4.7's importer reads both keys without checking: a missing inner logs
                // an error, and a missing outer reads as 0, so the light is dropped. Keep both
                // just off their defaults so they're always written.
                const float NotDefault = 1e-4f;
                float innerRadians = MathF.Max(inner * MathF.PI / 180f, NotDefault);
                float outerRadians = outer * MathF.PI / 180f;
                if (MathF.Abs(outerRadians - MathF.PI / 4f) < NotDefault)
                    outerRadians = MathF.PI / 4f - NotDefault;
                light = new LightBuilder.Spot
                {
                    Range = radius * UnrealSpace.UnitsToMeters,
                    InnerConeAngle = innerRadians,
                    OuterConeAngle = outerRadians,
                };
                break;
            case "DirectionalLightComponent":
            case "DominantDirectionalLightComponent":
                light = new LightBuilder.Directional();
                break;
            default:
                _skipped[component.ClassName] = _skipped.GetValueOrDefault(component.ClassName) + 1;
                return;
        }
        light.Color = color;
        light.Intensity = brightness;

        var extras = new JsonObject
        {
            ["ue_class"] = component.ClassName,
            ["ue_brightness"] = brightness,
            ["ue_radius"] = radius,
            ["ue_falloff"] = falloff,
        };
        // A light can be limited to its own level and the ones it names, e.g. the Wards' sky
        // directional (in _00_ART) only reaches the open areas 01, 02, 08, 09, and 10. ME1 names
        // areas, not packages (there's no BIOA_STA60_01.pcc), so the game matches these as
        // package-name prefixes. Lightmaps already honor this; the game's light environments need it.
        if (props.GetProp<BoolProperty>("bOnlyAffectSameAndSpecifiedLevels")?.Value == true)
        {
            var levels = new JsonArray(Path.GetFileNameWithoutExtension(component.FileRef.FilePath));
            foreach (NameProperty level in props.GetProp<ArrayProperty<NameProperty>>("OtherLevelsToAffect") ?? Enumerable.Empty<NameProperty>())
            {
                string name = level.Value.Instanced;
                if (!string.IsNullOrEmpty(name) && name != "None")
                    levels.Add(name);
            }
            extras["ue_levels"] = levels;
        }
        var node = new NodeBuilder($"{component.ClassName.Replace("Component", "")}_{_lightCount}", extras);
        _lights.AddNode(node);

        // Unreal lights shine along their +X axis; glTF lights along their node's -Z.
        Vector3 position = UnrealSpace.Position(world.Translation);
        Vector3 forward = MeshCommand.SafeNormalize(UnrealSpace.Direction(new Vector3(world.M11, world.M12, world.M13)));
        Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        node.LocalMatrix = Matrix4x4.CreateWorld(position, forward, up);
        _scene.AddLight(light, node);
        _lightCount++;
    }

    public void Write(string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        AddMaterialTextures();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SharpGLTF.Schema2.ModelRoot model = _scene.ToGltf2(Program.GltfSettings);
        string dds = DdsTextures.Convert(model);
        double built = clock.Elapsed.TotalSeconds;
        OutputFiles.Write(outPath, temporary => model.SaveGLB(temporary, Program.GlbSettings));
        WriteLooks(outPath);
        Console.WriteLine($"Wrote {outPath}: {_renders.Count} meshes, {_instances} instances, {_materials.Count} materials, "
            + $"{_images.Count} textures, {_lightCount} lights, {_hullCount} hulls, {_trimeshTriangles} collision triangles "
            + $"(glTF built in {built:0.0} s, saved in {clock.Elapsed.TotalSeconds - built:0.0} s).");
        Console.WriteLine($"Textures: {dds}.");
        Console.WriteLine($"Original lighting: {_textureLit} instances from {_pageCount} lightmap pages, {_vertexLit} from vertex lightmaps, "
            + $"{_instances - _textureLit - _vertexLit} without; {_placeables} placeables.");
        Console.WriteLine($"Particles: {_emitters} emitters from {_particleSystems.Count(s => s.Value.Count > 0)} of {_particleSystems.Count} systems.");
        Console.WriteLine($"Start markers: {_markerCount}.");
        Console.WriteLine($"Decals: {_decals}, drawn on {_decalMeshes} receivers ({_decalsUnlit} without original lighting left out).");
        Console.WriteLine($"Skeletal placeables: {_skeletalPlaceables} from {_skeletalAssets.Count} models, "
            + $"{_sounds.Values.Count(v => v != null)} of {_sounds.Count} sounds to {_soundsDir}.");
        Console.WriteLine($"Placeable effects: {_effects}.");
        Console.WriteLine($"Ambient sounds: {_ambientSounds} ({_ambientMusic} music); music volumes: {_musicVolumeCount}; stream volumes: {_streamVolumeCount}.");
        if (_pawnsNode != null)
        {
            Console.WriteLine($"NPCs: {_pawns} from {_pawnAssets.Count} looks; {_pawnsAtStations} at action stations, "
                + $"{_pawnsRevealed} shown by Kismet, {_pawnsUnposed} without a pose.");
            if (_missingClips.Count > 0)
                Console.WriteLine("NPC clips not found: " + string.Join(", ", _missingClips));
            if (_unusableClips.Count > 0)
                Console.WriteLine("NPC clips left out (no track fits): " + string.Join(", ", _unusableClips));
        }
        if (_skipped.Count > 0)
            Console.WriteLine("Skipped: " + string.Join(", ", _skipped.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        if (DroppedSections.Count > 0)
            Console.WriteLine("Dropped sections: " + string.Join(", ", DroppedSections.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
    }
}

// Unreal 3 transforms (row-vector matrices, as System.Numerics uses) and their glTF form.
public static class UnrealTransform
{
    // An actor's LocalToWorld: pre-pivot, then scale, rotation, and location.
    public static Matrix4x4 ActorToWorld(PropertyCollection props)
    {
        float drawScale = props.GetProp<FloatProperty>("DrawScale")?.Value ?? 1f;
        Vector3 drawScale3D = props.GetProp<StructProperty>("DrawScale3D") is StructProperty s ? Vector(s) : Vector3.One;
        Vector3 prePivot = props.GetProp<StructProperty>("PrePivot") is StructProperty p ? Vector(p) : Vector3.Zero;
        return Matrix4x4.CreateTranslation(-prePivot)
            * ScaleRotationTranslation(drawScale * drawScale3D, props.GetProp<StructProperty>("Rotation"), Location(props, "Location"));
    }

    // A primitive component's own offset from its actor.
    public static Matrix4x4 ComponentToActor(PropertyCollection props)
    {
        float scale = props.GetProp<FloatProperty>("Scale")?.Value ?? 1f;
        Vector3 scale3D = props.GetProp<StructProperty>("Scale3D") is StructProperty s ? Vector(s) : Vector3.One;
        return ScaleRotationTranslation(scale * scale3D, props.GetProp<StructProperty>("Rotation"), Location(props, "Translation"));
    }

    public static Matrix4x4 WithoutScale(Matrix4x4 m)
    {
        Vector3 x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        Vector3 y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        Vector3 z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, m.M41, m.M42, m.M43, 1);
    }

    // Unreal world matrix -> glTF node matrix. Mesh vertices are converted by the Y/Z swap P
    // (and cm -> m), so a world matrix W becomes P·W·P, with its translation scaled to meters.
    public static Matrix4x4 ToGltf(Matrix4x4 w)
    {
        var p = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        Matrix4x4 g = p * w * p;
        g.M41 *= UnrealSpace.UnitsToMeters;
        g.M42 *= UnrealSpace.UnitsToMeters;
        g.M43 *= UnrealSpace.UnitsToMeters;
        return g;
    }

    // Unreal's FRotationMatrix, with scale applied per axis row and the translation last.
    private static Matrix4x4 ScaleRotationTranslation(Vector3 scale, StructProperty rotator, Vector3 translation)
    {
        const float ToRadians = MathF.PI / 32768f;
        float pitch = (rotator?.GetProp<IntProperty>("Pitch")?.Value ?? 0) * ToRadians;
        float yaw = (rotator?.GetProp<IntProperty>("Yaw")?.Value ?? 0) * ToRadians;
        float roll = (rotator?.GetProp<IntProperty>("Roll")?.Value ?? 0) * ToRadians;
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);

        var m = new Matrix4x4(
            cp * cy, cp * sy, sp, 0,
            sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp, 0,
            -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp, 0,
            0, 0, 0, 1);
        m = Matrix4x4.CreateScale(scale) * m;
        m.Translation = translation;
        return m;
    }

    private static Vector3 Location(PropertyCollection props, string name) =>
        props.GetProp<StructProperty>(name) is StructProperty s ? Vector(s) : Vector3.Zero;

    public static Vector3 Vector(StructProperty v) => new Vector3(
        v.GetProp<FloatProperty>("X")?.Value ?? 0f,
        v.GetProp<FloatProperty>("Y")?.Value ?? 0f,
        v.GetProp<FloatProperty>("Z")?.Value ?? 0f);

    // A Matrix struct (XPlane..WPlane rows).
    public static Matrix4x4 Matrix(StructProperty m)
    {
        if (m == null)
            return Matrix4x4.Identity;
        Vector4 Row(string name) => m.GetProp<StructProperty>(name) is StructProperty r
            ? new Vector4(r.GetProp<FloatProperty>("X")?.Value ?? 0f, r.GetProp<FloatProperty>("Y")?.Value ?? 0f,
                r.GetProp<FloatProperty>("Z")?.Value ?? 0f, r.GetProp<FloatProperty>("W")?.Value ?? 0f)
            : Vector4.Zero;
        Vector4 x = Row("XPlane"), y = Row("YPlane"), z = Row("ZPlane"), w = Row("WPlane");
        return new Matrix4x4(x.X, x.Y, x.Z, x.W, y.X, y.Y, y.Z, y.W, z.X, z.Y, z.Z, z.W, w.X, w.Y, w.Z, w.W);
    }

    // An FColor (sRGB bytes; unset means white) as a linear color.
    public static Vector3 SrgbColor(StructProperty c)
    {
        static float Linear(int b) => MathF.Pow(b / 255f, 2.2f);
        if (c == null)
            return Vector3.One;
        return new Vector3(
            Linear(c.GetProp<ByteProperty>("R")?.Value ?? 255),
            Linear(c.GetProp<ByteProperty>("G")?.Value ?? 255),
            Linear(c.GetProp<ByteProperty>("B")?.Value ?? 255));
    }
}
