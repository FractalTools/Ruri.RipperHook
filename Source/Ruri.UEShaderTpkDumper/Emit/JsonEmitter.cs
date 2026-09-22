using System.Text.Json;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper.Emit;

/// <summary>
/// Writes one uniform buffer's seed: its members at their offsets, its resources in slot order,
/// the hash the engine stamps on that layout, and WHICH hash that is -- so a reader matching a
/// cook's hash knows whether the low byte is a static-slot index to look past or part of the
/// value to match exactly.
/// </summary>
public static class JsonEmitter
{
    public static void EmitLayout(string outputDir, LayoutResult layout, uint layoutHash, string bindingFlagsName,
        string hashFormula, string usageFlags, string engineVersion, string engineSourcePath)
    {
        Directory.CreateDirectory(outputDir);
        string fileName = $"{layout.BindingName}_{layoutHash:X8}_MetaData.json";
        string filePath = Path.Combine(outputDir, fileName);

        var payload = new Dictionary<string, object?>
        {
            ["Name"] = layout.BindingName,
            ["EngineVersion"] = engineVersion,
            ["EngineSource"] = engineSourcePath,
            ["LayoutHash"] = $"0x{layoutHash:X8}",
            ["HashFormula"] = hashFormula,
            ["BindingFlags"] = bindingFlagsName,
            ["UsageFlags"] = usageFlags,
            ["ConstantBuffer"] = BuildConstantBuffer(layout),
            ["Textures"] = BuildTypedBucket(layout),
            ["Samplers"] = BuildSamplerBucket(layout),
            ["Buffers"] = BuildBufferBucket(layout),
            ["UAVs"] = BuildUavBucket(layout),
            ["Resources"] = BuildResourcesList(layout),
        };

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(payload, options) + "\n");
    }

    private static Dictionary<string, object?> BuildConstantBuffer(LayoutResult layout)
    {
        var matrices = new List<Dictionary<string, object?>>();
        var vectors = new List<Dictionary<string, object?>>();

        foreach (NumericMember member in layout.NumericMembers)
        {
            var entry = new Dictionary<string, object?>
            {
                ["Name"] = member.Name,
                ["NameIndex"] = -1,
                ["Index"] = member.Offset,
                ["ArraySize"] = member.ArraySize,
                ["Type"] = member.HlslType.StartsWith("Float", StringComparison.Ordinal) ? "Float"
                         : member.HlslType.StartsWith("Int", StringComparison.Ordinal) ? "Int"
                         : member.HlslType.StartsWith("UInt", StringComparison.Ordinal) ? "UInt"
                         : member.HlslType.StartsWith("Bool", StringComparison.Ordinal) ? "Bool"
                         : "Float",
                ["RowCount"] = member.RowCount,
                ["ColumnCount"] = member.ColumnCount,
                ["IsMatrix"] = member.IsMatrix,
            };
            if (member.IsMatrix) matrices.Add(entry);
            else vectors.Add(entry);
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

    private static List<Dictionary<string, object?>> BuildTypedBucket(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource resource in layout.Resources)
        {
            if (!IsTextureUbmt(resource.Ubmt)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = resource.Name,
                ["NameIndex"] = -1,
                ["Index"] = resource.ResourceIndex,
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
        foreach (ResolvedResource resource in layout.Resources)
        {
            if (resource.Ubmt != "SAMPLER") continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = resource.Name,
                ["Sampler"] = resource.ResourceIndex,
                ["BindPoint"] = resource.ResourceIndex,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildBufferBucket(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource resource in layout.Resources)
        {
            if (IsTextureUbmt(resource.Ubmt) || resource.Ubmt == "SAMPLER" || IsUavUbmt(resource.Ubmt)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = resource.Name,
                ["NameIndex"] = -1,
                ["Index"] = resource.ResourceIndex,
                ["ArraySize"] = 0,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildUavBucket(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource resource in layout.Resources)
        {
            if (!IsUavUbmt(resource.Ubmt)) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = resource.Name,
                ["NameIndex"] = -1,
                ["Index"] = resource.ResourceIndex,
                ["OriginalIndex"] = resource.ResourceIndex,
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildResourcesList(LayoutResult layout)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (ResolvedResource resource in layout.Resources)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["Index"] = resource.ResourceIndex,
                ["Offset"] = resource.Offset,
                ["Name"] = resource.Name,
                ["UbmtType"] = "UBMT_" + resource.Ubmt,
                ["ShaderType"] = resource.ShaderType,
            });
        }
        return list;
    }

    private static readonly HashSet<string> TextureUbmts = new(StringComparer.Ordinal)
    {
        "TEXTURE", "RDG_TEXTURE", "RDG_TEXTURE_SRV", "RDG_TEXTURE_NON_PIXEL_SRV", "SRV",
    };

    private static readonly HashSet<string> UavUbmts = new(StringComparer.Ordinal)
    {
        "UAV", "RDG_TEXTURE_UAV", "RDG_BUFFER_UAV",
    };

    private static bool IsTextureUbmt(string ubmt) => TextureUbmts.Contains(ubmt);

    private static bool IsUavUbmt(string ubmt) => UavUbmts.Contains(ubmt);
}
