using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LexExport;

// Translates a material's compiled base-pass pixel shader (DX11 assembly, from
// ShaderAnalysis.Disassemble) into Godot shading language, so a level surface draws exactly as
// Mass Effect drew it over its original lightmaps: its own specular, cubemap reflections,
// parallax, detail normals, and emissive. Cooking strips the material graphs, but the
// compiled shaders keep all of it, and the instruction set they use is small (arithmetic, dot
// products, texture samples, compares, discard).
//
// The result is the body of a Godot fragment(), with the registers as vec4 locals, plus the
// uniforms it samples. The game's material wrapper (script and shader include) supplies
// what the game's vertex shader would have, under the fixed names Input() lists, in Unreal's
// axes and units, and the dx_* helpers for the bitwise compare results.
public static class ShaderTranslator
{
    // Lit materials draw with one of the two lightmap policies; unlit ones (holo signs, sky
    // traffic, glass) with the no-lightmap policy. Lit meshes ME1 didn't lightmap (static
    // lockers, the elevators) drew with their light environment's policy: one dominant light
    // with diffuse and specular, plus second-order spherical harmonics for the rest. Distortion
    // is a material's distortion pass (ME3's effects: the biotic ripple), which writes screen
    // offsets UE3 accumulates and then applies to the scene behind.
    public enum Policy { TextureLightmap, VertexLightmap, Unlit, LightEnvironment, Distortion }

    // The base-pass shader each policy draws with (without sky light).
    public static string ShaderType(Policy policy) => policy switch
    {
        Policy.TextureLightmap => "TBasePassPixelShaderFDirectionalLightMapTexturePolicyNoSkyLight",
        Policy.VertexLightmap => "TBasePassPixelShaderFDirectionalVertexLightMapPolicyNoSkyLight",
        Policy.LightEnvironment => "TBasePassPixelShaderFSHLightLightMapPolicyNoSkyLight",
        Policy.Distortion => "TDistortionMeshPixelShader<FDistortMeshAccumulatePolicy>",
        _ => "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight",
    };

    // The key each policy's translation goes under in the material extras.
    public static string Key(Policy policy) => policy switch
    {
        Policy.TextureLightmap => "texture",
        Policy.VertexLightmap => "vertex",
        Policy.LightEnvironment => "environment",
        Policy.Distortion => "distortion",
        _ => "unlit",
    };

    // How a material texture is sampled: sRGB decoding and wrapping. Black: a texture the
    // level can't carry (a realtime reflection's render target), which samples as black.
    public record TextureInfo(bool Srgb, bool Wrap, bool Black = false);

    // Uniforms: the declarations Code needs. The slot lists say which material textures
    // (Texture2D_<n>, TextureCube_<n>) and uniform values (UniformPixelVector_<n>,
    // UniformPixelScalar_<n>) it reads, so the caller can supply them. CutoutTextures: the
    // textures whose alpha decides a discard (a masked material's opacity mask), whose mips the
    // Godot side rebuilds so the cutout keeps its coverage at a distance.
    public record Translation(string Uniforms, string Code, SortedSet<int> Textures, SortedSet<int> Cubes,
        SortedSet<int> Vectors, SortedSet<int> Scalars, SortedSet<int> CutoutTextures);

    public static Translation Translate(string asm, Policy policy, Func<int, TextureInfo> textureInfo,
        Func<int, TextureInfo> cubeInfo, out string failure)
    {
        try
        {
            failure = null;
            return new Translator(asm, policy).Run(textureInfo, cubeInfo);
        }
        catch (NotSupportedException ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    // What the Godot side names each pixel shader input, by semantic. The texture-lightmap
    // policy reads its atlas UVs from TEXCOORD0 and the material UVs from TEXCOORD1; the
    // vertex-lightmap policy has the material UVs in TEXCOORD0 and the lightmap samples
    // (already decoded and scaled) in TEXCOORD2 and 3; the light-environment policy has its
    // dominant light's direction in tangent space in TEXCOORD4. TEXCOORD5 is the pixel's position: in
    // world space (relative to the camera) for shaders that use world positions, which is when
    // they read CameraWorldPos, and otherwise in clip space (screen position and depth).
    private static string Input(Policy policy, string semantic, int index, bool worldPosition) => (semantic, index) switch
    {
        ("TEXCOORD", 10) => "in_tangent_x",
        ("TEXCOORD", 11) => "in_tangent_z",
        ("COLOR", 0) => "in_color",
        ("TEXCOORD", 0) => policy == Policy.TextureLightmap ? "in_lightmap_uv" : "in_uv",
        ("TEXCOORD", 1) when policy == Policy.TextureLightmap => "in_uv",
        ("TEXCOORD", 2) when policy == Policy.VertexLightmap => "in_lightmap_color",
        ("TEXCOORD", 3) when policy == Policy.VertexLightmap => "in_lightmap_directional",
        // Translucent surfaces get the vertex shader's height fog: in-scattered color, then
        // how much of the surface shows through. Lit ones (decals) read it over a lightmap.
        ("TEXCOORD", 4) when policy is Policy.Unlit or Policy.TextureLightmap or Policy.VertexLightmap => "in_fog",
        ("TEXCOORD", 4) when policy == Policy.LightEnvironment => "in_light_vector",
        ("TEXCOORD", 5) => worldPosition ? "in_world_position" : "in_screen_position",
        ("TEXCOORD", 6) => "in_camera_vector",
        ("TEXCOORD", 7) => "in_sky_vector",
        ("SV_IsFrontFace", 0) => "in_front_face",
        _ => null,
    };

    private sealed class Translator
    {
        private static readonly Regex SignatureRow = new(@"^//\s+(\w+)\s+(\d+)\s+[xyzw]+\s+(\d+)\s+\w+\s+\w+");
        private static readonly Regex CbufferField = new(@"^//\s+\w+\s+(\w+)(\[\d+\])?;\s*// Offset:\s+(\d+)\s+Size:\s+(\d+)");
        private static readonly Regex Binding = new(@"^//\s+(\S+)\s+texture\s+\S+\s+(\S+)\s+t(\d+)\s");
        private static readonly Regex Operand = new(@"^(?<neg>-)?(?<abs>\|)?(?<reg>[a-z]+)(?<num>\d+)?(\[(?<idx>\d+)\])?(\.(?<swz>[xyzw]+))?\|?$");

        private readonly string[] _lines;
        private readonly Policy _policy;
        // Input register -> the Godot input it reads.
        private readonly Dictionary<int, string> _inputs = new();
        // cbuffer fields: name, byte offset, size.
        private readonly List<(string Name, int Offset, int Size)> _fields = new();
        // Shader resource register -> sampler uniform, and whether it's a cubemap.
        private readonly Dictionary<int, (string Name, bool Cube)> _resources = new();

        private readonly SortedSet<int> _textures = new(), _cubes = new(), _vectors = new(), _scalars = new();
        private readonly SortedSet<int> _usedInputs = new(), _outputs = new();
        private int _temps;
        // The texture channels (slot, channel) each register component's value derives from,
        // through any arithmetic but not through sample coordinates, by "r0.x". A discard
        // names the alpha channels among its operand's in _cutouts.
        private readonly Dictionary<string, HashSet<(int Slot, char Channel)>> _flow = new();
        private readonly SortedSet<int> _cutouts = new();
        // Whether the shader reads CameraWorldPos (so TEXCOORD5 is a world position), and
        // whether it samples the scene depth (soft edges where it meets geometry).
        private bool _worldPosition, _sceneDepth;

        public Translator(string asm, Policy policy)
        {
            _lines = asm.Split('\n').Select(l => l.Trim()).ToArray();
            _policy = policy;
            string section = "";
            foreach (string line in _lines)
            {
                if (line.StartsWith("// Input signature"))
                    section = "input";
                else if (line.StartsWith("// Output signature"))
                    section = "output";
                else if (line.StartsWith("// Buffer Definitions"))
                    section = "cbuffer";
                else if (line.StartsWith("// Resource Bindings"))
                    section = "bindings";
                else if (line.Length > 0 && !line.StartsWith("//"))
                    break;

                Match m;
                if (section == "input" && (m = SignatureRow.Match(line)).Success)
                {
                    string name = Input(policy, m.Groups[1].Value, int.Parse(m.Groups[2].Value), _worldPosition);
                    if (name != null)
                        _inputs[int.Parse(m.Groups[3].Value)] = name;
                }
                else if (section == "cbuffer" && (m = CbufferField.Match(line)).Success)
                {
                    _fields.Add((m.Groups[1].Value, int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
                    // The buffer definitions come before the input signature.
                    if (m.Groups[1].Value == "CameraWorldPos" && !line.Contains("[unused]"))
                        _worldPosition = true;
                }
                else if (section == "bindings" && (m = Binding.Match(line)).Success)
                {
                    string name = m.Groups[1].Value;
                    int register = int.Parse(m.Groups[3].Value);
                    Match slot;
                    if ((slot = Regex.Match(name, @"^Texture2D_(\d+)$")).Success)
                        _resources[register] = ($"texture_{slot.Groups[1].Value}", false);
                    else if ((slot = Regex.Match(name, @"^TextureCube_(\d+)$")).Success)
                        _resources[register] = ($"cube_{slot.Groups[1].Value}", true);
                    else if (name == "LightMapTextures[0]")
                        _resources[register] = ("lightmap_color", false);
                    else if (name == "LightMapTextures[1]")
                        _resources[register] = ("lightmap_directional", false);
                    else if (name == "SceneDepthTexture")
                        _resources[register] = ("scene_depth_texture", false);
                    else
                        throw new NotSupportedException($"resource {name}");
                }
            }
        }

        public Translation Run(Func<int, TextureInfo> textureInfo, Func<int, TextureInfo> cubeInfo)
        {
            var body = new StringBuilder();
            bool started = false;
            foreach (string line in _lines)
            {
                if (!started)
                {
                    started = line.StartsWith("ps_");
                    continue;
                }
                if (line.Length == 0 || line.StartsWith("//"))
                    continue;
                if (line == "ret")
                    break;
                if (line.StartsWith("dcl_temps "))
                {
                    _temps = int.Parse(line["dcl_temps ".Length..]);
                    continue;
                }
                if (line.StartsWith("dcl_"))
                    continue;
                int space = line.IndexOf(' ');
                string opcode = space < 0 ? line : line[..space];
                List<string> operands = space < 0 ? new() : SplitOperands(line[(space + 1)..]);
                body.Append('\t').AppendLine(Instruction(opcode, operands));
            }
            if (!_outputs.Contains(0))
                throw new NotSupportedException("no color output");

            // Registers: temps start at zero, inputs come from the Godot side, outputs are locals
            // whose color the caller reads from o0.
            var code = new StringBuilder();
            code.AppendLine("\tvec4 tmp;");
            for (int r = 0; r < _temps; r++)
                code.AppendLine($"\tvec4 r{r} = vec4(0.0);");
            foreach (int v in _usedInputs)
                code.AppendLine($"\tvec4 v{v} = {_inputs[v]};");
            foreach (int o in _outputs)
                code.AppendLine($"\tvec4 o{o} = vec4(0.0);");
            code.Append(body);

            var uniforms = new StringBuilder();
            foreach (int t in _textures)
            {
                TextureInfo info = textureInfo(t);
                uniforms.AppendLine($"uniform sampler2D texture_{t}{Hints(info, "filter_linear_mipmap_anisotropic")};");
            }
            foreach (int c in _cubes)
                uniforms.AppendLine($"uniform samplerCube cube_{c}{Hints(cubeInfo(c) with { Wrap = true }, "filter_linear_mipmap")};");
            foreach (int v in _vectors)
                uniforms.AppendLine($"uniform vec4 pixel_vector_{v};");
            foreach (int s in _scalars)
                uniforms.AppendLine($"uniform float pixel_scalar_{s};");
            if (_sceneDepth)
                uniforms.AppendLine("uniform sampler2D scene_depth_texture : hint_depth_texture, filter_nearest;");
            return new Translation(uniforms.ToString(), code.ToString(), _textures, _cubes, _vectors, _scalars, _cutouts);
        }

        private static string Hints(TextureInfo info, string filter)
        {
            var hints = new List<string>();
            if (info.Srgb)
                hints.Add("source_color");
            if (info.Black)
                hints.Add("hint_default_black");
            hints.Add(filter);
            hints.Add(info.Wrap ? "repeat_enable" : "repeat_disable");
            return " : " + string.Join(", ", hints);
        }

        // One instruction as Godot statements: the result goes to tmp, then into the
        // destination's written components (a destination may also be a source).
        private string Instruction(string opcode, List<string> operands)
        {
            bool saturate = opcode.EndsWith("_sat");
            string op = saturate ? opcode[..^4] : opcode;
            string S(int i) => Source(operands[i]);
            Track(op, operands);

            if (op.StartsWith("sample"))
                return Assign(operands[0], Sample(op, operands), saturate);
            switch (op)
            {
                case "discard_nz":
                    return $"if (floatBitsToUint({S(0)}.x) != 0u) {{ discard; }}";
                case "discard_z":
                    return $"if (floatBitsToUint({S(0)}.x) == 0u) {{ discard; }}";
                case "sincos":
                {
                    string source = S(2);
                    var statements = new List<string>();
                    if (operands[0] != "null")
                        statements.Add(Assign(operands[0], $"sin({source})", saturate));
                    if (operands[1] != "null")
                        statements.Add(Assign(operands[1], $"cos({source})", saturate));
                    return string.Join(" ", statements);
                }
            }

            string value = op switch
            {
                "mov" => S(1),
                "add" => $"{S(1)} + {S(2)}",
                "mul" => $"{S(1)} * {S(2)}",
                "mad" => $"{S(1)} * {S(2)} + {S(3)}",
                "div" => $"{S(1)} / {S(2)}",
                "min" => $"min({S(1)}, {S(2)})",
                "max" => $"max({S(1)}, {S(2)})",
                "sqrt" => $"sqrt({S(1)})",
                "rsq" => $"inversesqrt({S(1)})",
                "log" => $"log2({S(1)})",
                "exp" => $"exp2({S(1)})",
                "frc" => $"fract({S(1)})",
                "round_ne" => $"roundEven({S(1)})",
                "round_ni" => $"floor({S(1)})",
                "round_pi" => $"ceil({S(1)})",
                "round_z" => $"trunc({S(1)})",
                "dp2" => $"vec4(dot({S(1)}.xy, {S(2)}.xy))",
                "dp3" => $"vec4(dot({S(1)}.xyz, {S(2)}.xyz))",
                "dp4" => $"vec4(dot({S(1)}, {S(2)}))",
                "lt" => $"dx_mask(lessThan({S(1)}, {S(2)}))",
                "ge" => $"dx_mask(greaterThanEqual({S(1)}, {S(2)}))",
                "eq" => $"dx_mask(equal({S(1)}, {S(2)}))",
                "ne" => $"dx_mask(notEqual({S(1)}, {S(2)}))",
                "movc" => $"dx_movc({S(1)}, {S(2)}, {S(3)})",
                "and" => $"dx_and({S(1)}, {S(2)})",
                "or" => $"dx_or({S(1)}, {S(2)})",
                "deriv_rtx" or "deriv_rtx_coarse" or "deriv_rtx_fine" => $"dFdx({S(1)})",
                "deriv_rty" or "deriv_rty_coarse" or "deriv_rty_fine" => $"dFdy({S(1)})",
                _ => throw new NotSupportedException($"instruction {opcode}"),
            };
            return Assign(operands[0], value, saturate);
        }

        // Follows texture channels through one instruction (see _flow). Each destination
        // component takes what the sources' matching components carry, a dot product takes all
        // of theirs, and a sample's components are its texture's channels, whatever its
        // coordinates (a parallax offset doesn't make a texture part of the mask).
        private void Track(string op, List<string> operands)
        {
            if (op is "discard_nz" or "discard_z")
            {
                foreach (var (slot, channel) in FlowOf(operands[0], 0))
                {
                    if (channel == 'w')
                        _cutouts.Add(slot);
                }
                return;
            }
            if (op.StartsWith("sample"))
            {
                Match t = Operand.Match(operands[2]);
                int slot = -1;
                if (t.Success && t.Groups["reg"].Value == "t"
                    && _resources.TryGetValue(int.Parse(t.Groups["num"].Value), out var resource)
                    && resource.Name.StartsWith("texture_"))
                    slot = int.Parse(resource.Name["texture_".Length..]);
                string swizzle = t.Success && t.Groups["swz"].Success ? Swizzle4(t.Groups["swz"].Value) : "xyzw";
                SetFlow(operands[0], i => slot < 0 ? [] : new[] { (slot, swizzle[i]) });
                return;
            }
            if (op == "sincos")
            {
                SetFlow(operands[0], i => FlowOf(operands[2], i));
                SetFlow(operands[1], i => FlowOf(operands[2], i));
                return;
            }
            int dot = op switch { "dp2" => 2, "dp3" => 3, "dp4" => 4, _ => 0 };
            List<string> sources = operands.Skip(1).ToList();
            SetFlow(operands[0], i => dot > 0
                ? sources.SelectMany(s => Enumerable.Range(0, dot).SelectMany(c => FlowOf(s, c)))
                : sources.SelectMany(s => FlowOf(s, i)));
        }

        // The texture channels a source operand's component carries.
        private IEnumerable<(int Slot, char Channel)> FlowOf(string operand, int component)
        {
            Match m = Operand.Match(operand);
            if (operand.StartsWith("l(") || !m.Success || m.Groups["reg"].Value is not ("r" or "o"))
                return [];
            string swizzle = m.Groups["swz"].Success ? Swizzle4(m.Groups["swz"].Value) : "xyzw";
            string key = $"{m.Groups["reg"].Value}{m.Groups["num"].Value}.{swizzle[component]}";
            return _flow.TryGetValue(key, out var flow) ? flow : [];
        }

        // Sets the texture channels of each written component, by its index (0-3).
        private void SetFlow(string destination, Func<int, IEnumerable<(int Slot, char Channel)>> component)
        {
            Match d = Operand.Match(destination);
            if (!d.Success || d.Groups["reg"].Value is not ("r" or "o"))
                return;
            string register = d.Groups["reg"].Value + d.Groups["num"].Value;
            string mask = d.Groups["swz"].Success ? d.Groups["swz"].Value : "xyzw";
            // Read every source before writing: a destination may also be a source.
            var flows = mask.Select(c => (Key: $"{register}.{c}", Flow: component("xyzw".IndexOf(c)).ToHashSet())).ToList();
            foreach (var (key, flow) in flows)
                _flow[key] = flow;
        }

        // A texture sample. The resource operand's swizzle picks the channels, aligned with the
        // destination's components like any source swizzle.
        private string Sample(string op, List<string> operands)
        {
            Match t = Operand.Match(operands[2]);
            if (!t.Success || t.Groups["reg"].Value != "t" || !_resources.TryGetValue(int.Parse(t.Groups["num"].Value), out var resource))
                throw new NotSupportedException($"sample from {operands[2]}");
            UseResource(resource.Name);
            string coord = Source(operands[1]) + (resource.Cube ? ".xyz" : ".xy");
            string swizzle = t.Groups["swz"].Success ? Swizzle4(t.Groups["swz"].Value) : "xyzw";
            string kind = op.Split('(')[0];
            // The scene depth, encoded so the shader's own decode (with MinZ_MaxZRatio) yields
            // the linear depth in centimeters, as the game's scene_depth() helper returns it.
            if (resource.Name == "scene_depth_texture")
            {
                _sceneDepth = true;
                return $"scene_depth(textureLod(scene_depth_texture, {coord}, 0.0).r, {coord}, inv_projection).{swizzle}";
            }
            string fetch = kind switch
            {
                "sample" or "sample_indexable" => $"texture({resource.Name}, {coord})",
                "sample_l" => $"textureLod({resource.Name}, {coord}, {Source(operands[4])}.x)",
                "sample_b" => $"texture({resource.Name}, {coord}, {Source(operands[4])}.x)",
                _ => throw new NotSupportedException($"instruction {kind}"),
            };
            return $"{fetch}.{swizzle}";
        }

        private void UseResource(string name)
        {
            if (name.StartsWith("texture_"))
                _textures.Add(int.Parse(name["texture_".Length..]));
            else if (name.StartsWith("cube_"))
                _cubes.Add(int.Parse(name["cube_".Length..]));
        }

        private string Assign(string destination, string value, bool saturate)
        {
            Match d = Operand.Match(destination);
            if (!d.Success || d.Groups["reg"].Value is not ("r" or "o"))
                throw new NotSupportedException($"destination {destination}");
            string register = d.Groups["reg"].Value + d.Groups["num"].Value;
            if (d.Groups["reg"].Value == "o")
                _outputs.Add(int.Parse(d.Groups["num"].Value));
            string mask = d.Groups["swz"].Success ? d.Groups["swz"].Value : "xyzw";
            if (saturate)
                value = $"clamp({value}, 0.0, 1.0)";
            return $"tmp = {value}; {register}.{mask} = tmp.{mask};";
        }

        // A source operand as a vec4 expression (wrapped so a swizzle can follow it).
        private string Source(string operand)
        {
            if (operand.StartsWith("l("))
                return Literal(operand);
            Match m = Operand.Match(operand);
            if (!m.Success)
                throw new NotSupportedException($"operand {operand}");
            string reg = m.Groups["reg"].Value;
            string swizzle = m.Groups["swz"].Success ? Swizzle4(m.Groups["swz"].Value) : "xyzw";
            int number = m.Groups["num"].Success ? int.Parse(m.Groups["num"].Value) : 0;
            string value;
            switch (reg)
            {
                case "r":
                case "o":
                    value = $"{reg}{number}.{swizzle}";
                    break;
                case "v":
                    if (!_inputs.ContainsKey(number))
                        throw new NotSupportedException($"input v{number}");
                    _usedInputs.Add(number);
                    value = $"v{number}.{swizzle}";
                    break;
                // PSOffsetConstants: the screen position's scale and bias (realtime reflections,
                // scene depth), and the scene depth's decode.
                case "cb" when number == 2 && m.Groups["idx"].Success && m.Groups["idx"].Value == "0":
                    value = $"screen_position_scale_bias.{swizzle}";
                    break;
                case "cb" when number == 2 && m.Groups["idx"].Success && m.Groups["idx"].Value == "1":
                    value = $"min_z_max_z_ratio.{swizzle}";
                    break;
                case "cb" when number == 0 && m.Groups["idx"].Success:
                {
                    int register = int.Parse(m.Groups["idx"].Value);
                    value = $"vec4({string.Join(", ", swizzle.Select(c => Constant(register, "xyzw".IndexOf(c))))})";
                    break;
                }
                default:
                    throw new NotSupportedException($"operand {operand}");
            }
            if (m.Groups["abs"].Success)
                value = $"abs({value})";
            if (m.Groups["neg"].Success)
                value = $"(-{value})";
            return $"({value})";
        }

        // A single-component swizzle replicates.
        private static string Swizzle4(string swizzle) => swizzle.Length == 1 ? new string(swizzle[0], 4) : swizzle.PadRight(4, swizzle[^1]);

        // One component of the constant buffer, by the field it falls in.
        private string Constant(int register, int component)
        {
            int at = register * 16 + component * 4;
            var field = _fields.FirstOrDefault(f => at >= f.Offset && at < f.Offset + f.Size);
            if (field.Name == null)
                throw new NotSupportedException($"constant cb0[{register}]");
            int element = (at - field.Offset) / 16;
            int c = (at - field.Offset) % 16 / 4;
            string xyzw = "xyzw";
            Match slot;
            if ((slot = Regex.Match(field.Name, @"^UniformPixelVector_(\d+)$")).Success)
            {
                _vectors.Add(int.Parse(slot.Groups[1].Value));
                return $"pixel_vector_{slot.Groups[1].Value}.{xyzw[c]}";
            }
            if ((slot = Regex.Match(field.Name, @"^UniformPixelScalar_(\d+)$")).Success)
            {
                _scalars.Add(int.Parse(slot.Groups[1].Value));
                return $"pixel_scalar_{slot.Groups[1].Value}";
            }
            // ME2 and ME3 pack four scalars a register: UniformPixelScalars_<k> holds scalars
            // 4k to 4k + 3.
            if ((slot = Regex.Match(field.Name, @"^UniformPixelScalars_(\d+)$")).Success)
            {
                int scalar = int.Parse(slot.Groups[1].Value) * 4 + c;
                _scalars.Add(scalar);
                return $"pixel_scalar_{scalar}";
            }
            return field.Name switch
            {
                "CameraWorldPos" => c < 3 ? $"camera_world_pos.{xyzw[c]}" : "0.0",
                "LightMapScale" => c < 3 ? $"lightmap_scale{element}.{xyzw[c]}" : "0.0",
                // Unreal packs matrices a row per register (row vectors); the Godot side keeps
                // them as column-vector mat3s, so register k component c is m[k][c].
                "WorldToLocalMatrix" => c < 3 && element < 3 ? $"world_to_local[{element}][{c}]" : "0.0",
                "LocalToWorldMatrix" => c < 3 && element < 3 ? $"local_to_world[{element}][{c}]" : "0.0",
                // The light environment's dominant light, and its harmonics: 9 coefficients
                // per color in 7 registers (the game packs them).
                "LightColorAndFalloffExponent" when _policy == Policy.LightEnvironment => c < 3 ? $"light_color.{xyzw[c]}" : "0.0",
                "WorldIncidentLighting" when _policy == Policy.LightEnvironment => $"world_incident_lighting[{element}].{xyzw[c]}",
                // The same screen-position scale and bias PSOffsetConstants carries elsewhere.
                "ScreenPositionScaleBias" => $"screen_position_scale_bias.{xyzw[c]}",
                // Without a lightmap (particles), a lit material adds its diffuse times the
                // ambient color: the Godot side runs the code with me_ambient 0 and 1 to split
                // the emissive from the diffuse, which Godot then lights.
                "AmbientColorAndSkyFactor" when _policy == Policy.Unlit => $"me_ambient.{xyzw[c]}",
                // Ambient and sky light aren't part of the original lighting; the view-space
                // matrix and the character mask only feed the second render target.
                // Translucency depth parameters only feed it too.
                "AmbientColorAndSkyFactor" or "UpperSkyColor" or "LowerSkyColor" or "WorldToViewMatrix" or "CharacterMask"
                    or "TranslucencyDepthParameters" => "0.0",
                // Decals discard everything when it's negative; their exported geometry is
                // already clipped to their frustum.
                "DecalFarPlaneDistance" => "1.0",
                _ => throw new NotSupportedException($"constant {field.Name}"),
            };
        }

        // l(1.0, 2.0, 3.0, 4.0) or l(5.0). Integer literals are bit patterns (masks, infinity).
        private static string Literal(string operand)
        {
            string[] parts = operand[2..^1].Split(',').Select(p => p.Trim()).ToArray();
            string[] values = parts.Select(LiteralComponent).ToArray();
            return values.Length == 1 ? $"vec4({values[0]})" : $"vec4({string.Join(", ", values)})";
        }

        private static string LiteralComponent(string text)
        {
            if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
            {
                float f = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return Float(f);
            }
            uint bits = text.StartsWith("0x")
                ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : unchecked((uint)int.Parse(text, CultureInfo.InvariantCulture));
            return bits == 0 ? "0.0" : $"uintBitsToFloat({bits}u)";
        }

        private static string Float(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
                return $"uintBitsToFloat({BitConverter.SingleToUInt32Bits(f)}u)";
            if (f == 0f)
                return "0.0";
            float magnitude = MathF.Abs(f);
            return magnitude >= 1e-4f && magnitude < 1e7f
                ? f.ToString("0.0########", CultureInfo.InvariantCulture)
                : f.ToString("0.0#######e+0", CultureInfo.InvariantCulture);
        }

        // Splits "r0.xyz, l(1, 2, 3, 4), t0.xyzw" at top-level commas.
        private static List<string> SplitOperands(string text)
        {
            var operands = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                    depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    operands.Add(text[start..i].Trim());
                    start = i + 1;
                }
            }
            operands.Add(text[start..].Trim());
            return operands;
        }
    }
}
