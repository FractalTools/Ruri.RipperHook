namespace Ruri.UEShaderTpkDumper.Parser;

// Catalogue of every `SHADER_PARAMETER*` / `RDG_*ACCESS*` macro that can
// appear inside a `BEGIN_UNIFORM_BUFFER_STRUCT` block. Mirrors the
// `_MEMBER_MACROS` + `MACRO_INFO` tables in `gen_ub_metadata.py`.
//
// Each entry maps:
//   * `IsResource`: whether the macro emits a resource (Texture/SRV/UAV/...) that
//     occupies a pointer slot vs a numeric member that occupies its own size.
//   * `UbmtName`: the `EUniformBufferBaseType` slot name (without the
//     `UBMT_` prefix). Empty for numeric members because their UBMT comes from
//     the C++ type (FLOAT32 / INT32 / ...).
public readonly record struct MemberMacroInfo(bool IsResource, string UbmtName);

public static class MemberMacros
{
    public static readonly IReadOnlyDictionary<string, MemberMacroInfo> Catalog = new Dictionary<string, MemberMacroInfo>(StringComparer.Ordinal)
    {
        // Numeric / array
        ["SHADER_PARAMETER"]                              = new(false, ""),
        ["SHADER_PARAMETER_EX"]                           = new(false, ""),
        ["SHADER_PARAMETER_ARRAY"]                        = new(false, ""),
        ["SHADER_PARAMETER_ARRAY_EX"]                     = new(false, ""),
        ["SHADER_PARAMETER_SCALAR_ARRAY"]                 = new(false, ""),
        // Resources — sized like a pointer (8 bytes)
        ["SHADER_PARAMETER_TEXTURE"]                      = new(true,  "TEXTURE"),
        ["SHADER_PARAMETER_TEXTURE_ARRAY"]                = new(true,  "TEXTURE"),
        ["SHADER_PARAMETER_SRV"]                          = new(true,  "SRV"),
        ["SHADER_PARAMETER_SRV_ARRAY"]                    = new(true,  "SRV"),
        ["SHADER_PARAMETER_UAV"]                          = new(true,  "UAV"),
        ["SHADER_PARAMETER_UAV_ARRAY"]                    = new(true,  "UAV"),
        ["SHADER_PARAMETER_SAMPLER"]                      = new(true,  "SAMPLER"),
        ["SHADER_PARAMETER_SAMPLER_ARRAY"]                = new(true,  "SAMPLER"),
        ["SHADER_PARAMETER_RDG_TEXTURE"]                  = new(true,  "RDG_TEXTURE"),
        ["SHADER_PARAMETER_RDG_TEXTURE_ARRAY"]            = new(true,  "RDG_TEXTURE"),
        ["SHADER_PARAMETER_RDG_TEXTURE_SRV"]              = new(true,  "RDG_TEXTURE_SRV"),
        ["SHADER_PARAMETER_RDG_TEXTURE_SRV_ARRAY"]        = new(true,  "RDG_TEXTURE_SRV"),
        // UE 5.5+ added a dedicated UBMT slot. For pre-5.5 versions we fall
        // back to RDG_TEXTURE_SRV via the `Resolve()` helper.
        ["SHADER_PARAMETER_RDG_TEXTURE_NON_PIXEL_SRV"]    = new(true,  "RDG_TEXTURE_NON_PIXEL_SRV"),
        ["SHADER_PARAMETER_RDG_TEXTURE_UAV"]              = new(true,  "RDG_TEXTURE_UAV"),
        ["SHADER_PARAMETER_RDG_TEXTURE_UAV_ARRAY"]        = new(true,  "RDG_TEXTURE_UAV"),
        ["SHADER_PARAMETER_RDG_BUFFER_SRV"]               = new(true,  "RDG_BUFFER_SRV"),
        ["SHADER_PARAMETER_RDG_BUFFER_SRV_ARRAY"]         = new(true,  "RDG_BUFFER_SRV"),
        ["SHADER_PARAMETER_RDG_BUFFER_UAV"]               = new(true,  "RDG_BUFFER_UAV"),
        ["SHADER_PARAMETER_RDG_BUFFER_UAV_ARRAY"]         = new(true,  "RDG_BUFFER_UAV"),
        ["SHADER_PARAMETER_RDG_UNIFORM_BUFFER"]           = new(true,  "RDG_UNIFORM_BUFFER"),
        // Struct nesting/inclusion — emitted as a child layout walker
        ["SHADER_PARAMETER_STRUCT"]                       = new(false, "NESTED_STRUCT"),
        ["SHADER_PARAMETER_STRUCT_INCLUDE"]               = new(false, "INCLUDED_STRUCT"),
        ["SHADER_PARAMETER_STRUCT_REF"]                   = new(true,  "REFERENCED_STRUCT"),
        ["SHADER_PARAMETER_STRUCT_ARRAY"]                 = new(false, "NESTED_STRUCT"),
        // RDG access types — ShaderParameterStruct only (not legal inside
        // BEGIN_UNIFORM_BUFFER_STRUCT, but parse-robustness wins)
        ["RDG_BUFFER_ACCESS"]                             = new(true,  "RDG_BUFFER_ACCESS"),
        ["RDG_BUFFER_ACCESS_DYNAMIC"]                     = new(true,  "RDG_BUFFER_ACCESS"),
        ["RDG_BUFFER_ACCESS_ARRAY"]                       = new(true,  "RDG_BUFFER_ACCESS_ARRAY"),
        ["RDG_TEXTURE_ACCESS"]                            = new(true,  "RDG_TEXTURE_ACCESS"),
        ["RDG_TEXTURE_ACCESS_DYNAMIC"]                    = new(true,  "RDG_TEXTURE_ACCESS"),
        ["RDG_TEXTURE_ACCESS_ARRAY"]                      = new(true,  "RDG_TEXTURE_ACCESS_ARRAY"),
        ["RENDER_TARGET_BINDING_SLOTS"]                   = new(false, "RENDER_TARGET_BINDING_SLOTS"),
    };

    public static readonly string MacroNameRegex = "(?:"
        + string.Join("|", Catalog.Keys.Select(System.Text.RegularExpressions.Regex.Escape))
        + ")";
}
