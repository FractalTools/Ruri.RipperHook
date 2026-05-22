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
        // Exact-type first, RDG-aliased fallback. Prefer exact match.
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

    // Context-aware ordered resolver. When the (UbmtKind, HlslType) pair
    // isn't globally unique, narrow candidates to engine UBs the shader
    // actually USES (from cbuffer declarations). If EXACTLY ONE such UB
    // contributes resources of this type AND the anonymous-slot count is
    // ≤ that UB's resource count for the type, take the FIRST N names in
    // declaration order. UE's shader compiler is documented to emit UB
    // resource declarations in source order, so the prefix-subset
    // assumption holds for the vast majority of shaders. Worst-case
    // mismatch: a shader that uses a NON-PREFIX subset of resources gets
    // labelled with the wrong volumetric/distance-field name — still
    // better than `h<hash>_T<N>` for the user's "real plaintext names"
    // bar.
    //
    // shaderUsedUbs: lowercased set of cbuffer type names declared in
    // the shader (e.g. "view", "translucentbasepass").
    // expectedAnonCount: number of anonymous slots of this exact type
    // the shader has.
    // Returns the ordered list of resource names (first expectedAnonCount
    // entries), or null when no clear single-UB owner exists.
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

        // First try with the exact UbmtType (typical case). If that returns
        // null because no UB matches OR none of the matches are RDG-only,
        // also try with the RDG-aliased UbmtType (e.g. DBufferATexture is
        // UBMT_RDG_TEXTURE in seeds, while the shader's `Texture2D` on a
        // t-register classifies as UBMT_TEXTURE). Always prefer the exact-
        // type result when both succeed — the seed's UbmtType is the source
        // of truth for non-ambiguous cases.
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
        // PREFIX-SUBSET RULE: anon count must be ≤ UB resource count.
        // Caller scopes shaderUsedUbs to UBs DECLARED AS CBUFFERS in the
        // shader source — those are per-shader-bound and the cook only
        // emits the resources the shader actually references, IN
        // declaration order. So taking the first N is the right subset
        // for Shader-bound UBs (View, LandscapeParameters, etc.).
        // STATIC-bound UBs (OpaqueBasePass without a cbuffer declaration)
        // are excluded by the caller and routed through usage-pattern
        // matching instead — see ApplyUsagePatternMatches.
        if (expectedAnonCount > ordered.Count) return null;
        ownerUbName = ub;
        if (expectedAnonCount == ordered.Count) return ordered;
        return ordered.GetRange(0, expectedAnonCount);
    }

    private static Dictionary<string, List<string>>? _orderedByUbAndType;

    // Build (ubName + UbmtKind + ShaderType) -> [resource name in declaration
    // order]. Declaration order = the order the resources appear in the UB
    // metadata's "Resources" list (which the TPK dumper writes in member-
    // declaration order, sorted by offset).
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
                // RDG resources (DBufferATexture etc.) often come from the
                // TPK dumper WITHOUT a ShaderType (gap). Default the HLSL
                // type from UbmtType so they're indexable. The UbmtType is
                // kept ORIGINAL (UBMT_RDG_TEXTURE etc.) — lookup-side
                // coalesce decides when to also try the non-RDG alias.
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

    // Suggest a default HLSL type for RDG entries that the TPK dumper
    // didn't record a ShaderType for. RDG resources appear in HLSL
    // identically to their non-RDG cousins. Most RDG textures default to
    // Texture2D (dominant cooked shape); outliers (cube, 3D, array) need
    // explicit ShaderType in the dumper.
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

    // Maps a shader-side UBMT classification (returned by Pass200's
    // ClassifyUbmtFromHlslType) to the RDG variant that the engine seeds
    // may carry the same resource under. Used at LOOKUP time so a shader
    // `Texture2D` on a t-register can find RDG-flavoured engine resources
    // (DBufferATexture etc.) without polluting the primary UBMT_TEXTURE
    // candidate set for resources that genuinely are not RDG.
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
                if (string.IsNullOrWhiteSpace(resName) || string.IsNullOrWhiteSpace(ubmt)) continue;
                // Same default-ShaderType for RDG entries — see TryIngestOrdered.
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
        catch { /* tolerate one bad file */ }
    }

    // UE source macros sometimes omit the template argument
    // (`SHADER_PARAMETER_TEXTURE(Texture3D, Name)`) but the HLSL emitter
    // always materialises one (`Texture3D<float4> Name`). To make the
    // index match the cook's HLSL declarations, emit BOTH forms when
    // ingesting: the original token AND a `<float4>`-defaulted variant
    // for bare Texture/RWTexture/Buffer types. This is benign — types
    // that already have an explicit `<...>` only emit the original.
    private static IEnumerable<string> NormalizeShaderType(string st)
    {
        yield return st;
        if (st.Contains('<', StringComparison.Ordinal)) yield break;
        // Default-template suffix for typed textures + buffers.
        // SamplerState / SamplerComparisonState are typeless — no suffix.
        if (st.StartsWith("Texture", StringComparison.Ordinal)
            || st.StartsWith("RWTexture", StringComparison.Ordinal)
            || st == "Buffer"
            || st == "RWBuffer")
        {
            yield return st + "<float4>";
        }
    }
}
