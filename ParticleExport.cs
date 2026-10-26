using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LexExport;

// Reads Cascade particle systems (a level's emitters, a power's effects): each emitter's
// highest-detail LOD (Level 0), its modules, and their distributions, as the "me_particles"
// description that the game's particle builder turns into GPUParticles3D nodes.
//
// Cooked packages keep only each distribution's lookup table (FRawDistribution), not the
// distribution object:
//   Op:    1 = one value per entry, 2 = random between an entry's min and max, 3 = either its
//          min or its max ("extreme"); random and extreme entries hold two values (min, max).
//   Time:  entries sit LookupTableTimeScale apart from LookupTableStartTime, over the
//          particle's relative life (0-1) for the *OverLife modules, emitter time otherwise.
//   Float tables open with their output range [min, max]; vector tables are FVectors, with
//   the range in separate fields.
//   Type:  a vector's axis lock (EDVLF: 1 XY, 2 XZ, 3 YZ, 4 XYZ), whose axes share one
//          random value (sizes lock XYZ, so sprites stay square).
// A table left empty means the distribution object decides at runtime: ME3's effects drive
// some (spawn radii, offsets) by instance parameters (Distribution*ParticleParameter), read
// here as the value they'd take for the parameters given (Read's parameters, else unset).
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

    // Modules that change nothing visible here (BioWare's sounds and collision decals among
    // them); any other unknown module leaves its emitter out.
    private static readonly HashSet<string> Ignored = new()
    {
        "ParticleModuleParameterDynamic", "ParticleModuleCollision", "BioParticleModuleSound", "BioParticleModuleCollisionDecal",
    };

    // Float, FloatCm (centimeters to meters), Vector (Unreal's order and units), Spatial (a
    // position, velocity, or acceleration: Godot's axes, meters), Rotation (turns about
    // Godot's axes), Axes (per-axis multipliers in Godot's axes, unscaled).
    private enum Kind { Float, FloatCm, Vector, Spatial, Rotation, Axes }

    // shared: effects (ME3's powers) whose meshes and materials may be imports from the game's
    // shared packages (TextureCache.Resolve); levels keep to their own package. parameters:
    // instance parameter values by name, for parameter-driven distributions.
    public static List<Emitter> Read(IMEPackage pcc, ExportEntry system, List<string> notes, bool shared = false,
        IReadOnlyDictionary<string, float> parameters = null)
    {
        var result = new List<Emitter>();
        var reader = new Reader(pcc, shared, parameters ?? new Dictionary<string, float>());
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
            Emitter read = reader.ReadEmitter(name, lodProps, notes);
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

    // Unreal's per-axis flags (X, Y, Z) in Godot's order (X, Z, Y).
    private static JsonArray GodotAxes(bool x, bool y, bool z) => new(x, z, y);

    private static bool Flag(PropertyCollection p, string name, bool fallback) => p.GetProp<BoolProperty>(name)?.Value ?? fallback;

    private sealed class Reader(IMEPackage pcc, bool shared, IReadOnlyDictionary<string, float> parameters)
    {
        private ExportEntry Find(int uIndex) =>
            shared ? TextureCache.Resolve(pcc, uIndex) : pcc.TryGetUExport(uIndex, out ExportEntry e) ? e : null;

        public Emitter ReadEmitter(string name, PropertyCollection lodProps, List<string> notes)
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
            emitter.Material = req.GetProp<ObjectProperty>("Material") is ObjectProperty mat ? Find(mat.Value) : null;
            json["screen_alignment"] = req.GetProp<EnumProperty>("ScreenAlignment")?.Value.Instanced ?? "PSA_Square";
            json["local_space"] = Flag(req, "bUseLocalSpace", false);
            json["duration"] = req.GetProp<FloatProperty>("EmitterDuration")?.Value ?? 1f;
            json["loops"] = req.GetProp<IntProperty>("EmitterLoops")?.Value ?? 0;
            json["delay"] = req.GetProp<FloatProperty>("EmitterDelay")?.Value ?? 0f;
            json["kill_on_deactivate"] = Flag(req, "bKillOnDeactivate", false);
            json["kill_on_completed"] = Flag(req, "bKillOnCompleted", false);
            json["spawn_rate"] = Dist(req, "SpawnRate", Kind.Float);
            int columns = req.GetProp<IntProperty>("SubImages_Horizontal")?.Value ?? 1;
            int rows = req.GetProp<IntProperty>("SubImages_Vertical")?.Value ?? 1;
            if (columns > 1 || rows > 1)
            {
                json["sub_images"] = new JsonArray(Math.Max(columns, 1), Math.Max(rows, 1));
                json["sub_uv_method"] = req.GetProp<EnumProperty>("InterpolationMethod")?.Value.Instanced ?? "PSUVIM_None";
            }
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
                emitter.Mesh = td.GetProp<ObjectProperty>("Mesh") is ObjectProperty mesh ? Find(mesh.Value) : null;
                emitter.OverrideMaterial = Flag(td, "bOverrideMaterial", false);
                json["mesh_alignment"] = td.GetProp<EnumProperty>("MeshAlignment")?.Value.Instanced ?? "PSMA_MeshFaceCameraWithRoll";
                if (emitter.Mesh == null)
                    unsupported.Add("mesh emitter without a mesh");
            }
            json["mesh"] = emitter.Mesh != null;

            foreach (ObjectProperty moduleRef in lodProps.GetProp<ArrayProperty<ObjectProperty>>("Modules") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (!pcc.TryGetUExport(moduleRef.Value, out ExportEntry module))
                    continue;
                PropertyCollection p = module.GetProperties();
                if (Flag(p, "bEnabled", true) == false)
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
                        json["velocity_world_space"] = Flag(p, "bInWorldSpace", false);
                        break;
                    case "ParticleModuleAcceleration":
                        json["acceleration"] = Dist(p, "Acceleration", Kind.Spatial);
                        break;
                    case "ParticleModuleSize":
                        json["size"] = Dist(p, "StartSize", Kind.Vector);
                        break;
                    case "ParticleModuleSizeMultiplyLife":
                        json["size_life"] = Dist(p, "LifeMultiplier", Kind.Vector);
                        json["size_life_axes"] = new JsonArray(Flag(p, "MultiplyX", true), Flag(p, "MultiplyY", true), Flag(p, "MultiplyZ", true));
                        break;
                    case "ParticleModuleSizeScale":
                        json["size_scale_life"] = Dist(p, "SizeScale", Kind.Vector);
                        json["size_scale_axes"] = new JsonArray(Flag(p, "EnableX", true), Flag(p, "EnableY", true), Flag(p, "EnableZ", true));
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

                    // Spawn shapes: a point in (or on) a sphere or cylinder, optionally sent
                    // outward at VelocityScale times its offset (UE3's rule; negative pulls in).
                    // axes: which half of each axis the point may fall in (+X, +Y, +Z, -X, -Y, -Z,
                    // Godot's order).
                    case "ParticleModuleLocationPrimitiveSphere":
                    case "ParticleModuleLocationPrimitiveCylinder":
                    {
                        bool cylinder = module.ClassName.EndsWith("Cylinder");
                        var shape = new JsonObject
                        {
                            ["type"] = cylinder ? "cylinder" : "sphere",
                            ["radius"] = Dist(p, "StartRadius", Kind.FloatCm),
                            ["surface"] = Flag(p, "SurfaceOnly", false),
                            ["offset"] = Dist(p, "StartLocation", Kind.Spatial),
                            ["velocity_scale"] = Flag(p, "Velocity", false) ? Dist(p, "VelocityScale", Kind.Float) : null,
                            ["positive"] = GodotAxes(Flag(p, "Positive_X", true), Flag(p, "Positive_Y", true), Flag(p, "Positive_Z", true)),
                            ["negative"] = GodotAxes(Flag(p, "Negative_X", true), Flag(p, "Negative_Y", true), Flag(p, "Negative_Z", true)),
                        };
                        if (cylinder)
                        {
                            shape["height"] = Dist(p, "StartHeight", Kind.FloatCm);
                            // Unreal's X, Y, Z are Godot's X, Z, Y (default Z: up).
                            shape["axis"] = (p.GetProp<EnumProperty>("HeightAxis")?.Value.Instanced ?? "PMLPC_HEIGHTAXIS_Z") switch
                            {
                                "PMLPC_HEIGHTAXIS_X" => 0,
                                "PMLPC_HEIGHTAXIS_Y" => 2,
                                _ => 1,
                            };
                            shape["radial_velocity"] = Flag(p, "RadialVelocity", true);
                        }
                        json["shape"] = shape;
                        break;
                    }
                    // Holds the particle at a position over its life (relative to the emitter).
                    case "ParticleModuleLocationDirect":
                        json["location_direct"] = Dist(p, "Location", Kind.Spatial);
                        json["location_direct_offset"] = Dist(p, "LocationOffset", Kind.Spatial);
                        break;
                    // Velocity scaled per axis over life (or set outright when Absolute).
                    case "ParticleModuleVelocityOverLifetime":
                        json["velocity_life"] = Dist(p, "VelOverLife", Kind.Axes);
                        json["velocity_life_absolute"] = Flag(p, "Absolute", false);
                        break;
                    case "ParticleModuleAccelerationOverLifetime":
                        json["acceleration_life"] = Dist(p, "AccelOverLife", Kind.Spatial);
                        break;
                    // Size times the particle's speed (cm/s) per axis: the stretched sparks.
                    case "ParticleModuleSizeMultiplyVelocity":
                        json["size_velocity"] = Dist(p, "VelocityMultiplier", Kind.Vector);
                        json["size_velocity_axes"] = new JsonArray(Flag(p, "MultiplyX", true), Flag(p, "MultiplyY", true), Flag(p, "MultiplyZ", true));
                        break;
                    // The sub-image (of sub_images) over life.
                    case "ParticleModuleSubUV":
                        json["sub_uv"] = Dist(p, "SubImageIndex", Kind.Float);
                        break;
                    // Particles per UnitScalar centimeters the emitter moves (the trails).
                    case "ParticleModuleSpawnPerUnit":
                        json["spawn_per_unit"] = new JsonObject
                        {
                            ["count"] = Dist(p, "SpawnPerUnit", Kind.Float),
                            ["unit"] = (p.GetProp<FloatProperty>("UnitScalar")?.Value ?? 50f) * 0.01f,
                            ["ignore_rate_moving"] = Flag(p, "bIgnoreSpawnRateWhenMoving", false),
                        };
                        break;
                    // Draws the particle offset from where it is, the offset turning over life.
                    case "ParticleModuleOrbit":
                        json["orbit"] = new JsonObject
                        {
                            ["offset"] = Dist(p, "OffsetAmount", Kind.Spatial),
                            ["rotation"] = Dist(p, "RotationAmount", Kind.Rotation),
                            ["rate"] = Dist(p, "RotationRateAmount", Kind.Rotation),
                        };
                        break;
                    // Pulls particles toward a point that moves over the emitter's time.
                    case "ParticleModuleAttractorPoint":
                        json["attractor"] = new JsonObject
                        {
                            ["position"] = Dist(p, "Position", Kind.Spatial),
                            ["range"] = Dist(p, "Range", Kind.FloatCm),
                            // Per second: the pull is Strength times the offset to the point.
                            ["strength"] = Dist(p, "Strength", Kind.Float),
                            ["by_distance"] = Flag(p, "StrengthByDistance", true),
                            ["affect_base"] = Flag(p, "bAffectBaseVelocity", false),
                            ["override"] = Flag(p, "bOverrideVelocity", false),
                            ["world"] = Flag(p, "bUseWorldSpacePosition", false),
                        };
                        break;
                    case "ParticleModuleVelocityInheritParent":
                        json["inherit_parent"] = Dist(p, "Scale", Kind.Axes);
                        break;
                    // BioWare's crusts: spawn on the attached character's skin, weighted per
                    // bone (or per area of bones, from the area list).
                    case "BioParticleModuleLocationAttachedMesh":
                        json["attached_mesh"] = AttachedMesh(p);
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

        // { bones: [{ name, weight }], vertices: weigh each bone by its share of skin vertices
        // too (UseNumVertsAsWeights), local: particles move with the character }.
        private JsonObject AttachedMesh(PropertyCollection p)
        {
            // The area list: tag -> its bones.
            var areas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool byVertices = false;
            if (p.GetProp<ObjectProperty>("m_EmissionAreaList") is ObjectProperty listRef && Find(listRef.Value) is ExportEntry list)
            {
                foreach (StructProperty area in list.GetProperties().GetProp<ArrayProperty<StructProperty>>("m_EmissionAreas") ?? Enumerable.Empty<StructProperty>())
                {
                    string tag = area.GetProp<NameProperty>("AreaTag")?.Value.Instanced;
                    var bones = (area.GetProp<ArrayProperty<StructProperty>>("Bones") ?? Enumerable.Empty<StructProperty>())
                        .Select(b => b.GetProp<NameProperty>("BoneName")?.Value.Instanced)
                        .Where(b => b != null && b != "None")
                        .ToList();
                    if (tag != null)
                        areas[tag] = bones;
                    byVertices |= area.GetProp<BoolProperty>("UseNumVertsAsWeights")?.Value == true;
                }
            }
            // The module's weights name bones (EAST_WeightedDensityPerBone) or areas; without any,
            // every area's bones weigh the same.
            var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var listed = p.GetProp<ArrayProperty<StructProperty>>("m_EmissionAreaWeights");
            if (listed != null && listed.Count > 0)
            {
                foreach (StructProperty entry in listed)
                {
                    string tag = entry.GetProp<NameProperty>("AreaTag")?.Value.Instanced;
                    float weight = entry.GetProp<FloatProperty>("Weight")?.Value ?? 1f;
                    if (tag == null)
                        continue;
                    if (areas.TryGetValue(tag, out List<string> bones) && bones.Count > 0)
                    {
                        foreach (string bone in bones)
                            weights[bone] = weights.GetValueOrDefault(bone) + weight / bones.Count;
                    }
                    else
                        weights[tag] = weights.GetValueOrDefault(tag) + weight;
                }
            }
            else
            {
                foreach (string bone in areas.Values.SelectMany(b => b))
                    weights[bone] = 1f;
            }
            return new JsonObject
            {
                ["bones"] = new JsonArray(weights.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["weight"] = kv.Value }).ToArray()),
                ["vertices"] = byVertices,
                ["local"] = Flag(p, "bUseAttachedLocalSpace", false),
            };
        }

        // A distribution's lookup table as { op, lock, scale, start, min: [entries], max: [entries] },
        // each entry a number or an [x, y, z] (see the header). Null when the module doesn't have it.
        private JsonObject Dist(PropertyCollection props, string name, Kind kind)
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
                values.AddRange(floats.Skip(2).Select(f => (JsonNode)(f.Value * Unit(kind))));
            }
            else if (table is ArrayProperty<StructProperty> vectors)
            {
                if (kind is Kind.Float or Kind.FloatCm)
                    return null;
                foreach (StructProperty v in vectors)
                {
                    values.Add(Convert(kind, v.GetProp<FloatProperty>("X")?.Value ?? 0f, v.GetProp<FloatProperty>("Y")?.Value ?? 0f,
                        v.GetProp<FloatProperty>("Z")?.Value ?? 0f));
                }
                // Locked axes follow the swap too: Unreal's XY pair is Godot's XZ, and the reverse.
                if (kind is Kind.Spatial or Kind.Rotation or Kind.Axes)
                    lockFlags = lockFlags switch { 1 => 2, 2 => 1, _ => lockFlags };
            }
            else
                return null;

            int entries = values.Count / chunk;
            if (entries == 0)
                return Parameter(raw, kind);
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

        // An uncooked distribution: a parameter-driven one's value for the given parameters (else
        // its Constant), mapped from [MinInput, MaxInput] to [MinOutput, MaxOutput] (DPM_Normal;
        // DPM_Direct passes it through), or a constant's value. A one-entry table, or null.
        private JsonObject Parameter(StructProperty raw, Kind kind)
        {
            if (raw.GetProp<ObjectProperty>("Distribution") is not ObjectProperty distRef || Find(distRef.Value) is not ExportEntry dist)
                return null;
            PropertyCollection d = dist.GetProperties();
            JsonNode value;
            if (dist.ClassName == "DistributionFloatParticleParameter" || dist.ClassName == "DistributionFloatConstant")
            {
                if (kind is not (Kind.Float or Kind.FloatCm))
                    return null;
                value = MapFloat(d, dist.ClassName.EndsWith("Constant")) * Unit(kind);
            }
            else if (dist.ClassName == "DistributionVectorParticleParameter" || dist.ClassName == "DistributionVectorConstant")
            {
                if (kind is Kind.Float or Kind.FloatCm)
                    return null;
                bool constant = dist.ClassName.EndsWith("Constant");
                float[] v = new[] { "X", "Y", "Z" }.Select((axis, i) => MapComponent(d, axis, i, constant)).ToArray();
                value = Convert(kind, v[0], v[1], v[2]);
            }
            else
                return null;
            return new JsonObject
            {
                ["op"] = 1, ["lock"] = 0, ["scale"] = 0f, ["start"] = 0f,
                ["min"] = new JsonArray(value), ["max"] = new JsonArray(value.DeepClone()),
            };
        }

        private float MapFloat(PropertyCollection d, bool constant)
        {
            float input = d.GetProp<FloatProperty>("Constant")?.Value ?? 0f;
            if (constant)
                return input;
            string parameter = d.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            if (parameter != null && parameters.TryGetValue(parameter, out float given))
                input = given;
            if ((d.GetProp<EnumProperty>("ParamMode")?.Value.Instanced ?? "DPM_Normal") == "DPM_Direct")
                return input;
            return Map(input, d.GetProp<FloatProperty>("MinInput")?.Value ?? 0f, d.GetProp<FloatProperty>("MaxInput")?.Value ?? 1f,
                d.GetProp<FloatProperty>("MinOutput")?.Value ?? 0f, d.GetProp<FloatProperty>("MaxOutput")?.Value ?? 1f);
        }

        private float MapComponent(PropertyCollection d, string axis, int index, bool constant)
        {
            float Field(string name, float fallback) =>
                d.GetProp<StructProperty>(name)?.GetProp<FloatProperty>(axis)?.Value ?? fallback;
            float input = Field("Constant", 0f);
            if (constant)
                return input;
            string parameter = d.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
            if (parameter != null && parameters.TryGetValue(parameter, out float given))
                input = given;
            var modes = d.GetProp<ArrayProperty<EnumProperty>>("ParamModes");
            if (modes != null && index < modes.Count && modes[index].Value.Instanced == "DPM_Direct")
                return input;
            return Map(input, Field("MinInput", 0f), Field("MaxInput", 1f), Field("MinOutput", 0f), Field("MaxOutput", 1f));
        }

        private static float Map(float input, float minIn, float maxIn, float minOut, float maxOut)
        {
            if (Math.Abs(maxIn - minIn) < 1e-6f)
                return minOut;
            float t = Math.Clamp((input - minIn) / (maxIn - minIn), 0f, 1f);
            return minOut + (maxOut - minOut) * t;
        }

        private static float Unit(Kind kind) => kind == Kind.FloatCm ? 0.01f : 1f;

        private static JsonArray Convert(Kind kind, float x, float y, float z) => kind switch
        {
            Kind.Spatial => new JsonArray(x * 0.01f, z * 0.01f, y * 0.01f),
            // Rotating about Unreal's X, Y, Z is rotating about Godot's X, Z, Y, but the axis
            // swap is a reflection, which reverses the sense of every rotation.
            Kind.Rotation => new JsonArray(-x, -z, -y),
            Kind.Axes => new JsonArray(x, z, y),
            _ => new JsonArray(x, y, z),
        };
    }
}
