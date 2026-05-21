using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Builds a once-per-process lookup: HLSL type signature (e.g.
// `Texture3D<uint4>`) -> [UB name + resource name]. When the lookup
// returns exactly ONE entry, the cooked anonymous slot of that exact
// type can be confidently renamed to the real source name (e.g.
// `View_VolumetricLightmapIndirectionTexture`). When >1 entry, the
// type isn't uniquely identifying — caller falls back to the hash-
// tagged form.
//
// Source: the regenerated engine UB metadata JSONs under
// <exeDir>/EngineUbMetadata/<version>/<UB>_<hash>_MetaData.json.
// Each resource's `ShaderType` field (added by Stage 49 TPK dumper)
// carries the original macro type token from
// `SHADER_PARAMETER_TEXTURE(<type>, <name>)` etc.
//
// Lookup key includes the UBMT kind so SRV/UAV/Texture/Sampler resources
// with the same HLSL type don't collide (e.g. `Texture2D<float4>` exists
// both as plain texture and as SRV of texture).
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
        string key = $"{ubmtKind}|{shaderType}";
        if (_byType!.TryGetValue(key, out List<TypedResource>? list) && list.Count == 1)
        {
            ubName = list[0].UbName;
            resourceName = list[0].ResourceName;
            return true;
        }
        return false;
    }

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
                    // Skip the per-ShaderType seeds — they're keyed
                    // differently and don't carry engine UB resources.
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
                if (string.IsNullOrWhiteSpace(resName) || string.IsNullOrWhiteSpace(ubmt) || string.IsNullOrWhiteSpace(st)) continue;
                string key = $"{ubmt}|{st}";
                if (!built.TryGetValue(key, out List<TypedResource>? list))
                {
                    list = new List<TypedResource>();
                    built[key] = list;
                }
                // Dedup: don't double-count the same (UB, name) pair appearing
                // in multiple version files. We treat (UbName, ResourceName)
                // as the identity — if the same UB has the same resource in
                // two engine versions, that's still ONE candidate for the
                // type lookup.
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
        catch { /* tolerate one bad file */ }
    }
}
