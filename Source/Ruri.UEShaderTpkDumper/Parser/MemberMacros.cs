namespace Ruri.UEShaderTpkDumper.Parser;

public readonly record struct MemberMacroInfo(bool IsResource, string UbmtName);

public static class MemberMacros
{
    public static readonly IReadOnlyDictionary<string, MemberMacroInfo> Catalog = new Dictionary<string, MemberMacroInfo>(StringComparer.Ordinal)
    {
        ["SHADER_PARAMETER"]                              = new(false, ""),
        ["SHADER_PARAMETER_EX"]                           = new(false, ""),
        ["SHADER_PARAMETER_ARRAY"]                        = new(false, ""),
        ["SHADER_PARAMETER_ARRAY_EX"]                     = new(false, ""),
        ["SHADER_PARAMETER_SCALAR_ARRAY"]                 = new(false, ""),
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
        ["SHADER_PARAMETER_RDG_TEXTURE_NON_PIXEL_SRV"]    = new(true,  "RDG_TEXTURE_NON_PIXEL_SRV"),
        ["SHADER_PARAMETER_RDG_TEXTURE_UAV"]              = new(true,  "RDG_TEXTURE_UAV"),
        ["SHADER_PARAMETER_RDG_TEXTURE_UAV_ARRAY"]        = new(true,  "RDG_TEXTURE_UAV"),
        ["SHADER_PARAMETER_RDG_BUFFER_SRV"]               = new(true,  "RDG_BUFFER_SRV"),
        ["SHADER_PARAMETER_RDG_BUFFER_SRV_ARRAY"]         = new(true,  "RDG_BUFFER_SRV"),
        ["SHADER_PARAMETER_RDG_BUFFER_UAV"]               = new(true,  "RDG_BUFFER_UAV"),
        ["SHADER_PARAMETER_RDG_BUFFER_UAV_ARRAY"]         = new(true,  "RDG_BUFFER_UAV"),
        ["SHADER_PARAMETER_RDG_UNIFORM_BUFFER"]           = new(true,  "RDG_UNIFORM_BUFFER"),
        ["SHADER_PARAMETER_STRUCT"]                       = new(false, "NESTED_STRUCT"),
        ["SHADER_PARAMETER_STRUCT_INCLUDE"]               = new(false, "INCLUDED_STRUCT"),
        ["SHADER_PARAMETER_STRUCT_REF"]                   = new(true,  "REFERENCED_STRUCT"),
        ["SHADER_PARAMETER_STRUCT_ARRAY"]                 = new(false, "NESTED_STRUCT"),
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
