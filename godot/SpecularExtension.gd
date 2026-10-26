@tool
extends GLTFDocumentExtension
class_name SpecularExtension

# ---------------------------------------------------------------
# SpecularExtension — reads KHR_materials_specular, which Godot's glTF importer skips.
#
# LexExport bakes each character's and prop's specular from ME1's compiled shader (ShaderBaker)
# into the extension's specularColorTexture: the color in RGB and the Phong power in alpha, as
# log2(power) / 8. Its specularColorFactor scales the color. Godot's materials have nowhere to
# keep either, so this hangs them on the imported material as metadata, which
# LightEnvironment's shader reads. Registered by the Level Import plugin (editor imports) and by
# the runtime loaders (CharacterAssets, LevelGeometry) before they parse a .glb.
# ---------------------------------------------------------------

const EXTENSION: String = "KHR_materials_specular"
## The material's specular map (Texture2D).
const TEXTURE_META: StringName = &"me_specular"
## What the map's color is multiplied by (Vector3).
const SCALE_META: StringName = &"me_specular_scale"

static var _registered: SpecularExtension = null


## Registers the extension with every GLTFDocument, once per process.
static func ensure_registered() -> void:
	if _registered == null:
		_registered = SpecularExtension.new()
		GLTFDocument.register_gltf_document_extension(_registered)


func _get_supported_extensions() -> PackedStringArray:
	return PackedStringArray([EXTENSION])


func _import_post_parse(state: GLTFState) -> Error:
	var materials_json: Array = state.json.get("materials", []) as Array
	var materials: Array[Material] = state.get_materials()
	var textures: Array[GLTFTexture] = state.get_textures()
	var images: Array[Texture2D] = state.get_images()
	for index: int in mini(materials_json.size(), materials.size()):
		var extensions: Dictionary = (materials_json[index] as Dictionary).get("extensions", {}) as Dictionary
		var specular: Dictionary = extensions.get(EXTENSION, {}) as Dictionary
		var texture_info: Dictionary = specular.get("specularColorTexture", {}) as Dictionary
		var texture_index: int = int(texture_info.get("index", -1))
		if texture_index < 0 or texture_index >= textures.size() or materials[index] == null:
			continue
		var image_index: int = textures[texture_index].src_image
		if image_index < 0 or image_index >= images.size():
			continue
		var factor: Array = specular.get("specularColorFactor", [1.0, 1.0, 1.0]) as Array
		materials[index].set_meta(TEXTURE_META, images[image_index])
		materials[index].set_meta(SCALE_META, Vector3(float(factor[0]), float(factor[1]), float(factor[2])))
	return OK
