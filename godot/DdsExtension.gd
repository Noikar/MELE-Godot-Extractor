@tool
extends GLTFDocumentExtension
class_name DdsExtension

# ---------------------------------------------------------------
# DdsExtension — reads MSFT_texture_dds, which Godot's glTF importer skips.
#
# LexExport writes every texture as DDS (Tools/LexExport/DdsTextures.cs): ME1's own
# block-compressed data and mips where it carries a texture unchanged, and our bakes encoded to
# BC1, BC3, or BC5. Godot's runtime glTF loader would decode PNGs to uncompressed RGBA without
# mipmaps instead (2.7 GB of video memory for the Wards). This points each texture at its DDS
# image and decodes that with Image.load_dds_from_buffer, so it reaches the GPU as stored. The
# editor's import unpacks the decoded images as PNGs and imports those as before. Registered
# with SpecularExtension: by the Level Import plugin (editor imports) and by the runtime
# loaders (CharacterAssets, LevelGeometry) before they parse a .glb.
# ---------------------------------------------------------------

const EXTENSION: String = "MSFT_texture_dds"
const MIME_TYPE: String = "image/vnd-ms.dds"

static var _registered: DdsExtension = null


## Registers the extension with every GLTFDocument, once per process.
static func ensure_registered() -> void:
	if _registered == null:
		_registered = DdsExtension.new()
		GLTFDocument.register_gltf_document_extension(_registered)


func _get_supported_extensions() -> PackedStringArray:
	return PackedStringArray([EXTENSION])


## The texture's image is the extension's source (glTF's own source would be a fallback).
func _parse_texture_json(_state: GLTFState, texture_json: Dictionary, ret_gltf_texture: GLTFTexture) -> Error:
	var dds: Dictionary = (texture_json.get("extensions", {}) as Dictionary).get(EXTENSION, {}) as Dictionary
	if dds.has("source"):
		ret_gltf_texture.src_image = int(dds["source"])
	return OK


## DDS images only; leaving ret_image empty lets Godot decode the rest itself.
func _parse_image_data(_state: GLTFState, image_data: PackedByteArray, mime_type: String, ret_image: Image) -> Error:
	if mime_type != MIME_TYPE:
		return OK
	return ret_image.load_dds_from_buffer(image_data)
