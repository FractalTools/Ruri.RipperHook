namespace Ruri.UEShaderTpkDumper.Core;

public static class UbmtTables
{
    private static readonly Dictionary<string, int> Ue5_0_to_5_4 = new(StringComparer.Ordinal)
    {
        ["INVALID"] = 0,
        ["BOOL"] = 1,
        ["INT32"] = 2,
        ["UINT32"] = 3,
        ["FLOAT32"] = 4,
        ["TEXTURE"] = 5,
        ["SRV"] = 6,
        ["UAV"] = 7,
        ["SAMPLER"] = 8,
        ["RDG_TEXTURE"] = 9,
        ["RDG_TEXTURE_ACCESS"] = 10,
        ["RDG_TEXTURE_ACCESS_ARRAY"] = 11,
        ["RDG_TEXTURE_SRV"] = 12,
        ["RDG_TEXTURE_UAV"] = 13,
        ["RDG_BUFFER_ACCESS"] = 14,
        ["RDG_BUFFER_ACCESS_ARRAY"] = 15,
        ["RDG_BUFFER_SRV"] = 16,
        ["RDG_BUFFER_UAV"] = 17,
        ["RDG_UNIFORM_BUFFER"] = 18,
        ["NESTED_STRUCT"] = 19,
        ["INCLUDED_STRUCT"] = 20,
        ["REFERENCED_STRUCT"] = 21,
        ["RENDER_TARGET_BINDING_SLOTS"] = 22,
    };

    private static readonly Dictionary<string, int> Ue5_5_Plus = new(StringComparer.Ordinal)
    {
        ["INVALID"] = 0,
        ["BOOL"] = 1,
        ["INT32"] = 2,
        ["UINT32"] = 3,
        ["FLOAT32"] = 4,
        ["TEXTURE"] = 5,
        ["SRV"] = 6,
        ["UAV"] = 7,
        ["SAMPLER"] = 8,
        ["RDG_TEXTURE"] = 9,
        ["RDG_TEXTURE_ACCESS"] = 10,
        ["RDG_TEXTURE_ACCESS_ARRAY"] = 11,
        ["RDG_TEXTURE_SRV"] = 12,
        ["RDG_TEXTURE_NON_PIXEL_SRV"] = 13,        ["RDG_TEXTURE_UAV"] = 14,
        ["RDG_BUFFER_ACCESS"] = 15,
        ["RDG_BUFFER_ACCESS_ARRAY"] = 16,
        ["RDG_BUFFER_SRV"] = 17,
        ["RDG_BUFFER_UAV"] = 18,
        ["RDG_UNIFORM_BUFFER"] = 19,
        ["NESTED_STRUCT"] = 20,
        ["INCLUDED_STRUCT"] = 21,
        ["REFERENCED_STRUCT"] = 22,
        ["RENDER_TARGET_BINDING_SLOTS"] = 23,
        ["RESOURCE_COLLECTION"] = 24,    };

    public static IReadOnlyDictionary<string, int> ForVersion(int major, int minor)
        => (major == 5 && minor >= 5) ? Ue5_5_Plus : Ue5_0_to_5_4;

    public static int Resolve(IReadOnlyDictionary<string, int> table, string name)
    {
        if (table.TryGetValue(name, out int v)) return v;
        if (name == "RDG_TEXTURE_NON_PIXEL_SRV") return table["RDG_TEXTURE_SRV"];
        if (name == "RESOURCE_COLLECTION") return table["RENDER_TARGET_BINDING_SLOTS"];
        throw new KeyNotFoundException($"Unknown UBMT name '{name}' in active mapping (size={table.Count})");
    }

    public const int PointerAlign = 8;    public const int ArrayElemAlign = 16;    public const int StructAlign = 16;}
