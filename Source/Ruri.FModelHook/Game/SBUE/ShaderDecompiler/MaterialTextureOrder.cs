using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

/// <summary>
/// 从材质的 <c>UniformExpressionSet.UniformTextureParameters</c> 抽出**材质贴图的声明序名表**。
///
/// 为什么这张表就是 t 寄存器序:UE 生成材质 HLSL 时按 <c>UniformTextureParameters</c> 的
/// **桶序(Standard2D → Cube → Array2D → ArrayCube → Volume → Virtual → External)+ 桶内下标**
/// 依次声明 <c>Material.Texture2D_&lt;i&gt;</c> / <c>Material.TextureCube_&lt;i&gt;</c> …,而 DXC 按声明序
/// 分配 <c>t</c> 寄存器。所以扁平化后的第 k 项 = 本 shader 第 k 个材质贴图槽 —— 这是编译器契约,
/// 不是启发式;Pass200 再拿已具名的槽当锚点做一次校验,对不上就整体不改名。
///
/// JSON 两种形态都要认(cooked runtime 把 FHashedMaterialParameterInfo 摊平成顶层字段,
/// editor/.uasset 保留嵌套的 FMaterialParameterInfo):
/// <code>
///   { "ParameterName": "Main_D", ... }
///   { "ParameterInfo": { "Name": "Main_D", ... }, ... }
/// </code>
/// </summary>
internal static class MaterialTextureOrder
{
    public static List<string> Extract(JsonElement uniformExpressionSet)
    {
        var names = new List<string>();
        if (uniformExpressionSet.ValueKind != JsonValueKind.Object) return names;
        if (!uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement buckets)
            || buckets.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (JsonElement bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement entry in bucket.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                names.Add(ReadName(entry) ?? $"Texture_{names.Count}");
            }
        }
        return names;
    }

    private static string? ReadName(JsonElement entry)
    {
        if (entry.TryGetProperty("ParameterName", out JsonElement direct)
            && direct.ValueKind == JsonValueKind.String)
        {
            string? value = direct.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value != "None") return value;
        }
        if (entry.TryGetProperty("ParameterInfo", out JsonElement info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("Name", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            string? value = nested.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value != "None") return value;
        }
        return null;
    }
}
