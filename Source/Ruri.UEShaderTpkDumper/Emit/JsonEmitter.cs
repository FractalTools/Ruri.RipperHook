using System.Text.Json;
using Ruri.UEShaderTpkDumper.Core;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper.Emit;

// Writes per-UB `<Name>_<LayoutHash:08X>_MetaData.json` files matching the
// schema the runtime decompiler consumes. Shape mirrors what the Python
// generator emits (committed examples in EngineUbMetadata/GAME_UE5_X/).
//
// Keep the schema BYTE-IDENTICAL — the C# runtime side reads the JSON via
// System.Text.Json with PropertyNameCaseInsensitive=true, so casing
// differences are tolerated but field NAMES are not.
public static class JsonEmitter
{
    public static void EmitLayout(string outputDir, LayoutResult layout, uint layoutHash, string bindingFlagsName, IReadOnlyDictionary<string, int> ubmtTable, string engineVersion, string engineSourcePath)
    {
        Directory.CreateDirectory(outputDir);
        string fileName = $"{layout.BindingName}_{layoutHash:X8}_MetaData.json";
        string filePath = Path.Combine(outputDir, fileName);

        var obj = new Dictionary<string, object?>
        {
            ["Name"] = layout.BindingName,
            ["EngineVersion"] = engineVersion,
            ["EngineSource"] = engineSourcePath,
            ["LayoutHash"] = $"0x{layoutHash:X8}",
            ["BindingFlags"] = bindingFlagsName,
            ["ConstantBuffer"] = BuildConstantBuffer(layout),
            ["Textures"] = BuildTypedBucket(layout, kind: "TEXTURE"),
            ["Samplers"] = BuildSamplerBucket(layout),
            ["Buffers"] = BuildBufferBucket(layout),
            ["UAVs"] = BuildUavBucket(layout),
            ["Resources"] = BuildResourcesList(layout, ubmtTable),
        };

        JsonSerializerOptions opts = new()
        {
            WriteIndented = true,
            // Round-trip with the runtime loader's reader. Default options
            // would encode `<`/`>` in struct names; we want them literal.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        string json = JsonSerializer.Serialize(obj, opts) + "\n";
        File.WriteAllText(filePath, json);
    }

    private static Dictionary<string, object?> BuildConstantBuffer(LayoutResult layout)
    {
        var matrices = new List<Dictionary<string, object?>>();
        var vectors = new List<Dictionary<string, object?>>();

        foreach (NumericMember m in layout.NumericMembers)
        {
            var payload = new Dictionary<string, object?>
            {
                ["Name"] = m.Name,
                ["NameIndex"] = -1,
                ["Index"] = m.Offset,
                ["ArraySize"] = m.ArraySize,
                ["Type"] = m.HlslType.StartsWith("Float", StringComparison.Ordinal) ? "Float"
                         : m.HlslType.StartsWith("Int", StringComparison.Ordinal) ? "Int"
                         : m.HlslType.StartsWith("UInt", StringComparison.Ordinal) ? "UInt"
                         : m.HlslType.StartsWith("Bool", StringComparison.Ordinal) ? "Bool"
                         : m.HlslType.StartsWith("Half", StringComparison.Ordinal) ? "Half"
                         : "Float",
                ["RowCount"] = m.RowCount,
                ["ColumnCount"] = m.ColumnCount,
                ["IsMatrix"] = m.IsMatrix,
            };
            if (m.IsMatrix) matrices.Add(payload);
            else vectors.Add(payload);
        }

        return new Dictionary<string, object?>
        {
            ["Name"] = layout.BindingName,
            ["NameIndex"] = -1,
            ["MatrixParameters"] = matrices,
            ["VectorParameters"] = vectors,
            ["StructParameters"] = new List<object>(),
            ["Size"] = layout.Size,
            ["IsPartialCB"] = false,
        };
    }

    private static List<Dictionary<string, object?>> BuildTypedBucket(LayoutResult layout, string kind)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource r in layout.Resources)
        {
            if (!IsTextureUbmt(r.Ubmt)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = r.Name,
                ["NameIndex"] = -1,
                ["Index"] = r.ResourceIndex,
                ["SamplerIndex"] = -1,
                ["MultiSampled"] = false,
                ["Dim"] = 2,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildSamplerBucket(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource r in layout.Resources)
        {
            if (r.Ubmt != "SAMPLER") continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = r.Name,
                ["Sampler"] = r.ResourceIndex,
                ["BindPoint"] = r.ResourceIndex,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildBufferBucket(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource r in layout.Resources)
        {
            if (IsTextureUbmt(r.Ubmt) || r.Ubmt == "SAMPLER" || IsUavUbmt(r.Ubmt)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = r.Name,
                ["NameIndex"] = -1,
                ["Index"] = r.ResourceIndex,
                ["ArraySize"] = 0,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildUavBucket(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource r in layout.Resources)
        {
            if (!IsUavUbmt(r.Ubmt)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = r.Name,
                ["NameIndex"] = -1,
                ["Index"] = r.ResourceIndex,
                ["OriginalIndex"] = r.ResourceIndex,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildResourcesList(LayoutResult layout, IReadOnlyDictionary<string, int> ubmtTable)
    {
        // Canonical flat resource list, 1:1 with FRHIUniformBufferLayoutInitializer
        // .Resources[]. Sorted by (offset, ubmt) — the same order the layout
        // hash was computed over. This is the source of truth for SRT lookup.
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource r in layout.Resources)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["Index"] = r.ResourceIndex,
                ["Offset"] = r.Offset,
                ["Name"] = r.Name,
                ["UbmtType"] = "UBMT_" + r.Ubmt,
                // HLSL/CPP-side type signature (e.g. "Texture3D<uint4>",
                // "ByteAddressBuffer", "SamplerState"). Empty for macros
                // that don't carry one (RENDER_TARGET_BINDING_SLOTS).
                // Used at decompile time by the type-uniqueness rename
                // path: if the engine has exactly one resource matching
                // a given (UbmtType, ShaderType) pair, an anonymous slot
                // of that exact type can be confidently renamed to the
                // real UE source name.
                ["ShaderType"] = r.ShaderType,
            });
        }
        return list;
    }

    private static readonly HashSet<string> s_textureUbmts = new(StringComparer.Ordinal)
    {
        "TEXTURE", "RDG_TEXTURE", "RDG_TEXTURE_SRV", "RDG_TEXTURE_NON_PIXEL_SRV",
        "SRV",
    };
    private static readonly HashSet<string> s_uavUbmts = new(StringComparer.Ordinal)
    {
        "UAV", "RDG_TEXTURE_UAV", "RDG_BUFFER_UAV",
    };

    private static bool IsTextureUbmt(string ubmt) => s_textureUbmts.Contains(ubmt);
    private static bool IsUavUbmt(string ubmt) => s_uavUbmts.Contains(ubmt);
}
