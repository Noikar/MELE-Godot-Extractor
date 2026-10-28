using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using SharpGLTF.Scenes;

namespace LexExport;

// A power's visual effects from ME3 (powers.json's "vfx"), as <out>/<id>/vfx.glb. The power's
// class default names an RvrClientEffect template per role:
//   caster      CE_CasterCrustTemplate   on the caster while casting (Biotics_Charge)
//   projectile  CE_ProjectileTemplate    on the flying projectile
//   impact      CE_ImpactTemplate        where it lands
//   target      CE_TargetCrustTemplate   on the target while the effect lasts
// (the manifest may name a template for a role the default leaves out: Pull's lift crust is
// only referenced from code; or, with no "power" class, name roles of its own: the omni-tool's
// "arm" and "hand"). Each template is a module list, written as an Effects/<role>
// node whose "me_effect" extras hold { template, modules: [...] }:
//   emitter          a particle system ("me_particles": { system, emitters }, as a level's,
//                    each emitter's "asset" drawn from ParticleAssets/), its tag, duration,
//                    draw scale, and the instance parameters it sets
//   location         the bone it attaches to (m_sAttachment) and how, and its offset and
//                    turn from that bone ("adjust")
//   lifetime         how long the effect lives (cooldown: after it's told to stop)
//   camera_shake     UE3's CameraShake: rotation (Unreal units, 65536 a turn), location (cm),
//                    and FOV (degrees) oscillations, with ShakeScale and its radial falloff
//   effects_material ME3's crust mode on the target's own material: its name, duration, and
//                    parameters (a game's own crust can take its timing from these)
//   sound            the Wwise events it plays (the power's sounds are exported separately)
// Lens flares and framebuffer effects (motion blur) are left out. ME3's power projectiles cast
// no light: their class has an LC_EffectLight, but it isn't among their Components, so it's
// never attached (the lens flare is their only glow). Particle parameters: the power's
// VFXIntensity as "Intensity" (UE3's default 0 when the power doesn't set it), plus each
// emitter module's own.
public partial class LevelBuilder
{
    private static readonly (string Role, string Property)[] EffectRoles =
    [
        ("caster", "CE_CasterCrustTemplate"),
        ("projectile", "CE_ProjectileTemplate"),
        ("impact", "CE_ImpactTemplate"),
        ("target", "CE_TargetCrustTemplate"),
    ];

    public static void ExportPowerVfx(string package, JsonElement vfx, string outPath)
    {
        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(package);
        // Without a power class (the omni-tool), only the roles the manifest names are exported.
        PropertyCollection powerProps = new PropertyCollection();
        if (vfx.TryGetProperty("power", out JsonElement powerName))
        {
            string powerClass = powerName.GetString();
            ExportEntry power = pcc.Exports.FirstOrDefault(e => e.ObjectName.Instanced == "Default__" + powerClass)
                ?? throw new ArgumentException($"No Default__{powerClass} in {Path.GetFileName(package)}.");
            powerProps = power.GetProperties();
        }

        var builder = new LevelBuilder(1024);
        var textures = new TextureCache(pcc, new List<string>(), builder._materials, builder._images)
        {
            MaxTextureSize = 1024,
            GuessDiffuse = true,
            SurfaceShading = true,
            CarriedTextures = builder._carriedTextures,
            SharedShaders = true,
        };
        var parameters = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Intensity"] = powerProps.GetProp<StructProperty>("VFXIntensity")?.GetProp<FloatProperty>("BaseValue")?.Value ?? 0f,
        };
        NodeBuilder effects = builder._root.CreateNode("Effects");

        // The power's four roles, then any other role the manifest names (the omni-tool's arm and hand).
        var roles = EffectRoles.ToList();
        foreach (JsonProperty named in vfx.EnumerateObject())
            if (named.Name != "power" && !roles.Any(r => r.Role == named.Name))
                roles.Add((named.Name, null));
        foreach (var (role, property) in roles)
        {
            ExportEntry template = vfx.TryGetProperty(role, out JsonElement named)
                ? pcc.Exports.FirstOrDefault(e => e.InstancedFullPath.Equals(named.GetString(), StringComparison.OrdinalIgnoreCase))
                : property != null && powerProps.GetProp<ObjectProperty>(property) is ObjectProperty o && pcc.TryGetUExport(o.Value, out ExportEntry t) ? t : null;
            if (template == null)
            {
                Console.WriteLine($"  {role}: no template");
                continue;
            }
            JsonObject effect = builder.ReadEffect(pcc, template, textures, parameters);
            effects.AddNode(new NodeBuilder(role, new JsonObject { ["me_effect"] = effect }));
        }
        builder.WriteEffects(outPath);
    }

    // A location module's offset from its bone (m_LocationAdjust, Unreal units in the bone's frame)
    // and turn (m_RotationAdjust, in turns: X roll, Y pitch, Z yaw, as Cascade's rotations), as
    // one transform in the bone's glTF frame: { x, y, z (basis vectors), origin }. Null when both
    // are zero. Their tables are constants: [min, max, x, y, z, ...]; the first entry is used.
    private static JsonObject LocationAdjust(PropertyCollection p)
    {
        Vector3 First(string name)
        {
            var table = p.GetProp<StructProperty>(name)?.GetProp<ArrayProperty<FloatProperty>>("LookupTable");
            return table == null || table.Count < 5 ? Vector3.Zero : new Vector3(table[2].Value, table[3].Value, table[4].Value);
        }
        Vector3 move = First("m_LocationAdjust");
        Vector3 turn = First("m_RotationAdjust") * MathF.Tau;
        if (move == Vector3.Zero && turn == Vector3.Zero)
            return null;
        // Unreal's FRotationMatrix (as ScaleRotationTranslation) from roll X, pitch Y, yaw Z.
        float sr = MathF.Sin(turn.X), cr = MathF.Cos(turn.X);
        float sp = MathF.Sin(turn.Y), cp = MathF.Cos(turn.Y);
        float sy = MathF.Sin(turn.Z), cy = MathF.Cos(turn.Z);
        var m = new Matrix4x4(
            cp * cy, cp * sy, sp, 0,
            sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp, 0,
            -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp, 0,
            move.X, move.Y, move.Z, 1);
        Matrix4x4 g = UnrealTransform.ToGltf(m);
        JsonArray Row(float a, float b, float c) => new JsonArray(a, b, c);
        return new JsonObject
        {
            ["x"] = Row(g.M11, g.M12, g.M13),
            ["y"] = Row(g.M21, g.M22, g.M23),
            ["z"] = Row(g.M31, g.M32, g.M33),
            ["origin"] = Row(g.M41, g.M42, g.M43),
        };
    }

    // An RvrClientEffect's modules (see the header), with its particle systems' assets added.
    private JsonObject ReadEffect(IMEPackage pcc, ExportEntry template, TextureCache textures, IReadOnlyDictionary<string, float> parameters)
    {
        var modules = new JsonArray();
        var left = new List<string>();
        foreach (ObjectProperty moduleRef in template.GetProperties().GetProp<ArrayProperty<ObjectProperty>>("m_lstModules")
            ?? Enumerable.Empty<ObjectProperty>())
        {
            if (!pcc.TryGetUExport(moduleRef.Value, out ExportEntry module))
                continue;
            PropertyCollection p = module.GetProperties();
            float F(string name, float fallback) => p.GetProp<FloatProperty>(name)?.Value ?? fallback;
            bool B(string name) => p.GetProp<BoolProperty>(name)?.Value == true;
            string N(string name) => p.GetProp<NameProperty>(name)?.Value.Instanced;
            var json = new JsonObject
            {
                ["tag"] = N("m_nmTag"),
                ["duration"] = F("m_fDuration", 0f),
                ["delay"] = F("m_fDelay", 0f),
                ["cooldown"] = B("m_bIsCoolDownModule"),
            };
            switch (module.ClassName)
            {
                case "RvrCEffectModuleEmitter":
                {
                    if (p.GetProp<ObjectProperty>("m_pParticleSystem") is not ObjectProperty systemRef
                        || !pcc.TryGetUExport(systemRef.Value, out ExportEntry system))
                        continue;
                    var given = ModuleParameters(p);
                    var all = new Dictionary<string, float>(parameters, StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, value) in given)
                        all[name] = value;
                    var notes = new List<string>();
                    var emitters = new JsonArray();
                    foreach (ParticleExport.Emitter emitter in ParticleExport.Read(pcc, system, notes, shared: true, all))
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
                    Console.WriteLine($"  particles {system.ObjectName.Instanced}: {emitters.Count} emitter(s)"
                        + (notes.Count > 0 ? $"; {string.Join("; ", notes)}" : ""));
                    json["type"] = "emitter";
                    json["draw_scale"] = F("m_fDrawScale", 1f);
                    json["end_on_instigator_destroy"] = B("m_bEndOnInstigatorDestroy");
                    json["parameters"] = new JsonObject(given.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value)));
                    json["me_particles"] = new JsonObject { ["system"] = system.InstancedFullPath, ["emitters"] = emitters };
                    _emitters += emitters.Count;
                    break;
                }
                case "RvrCEffectModuleLocation":
                    json["type"] = "location";
                    json["bone"] = p.GetProp<NameProperty>("m_sAttachment")?.Value.Instanced;
                    json["reference"] = p.GetProp<EnumProperty>("m_eReference")?.Value.Instanced;
                    json["attach"] = B("m_bAttach");
                    json["rotation"] = p.GetProp<EnumProperty>("m_eRotationTarget")?.Value.Instanced;
                    json["adjust"] = LocationAdjust(p);
                    break;
                case "RvrCEffectModuleLifetime":
                    json["type"] = "lifetime";
                    json["max"] = F("m_fMax", 0f);
                    break;
                case "RvrCEffectModuleCameraShake":
                    json["type"] = "camera_shake";
                    json["scale"] = F("ShakeScale", 1f);
                    json["radial"] = B("bRadialShake");
                    json["inner_radius"] = F("RadialShake_InnerRadius", 0f) * 0.01f;
                    json["outer_radius"] = F("RadialShake_OuterRadius", 0f) * 0.01f;
                    json["falloff"] = F("RadialShake_Falloff", 1f);
                    json["max_distance"] = F("m_fMaxDistance", 0f) * 0.01f;
                    json["shake"] = p.GetProp<ObjectProperty>("Shake") is ObjectProperty shakeRef && pcc.TryGetUExport(shakeRef.Value, out ExportEntry shake)
                        ? CameraShake(shake.GetProperties()) : null;
                    break;
                case "RvrCEffectModuleEffectsMaterial":
                    json["type"] = "effects_material";
                    json["effect"] = N("m_nmEffect");
                    json["parameters"] = new JsonObject(ModuleParameters(p).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value)));
                    break;
                case "RvrCEffectModuleSound":
                    json["type"] = "sound";
                    json["start"] = p.GetProp<ObjectProperty>("m_pStartEvent") is ObjectProperty start ? pcc.GetEntry(start.Value)?.InstancedFullPath : null;
                    json["stop"] = p.GetProp<ObjectProperty>("m_pStopEvent") is ObjectProperty stop ? pcc.GetEntry(stop.Value)?.InstancedFullPath : null;
                    break;
                default:
                    left.Add(module.ClassName.Replace("RvrCEffectModule", ""));
                    continue;
            }
            modules.Add(json);
        }
        Console.WriteLine($"  effect {template.ObjectName.Instanced}: {modules.Count} module(s)"
            + (left.Count > 0 ? $"; left out {string.Join(", ", left)}" : ""));
        return new JsonObject { ["template"] = template.InstancedFullPath, ["modules"] = modules };
    }

    // A module's m_aParameters: each named variable's face value.
    private static Dictionary<string, float> ModuleParameters(PropertyCollection p)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (StructProperty entry in p.GetProp<ArrayProperty<StructProperty>>("m_aParameters") ?? Enumerable.Empty<StructProperty>())
        {
            StructProperty parameter = entry.GetProp<StructProperty>("Parameter");
            string name = parameter?.GetProp<NameProperty>("Variable")?.Value.Instanced;
            if (name != null && name != "None")
                values[name] = parameter.GetProp<FloatProperty>("ValueFloat")?.Value ?? 0f;
        }
        return values;
    }

    // UE3's CameraShake: per-axis oscillations (amplitude, frequency) and their timing.
    private static JsonObject CameraShake(PropertyCollection p)
    {
        JsonObject Oscillator(StructProperty axis) => new()
        {
            ["amplitude"] = axis?.GetProp<FloatProperty>("Amplitude")?.Value ?? 0f,
            ["frequency"] = axis?.GetProp<FloatProperty>("Frequency")?.Value ?? 0f,
        };
        JsonObject Axes(string name, params string[] axes)
        {
            StructProperty group = p.GetProp<StructProperty>(name);
            var json = new JsonObject();
            foreach (string axis in axes)
                json[axis.ToLowerInvariant()] = Oscillator(group?.GetProp<StructProperty>(axis));
            return json;
        }
        StructProperty fov = p.GetProp<StructProperty>("FOVOscillation");
        return new JsonObject
        {
            ["rotation"] = Axes("RotOscillation", "Pitch", "Yaw", "Roll"),
            ["location"] = Axes("LocOscillation", "X", "Y", "Z"),
            ["fov"] = Oscillator(fov),
            ["duration"] = p.GetProp<FloatProperty>("OscillationDuration")?.Value ?? 0f,
            ["blend_in"] = p.GetProp<FloatProperty>("OscillationBlendInTime")?.Value ?? 0.1f,
            ["blend_out"] = p.GetProp<FloatProperty>("OscillationBlendOutTime")?.Value ?? 0.2f,
        };
    }

    // Writes an effects export: Effects/, ParticleAssets/, and MaterialTextures/.
    private void WriteEffects(string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        AddMaterialTextures();
        SharpGLTF.Schema2.ModelRoot model = _scene.ToGltf2(Program.GltfSettings);
        string dds = DdsTextures.Convert(model);
        OutputFiles.Write(outPath, temporary => model.SaveGLB(temporary, Program.GlbSettings));
        Console.WriteLine($"Wrote {outPath}: {_emitters} emitters, {_particleAssetKeys.Count} particle assets, {_materials.Count} materials; textures: {dds}.");
    }
}
