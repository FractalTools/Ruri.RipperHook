using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class EngineTypeUniquenessIndex
{
    private static readonly object Lock = new();
    private static Dictionary<string, List<TypedResource>>? _byType;

    public readonly record struct TypedResource(string UbName, string ResourceName, string UbmtType);

    public static bool TryResolveUnique(string ubmtKind, string shaderType, out string ubName, out string resourceName)
    {
        ubName = string.Empty;
        resourceName = string.Empty;
        if (string.IsNullOrWhiteSpace(shaderType)) return false;
        EnsureBuilt();
        if (TryResolveUniqueInner(ubmtKind, shaderType, out ubName, out resourceName)) return true;
        string rdg = RdgAliasFor(ubmtKind);
        if (!string.IsNullOrEmpty(rdg) && TryResolveUniqueInner(rdg, shaderType, out ubName, out resourceName)) return true;
        return false;
    }

    private static bool TryResolveUniqueInner(string ubmtKind, string shaderType, out string ubName, out string resourceName)
    {
        ubName = string.Empty;
        resourceName = string.Empty;
        string key = $"{ubmtKind}|{shaderType}";
        if (_byType!.TryGetValue(key, out List<TypedResource>? list) && list.Count == 1)
        {
            ubName = list[0].UbName;
            resourceName = list[0].ResourceName;
            return true;
        }
        return false;
    }

    public static IReadOnlyList<string>? TryResolveOrderedByUbContext(
        string ubmtKind,
        string shaderType,
        IReadOnlySet<string> shaderUsedUbs,
        int expectedAnonCount,
        out string ownerUbName)
    {
        ownerUbName = string.Empty;
        if (string.IsNullOrWhiteSpace(shaderType) || expectedAnonCount <= 0) return null;
        EnsureBuilt();
        EnsureOrderedBuilt();

        IReadOnlyList<string>? exact = TryResolveOrderedByUbContextInner(ubmtKind, shaderType, shaderUsedUbs, expectedAnonCount, out ownerUbName);
        if (exact != null) return exact;
        string rdgAlias = RdgAliasFor(ubmtKind);
        if (string.IsNullOrEmpty(rdgAlias)) return null;
        return TryResolveOrderedByUbContextInner(rdgAlias, shaderType, shaderUsedUbs, expectedAnonCount, out ownerUbName);
    }

    private static IReadOnlyList<string>? TryResolveOrderedByUbContextInner(
        string ubmtKind,
        string shaderType,
        IReadOnlySet<string> shaderUsedUbs,
        int expectedAnonCount,
        out string ownerUbName)
    {
        ownerUbName = string.Empty;
        string key = $"{ubmtKind}|{shaderType}";
        if (!_byType!.TryGetValue(key, out List<TypedResource>? all)) return null;

        Dictionary<string, List<string>> byUb = new(StringComparer.Ordinal);
        foreach (TypedResource r in all)
        {
            if (!shaderUsedUbs.Contains(r.UbName.ToLowerInvariant())) continue;
            if (!byUb.TryGetValue(r.UbName, out List<string>? list))
            {
                list = new List<string>();
                byUb[r.UbName] = list;
            }
            list.Add(r.ResourceName);
        }
        if (byUb.Count != 1) return null;

        KeyValuePair<string, List<string>> entry = System.Linq.Enumerable.First(byUb);
        string ub = entry.Key;
        if (!_orderedByUbAndType!.TryGetValue($"{ub}|{key}", out List<string>? ordered)) return null;
        if (expectedAnonCount > ordered.Count) return null;
        ownerUbName = ub;
        if (expectedAnonCount == ordered.Count) return ordered;
        return ordered.GetRange(0, expectedAnonCount);
    }

    private static Dictionary<string, List<string>>? _orderedByUbAndType;

    private static void EnsureOrderedBuilt()
    {
        if (_orderedByUbAndType != null) return;
        lock (Lock)
        {
            if (_orderedByUbAndType != null) return;
            var built = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string exeDir = AppContext.BaseDirectory;
            string root = Path.Combine(exeDir, "EngineUbMetadata");
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*_MetaData.json", SearchOption.AllDirectories))
                {
                    string norm = file.Replace('\\', '/');
                    if (norm.Contains("/_ShaderType/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/_VertexFactoryType/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/_ShaderPipelineType/", StringComparison.OrdinalIgnoreCase)) continue;
                    TryIngestOrdered(file, built);
                }
            }
            _orderedByUbAndType = built;
        }
    }

    private static void TryIngestOrdered(string file, Dictionary<string, List<string>> built)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("Name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String) return;
            string ubName = nameEl.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(ubName)) return;
            if (!root.TryGetProperty("Resources", out JsonElement resources) || resources.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement r in resources.EnumerateArray())
            {
                string resName = r.TryGetProperty("Name", out JsonElement rn) && rn.ValueKind == JsonValueKind.String
                    ? rn.GetString() ?? string.Empty : string.Empty;
                string ubmt = r.TryGetProperty("UbmtType", out JsonElement ru) && ru.ValueKind == JsonValueKind.String
                    ? ru.GetString() ?? string.Empty : string.Empty;
                string st = r.TryGetProperty("ShaderType", out JsonElement rs) && rs.ValueKind == JsonValueKind.String
                    ? rs.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(resName) || string.IsNullOrWhiteSpace(ubmt)) continue;
                if (string.IsNullOrWhiteSpace(st)) st = DefaultShaderTypeForRdg(ubmt);
                if (string.IsNullOrWhiteSpace(st)) continue;
                foreach (string normSt in NormalizeShaderType(st))
                {
                    string key = $"{ubName}|{ubmt}|{normSt}";
                    if (!built.TryGetValue(key, out List<string>? list))
                    {
                        list = new List<string>();
                        built[key] = list;
                    }
                    if (!list.Contains(resName)) list.Add(resName);
                }
            }
        }
        catch { }
    }

    private static string DefaultShaderTypeForRdg(string ubmt) => ubmt switch
    {
        "UBMT_RDG_TEXTURE"        => "Texture2D",
        "UBMT_RDG_TEXTURE_SRV"    => "Texture2D<float4>",
        "UBMT_RDG_TEXTURE_UAV"    => "RWTexture2D<float4>",
        "UBMT_RDG_BUFFER_SRV"     => "Buffer<float4>",
        "UBMT_RDG_BUFFER_UAV"     => "RWBuffer<float4>",
        "UBMT_RDG_TEXTURE_ACCESS" => "Texture2D",
        "UBMT_RDG_BUFFER_ACCESS"  => "ByteAddressBuffer",
        _ => string.Empty,
    };

    private static string RdgAliasFor(string ubmt) => ubmt switch
    {
        "UBMT_TEXTURE" => "UBMT_RDG_TEXTURE",
        "UBMT_SRV"     => "UBMT_RDG_BUFFER_SRV",
        "UBMT_UAV"     => "UBMT_RDG_BUFFER_UAV",
        _ => string.Empty,
    };

    private static void EnsureBuilt()
    {
        if (_byType != null) return;
        lock (Lock)
        {
            if (_byType != null) return;
            var built = new Dictionary<string, List<TypedResource>>(StringComparer.Ordinal);
            string exeDir = AppContext.BaseDirectory;
            string root = Path.Combine(exeDir, "EngineUbMetadata");
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*_MetaData.json", SearchOption.AllDirectories))
                {
                    string norm = file.Replace('\\', '/');
                    if (norm.Contains("/_ShaderType/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/_VertexFactoryType/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.Contains("/_ShaderPipelineType/", StringComparison.OrdinalIgnoreCase)) continue;
                    TryIngest(file, built);
                }
            }
            _byType = built;
        }
    }

    private static void TryIngest(string file, Dictionary<string, List<TypedResource>> built)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("Name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String) return;
            string ubName = nameEl.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(ubName)) return;
            if (!root.TryGetProperty("Resources", out JsonElement resources) || resources.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement r in resources.EnumerateArray())
            {
                string resName = r.TryGetProperty("Name", out JsonElement rn) && rn.ValueKind == JsonValueKind.String
                    ? rn.GetString() ?? string.Empty : string.Empty;
                string ubmt = r.TryGetProperty("UbmtType", out JsonElement ru) && ru.ValueKind == JsonValueKind.String
                    ? ru.GetString() ?? string.Empty : string.Empty;
                string st = r.TryGetProperty("ShaderType", out JsonElement rs) && rs.ValueKind == JsonValueKind.String
                    ? rs.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(resName) || string.IsNullOrWhiteSpace(ubmt)) continue;
                if (string.IsNullOrWhiteSpace(st)) st = DefaultShaderTypeForRdg(ubmt);
                if (string.IsNullOrWhiteSpace(st)) continue;
                foreach (string normSt in NormalizeShaderType(st))
                {
                    string key = $"{ubmt}|{normSt}";
                    if (!built.TryGetValue(key, out List<TypedResource>? list))
                    {
                        list = new List<TypedResource>();
                        built[key] = list;
                    }
                    bool exists = false;
                    foreach (TypedResource existing in list)
                    {
                        if (string.Equals(existing.UbName, ubName, StringComparison.Ordinal)
                            && string.Equals(existing.ResourceName, resName, StringComparison.Ordinal))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists) list.Add(new TypedResource(ubName, resName, ubmt));
                }
            }
        }
        catch {}
    }

    private static IEnumerable<string> NormalizeShaderType(string st)
    {
        yield return st;
        if (st.Contains('<', StringComparison.Ordinal)) yield break;
        if (st.StartsWith("Texture", StringComparison.Ordinal)
            || st.StartsWith("RWTexture", StringComparison.Ordinal)
            || st == "Buffer"
            || st == "RWBuffer")
        {
            yield return st + "<float4>";
        }
    }
}
