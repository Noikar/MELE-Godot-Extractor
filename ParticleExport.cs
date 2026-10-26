using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Reads Cascade particle systems for the level export: each emitter's highest-detail LOD
// (Level 0), its modules, and their distributions, as the "me_particles" description that
// the game's particle builder turns into GPUParticles3D nodes.
//
// Cooked levels keep only each distribution's lookup table (FRawDistribution), not the
// distribution object:
//   Op:    1 = one value per entry, 2 = random between an entry's min and max, 3 = either its
//          min or its max ("extreme"); random and extreme entries hold two values (min, max).
//   Time:  entries sit LookupTableTimeScale apart from LookupTableStartTime, over the
//          particle's relative life (0-1) for the *OverLife modules, emitter time otherwise.
//   Float tables open with their output range [min, max]; vector tables are FVectors, with
//   the range in separate fields.
//   Type:  a vector's axis lock (EDVLF: 1 XY, 2 XZ, 3 YZ, 4 XYZ), whose axes share one
//          random value (sizes lock XYZ, so sprites stay square).
// Positions, velocities, and accelerations go out in Godot's axes and meters; rotations
// (in turns) as angles about Godot's axes; sizes stay in Unreal's order and units, since
// they mean centimeters for sprites but a scale for meshes.
public static class ParticleExport
{
    public sealed class Emitter
    {
        public JsonObject Json;
        public ExportEntry Material;   // the Required module's material
        public ExportEntry Mesh;       // a mesh emitter's StaticMesh
        public bool OverrideMaterial;  // a mesh emitter drawn with Material instead of the mesh's own
    }

    // Modules that change nothing visible here; any other unknown module leaves its emitter out.
    private static readonly HashSet<string> Ignored = new() { "ParticleModuleParameterDynamic", "ParticleModuleCollision" };

    private enum Kind { Float, FloatCm, Vector, Spatial, Rotation }

    public static List<Emitter> Read(IMEPackage pcc, ExportEntry system, List<string> notes)
    {
        var result = new List<Emitter>();
        foreach (ObjectProperty emitterRef in system.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("Emitters") ?? Enumerable.Empty<ObjectProperty>())
        {
            if (!pcc.TryGetUExport(emitterRef.Value, out ExportEntry emitter))
                continue;
            string name = emitter.GetProperties().GetProp<NameProperty>("EmitterName")?.Value.Instanced ?? emitter.ObjectName.Instanced;
            ExportEntry lod = Lod0(pcc, emitter);
            if (lod == null)
            {
                notes.Add($"{name}: no LOD 0");
                continue;
            }
            PropertyCollection lodProps = lod.GetProperties();
            if (lodProps.GetProp<BoolProperty>("bEnabled")?.Value == false)
                continue;
            Emitter read = ReadEmitter(pcc, name, lodProps, notes);
            if (read != null)
                result.Add(read);
        }
        return result;
    }

    // The emitter's highest-detail LOD: the lowest Level (left out when 0).
    private static ExportEntry Lod0(IMEPackage pcc, ExportEntry emitter)
    {
        return (emitter.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("LODLevels") ?? Enumerable.Empty<ObjectProperty>())
            .Select(lodRef => pcc.TryGetUExport(lodRef.Value, out ExportEntry lod) ? lod : null)
            .Where(lod => lod != null)
            .MinBy(lod => lod.GetProperties().GetProp<IntProperty>("Level")?.Value ?? 0);
    }

    // A burst list as [{ count, time }]: Count particles (or a random number from CountLow,
    // when it's set) at Time, as a fraction of the emitter's duration. Null when empty.
    private static JsonArray Bursts(PropertyCollection props)
    {
        var list = props.GetProp<ArrayProperty<StructProperty>>("BurstList");
        if (list == null || list.Count == 0)
            return null;
        var bursts = new JsonArray();
        foreach (StructProperty burst in list)
        {
            int count = burst.GetProp<IntProperty>("Count")?.Value ?? 0;
            int low = burst.GetProp<IntProperty>("CountLow")?.Value ?? -1;
            bursts.Add(new JsonObject
            {
                ["count"] = count,
                ["count_low"] = low >= 0 ? low : count,
                ["time"] = burst.GetProp<FloatProperty>("Time")?.Value ?? 0f,
            });
        }
        return bursts;
    }

    private static Emitter ReadEmitter(IMEPackage pcc, string name, PropertyCollection lodProps, List<string> notes)
    {
        var json = new JsonObject { ["name"] = name };
        var emitter = new Emitter { Json = json };
        var unsupported = new List<string>();

        ExportEntry Module(string property) =>
            lodProps.GetProp<ObjectProperty>(property) is ObjectProperty o && pcc.TryGetUExport(o.Value, out ExportEntry m) ? m : null;

        if (Module("RequiredModule") is not ExportEntry required)
        {
            notes.Add($"{name}: no Required module");
            return null;
        }
        PropertyCollection req = required.GetProperties();
        emitter.Material = req.GetProp<ObjectProperty>("Material") is ObjectProperty mat && pcc.TryGetUExport(mat.Value, out ExportEntry material) ? material : null;
        json["screen_alignment"] = req.GetProp<EnumProperty>("ScreenAlignment")?.Value.Instanced ?? "PSA_Square";
        json["local_space"] = req.GetProp<BoolProperty>("bUseLocalSpace")?.Value ?? false;
        json["duration"] = req.GetProp<FloatProperty>("EmitterDuration")?.Value ?? 1f;
        json["loops"] = req.GetProp<IntProperty>("EmitterLoops")?.Value ?? 0;
        json["delay"] = req.GetProp<FloatProperty>("EmitterDelay")?.Value ?? 0f;
        json["spawn_rate"] = Dist(req, "SpawnRate", Kind.Float);
        if ((req.GetProp<IntProperty>("SubImages_Horizontal")?.Value ?? 1) > 1 || (req.GetProp<IntProperty>("SubImages_Vertical")?.Value ?? 1) > 1)
            unsupported.Add("sub-images");
        // ME1 still carries the older burst list on the Required module; the Spawn module's
        // replaces it when there is one.
        json["bursts"] = Bursts(req);

        if (Module("SpawnModule") is ExportEntry spawn)
        {
            PropertyCollection sp = spawn.GetProperties();
            json["spawn_rate"] = Dist(sp, "Rate", Kind.Float) ?? json["spawn_rate"]?.DeepClone();
            json["spawn_rate_scale"] = Dist(sp, "RateScale", Kind.Float);
            json["bursts"] = Bursts(sp);
        }

        if (Module("TypeDataModule") is ExportEntry typeData)
        {
            if (typeData.ClassName != "ParticleModuleTypeDataMesh")
                unsupported.Add(typeData.ClassName);
            PropertyCollection td = typeData.GetProperties();
            emitter.Mesh = td.GetProp<ObjectProperty>("Mesh") is ObjectProperty mesh && pcc.TryGetUExport(mesh.Value, out ExportEntry m) ? m : null;
            emitter.OverrideMaterial = td.GetProp<BoolProperty>("bOverrideMaterial")?.Value == true;
            if (emitter.Mesh == null)
                unsupported.Add("mesh emitter without a mesh");
        }
        json["mesh"] = emitter.Mesh != null;

        foreach (ObjectProperty moduleRef in lodProps.GetProp<ArrayProperty<ObjectProperty>>("Modules") ?? Enumerable.Empty<ObjectProperty>())
        {
            if (!pcc.TryGetUExport(moduleRef.Value, out ExportEntry module))
                continue;
            PropertyCollection p = module.GetProperties();
            if (p.GetProp<BoolProperty>("bEnabled")?.Value == false)
                continue;
            switch (module.ClassName)
            {
                case "ParticleModuleLifetime":
                    json["lifetime"] = Dist(p, "Lifetime", Kind.Float);
                    break;
                case "ParticleModuleLocation":
                    json["location"] = Dist(p, "StartLocation", Kind.Spatial);
                    break;
                case "ParticleModuleVelocity":
                    json["velocity"] = Dist(p, "StartVelocity", Kind.Spatial);
                    json["velocity_radial"] = Dist(p, "StartVelocityRadial", Kind.FloatCm);
                    json["velocity_world_space"] = p.GetProp<BoolProperty>("bInWorldSpace")?.Value ?? false;
                    break;
                case "ParticleModuleAcceleration":
                    json["acceleration"] = Dist(p, "Acceleration", Kind.Spatial);
                    break;
                case "ParticleModuleSize":
                    json["size"] = Dist(p, "StartSize", Kind.Vector);
                    break;
                case "ParticleModuleSizeMultiplyLife":
                    json["size_life"] = Dist(p, "LifeMultiplier", Kind.Vector);
                    json["size_life_axes"] = new JsonArray(
                        p.GetProp<BoolProperty>("MultiplyX")?.Value ?? true,
                        p.GetProp<BoolProperty>("MultiplyY")?.Value ?? true,
                        p.GetProp<BoolProperty>("MultiplyZ")?.Value ?? true);
                    break;
                case "ParticleModuleSizeScale":
                    json["size_scale_life"] = Dist(p, "SizeScale", Kind.Vector);
                    json["size_scale_axes"] = new JsonArray(
                        p.GetProp<BoolProperty>("EnableX")?.Value ?? true,
                        p.GetProp<BoolProperty>("EnableY")?.Value ?? true,
                        p.GetProp<BoolProperty>("EnableZ")?.Value ?? true);
                    break;
                case "ParticleModuleColor":
                    json["color"] = Dist(p, "StartColor", Kind.Vector);
                    json["alpha"] = Dist(p, "StartAlpha", Kind.Float);
                    break;
                case "ParticleModuleColorOverLife":
                    json["color_life"] = Dist(p, "ColorOverLife", Kind.Vector);
                    json["alpha_life"] = Dist(p, "AlphaOverLife", Kind.Float);
                    break;
                case "ParticleModuleColorScaleOverLife":
                    json["color_scale_life"] = Dist(p, "ColorScaleOverLife", Kind.Vector);
                    json["alpha_scale_life"] = Dist(p, "AlphaScaleOverLife", Kind.Float);
                    break;
                case "ParticleModuleRotation":
                    json["rotation"] = Dist(p, "StartRotation", Kind.Float);
                    break;
                case "ParticleModuleRotationRate":
                    json["rotation_rate"] = Dist(p, "StartRotationRate", Kind.Float);
                    break;
                case "ParticleModuleMeshRotation":
                    json["mesh_rotation"] = Dist(p, "StartRotation", Kind.Rotation);
                    break;
                case "ParticleModuleMeshRotationRate":
                    json["mesh_rotation_rate"] = Dist(p, "StartRotationRate", Kind.Rotation);
                    break;
                case "ParticleModuleOrientationAxisLock":
                    json["axis_lock"] = p.GetProp<EnumProperty>("LockAxisFlags")?.Value.Instanced ?? "EPAL_NONE";
                    break;
                default:
                    if (!Ignored.Contains(module.ClassName))
                        unsupported.Add(module.ClassName.Replace("ParticleModule", ""));
                    break;
            }
        }

        if (unsupported.Count > 0)
        {
            notes.Add($"{name}: left out ({string.Join(", ", unsupported.Distinct())})");
            return null;
        }
        return emitter;
    }

    // A distribution's lookup table as { op, lock, scale, start, min: [entries], max: [entries] },
    // each entry a number or an [x, y, z] (see the header). Null when the module doesn't have it.
    private static JsonObject Dist(PropertyCollection props, string name, Kind kind)
    {
        StructProperty raw = props.GetProp<StructProperty>(name + "Rw") ?? props.GetProp<StructProperty>(name);
        Property table = raw?.Properties.FirstOrDefault(p => p.Name.Instanced == "LookupTable");
        if (table == null)
            return null;
        int op = raw.GetProp<ByteProperty>("Op")?.Value ?? 1;
        int lockFlags = raw.GetProp<ByteProperty>("Type")?.Value ?? 0;
        int chunk = op is 2 or 3 ? 2 : 1;

        var values = new List<JsonNode>();
        if (table is ArrayProperty<FloatProperty> floats)
        {
            if (kind is not (Kind.Float or Kind.FloatCm))
                return null;
            float unit = kind == Kind.FloatCm ? 0.01f : 1f;
            values.AddRange(floats.Skip(2).Select(f => (JsonNode)(f.Value * unit)));
        }
        else if (table is ArrayProperty<StructProperty> vectors)
        {
            if (kind is Kind.Float or Kind.FloatCm)
                return null;
            foreach (StructProperty v in vectors)
            {
                float x = v.GetProp<FloatProperty>("X")?.Value ?? 0f;
                float y = v.GetProp<FloatProperty>("Y")?.Value ?? 0f;
                float z = v.GetProp<FloatProperty>("Z")?.Value ?? 0f;
                values.Add(kind switch
                {
                    Kind.Spatial => new JsonArray(x * 0.01f, z * 0.01f, y * 0.01f),
                    // Rotating about Unreal's X, Y, Z is rotating about Godot's X, Z, Y, but the
                    // axis swap is a reflection, which reverses the sense of every rotation.
                    Kind.Rotation => new JsonArray(-x, -z, -y),
                    _ => new JsonArray(x, y, z),
                });
            }
            // Locked axes follow the swap too: Unreal's XY pair is Godot's XZ, and the reverse.
            if (kind is Kind.Spatial or Kind.Rotation)
                lockFlags = lockFlags switch { 1 => 2, 2 => 1, _ => lockFlags };
        }
        else
            return null;

        int entries = values.Count / chunk;
        if (entries == 0)
            return null;
        var min = new JsonArray();
        var max = new JsonArray();
        for (int i = 0; i < entries; i++)
        {
            min.Add(values[i * chunk].DeepClone());
            max.Add(values[i * chunk + chunk - 1].DeepClone());
        }
        return new JsonObject
        {
            ["op"] = op,
            ["lock"] = lockFlags,
            ["scale"] = raw.GetProp<FloatProperty>("LookupTableTimeScale")?.Value ?? 0f,
            ["start"] = raw.GetProp<FloatProperty>("LookupTableStartTime")?.Value ?? 0f,
            ["min"] = min,
            ["max"] = max,
        };
    }
}
