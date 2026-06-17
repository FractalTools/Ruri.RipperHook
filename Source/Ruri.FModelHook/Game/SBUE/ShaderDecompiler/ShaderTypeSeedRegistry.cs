using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Source-derived name catalogue for FShader subclasses — counterpart to
// `EngineUbMetadataRegistry` but keyed by `FShaderType::HashedName`
// (CityHash64WithSeed(UPPER(class_name), 0)) instead of UB-layout hash.
//
// Each seed JSON lives under `<EngineUbMetadata>/<EGame>/_ShaderType/
// <ClassName>_<HashedName:016X>_MetaData.json`. Generator: `_generator/
// gen_ub_metadata.py::emit_shader_type_seeds`. Format mirrors the engine
// UB seed schema — same `ConstantBuffer` (named `$Globals`) + `Textures`
// / `Samplers` / `Buffers` / `UAVs` shape, just keyed differently.
//
// Why a separate registry: the lookup KEY is different. Engine UBs key
// by `(UBName, LayoutHash)` taken from the cooked SRT. ShaderType keys
// by the single 64-bit `FShaderType::HashedName` baked into every cooked
// shader's TypeDependency entry. The hash math is also different
// (CityHash64WithSeed of UPPER-cased class name vs the UB-specific
// `(ConstantBufferSize, BindingFlags, hasStaticSlot)` + XOR-fold).
//
// Important caveat documented in each seed's Debug.Note: the seed's
// `ConstantBuffer.VectorParameters[].Index` values are SEQUENTIAL
// PLACEHOLDERS in source-declaration order, NOT the cook's real offsets.
// The cook assigns real `$Globals` offsets per DXC packing of the actual
// HLSL source, and that mapping isn't recoverable from C++ source alone.
// Downstream consumer (Pass180 / RuntimeSymbolReader) MUST reconcile the
// seed's name list against the cooked binary's
// `ParameterMapInfo.LooseParameterBuffers[].Parameters[]` (offset+size
// only, no names) before injecting names into the rewrite.
internal sealed class ShaderTypeSeedRegistry
{
    private readonly Dictionary<ulong, EngineUbMetadata> _byHash;
    // Secondary index keyed by class-name prefix (descending length) so
    // templated cook names like `TLightMapDensityPSFNoLightMapPolicy` can
    // fall back to the bare-template seed `TLightMapDensityPS`. UE's
    // `IMPLEMENT_*_SHADER_TYPE` uses `##`-concatenation to produce names
    // like `<Base>##<PolicyName>` for each policy specialisation. The
    // generator captures full ##-expanded names in `_HashToName.json` but
    // only emits per-CLASS seeds for the bare base (since the LAYOUT_FIELD
    // declarations live on the base, specialisations inherit them).
    private readonly List<(string Prefix, EngineUbMetadata Meta)> _byNamePrefixDesc;
    // Generator-side hash -> ShaderTypeName resolution. Populates the
    // cooked binary's empty `ShaderTypeName` field (export-side
    // `HashedNamesResolver` failed to resolve hashes due to a
    // path-not-found + buggy CityHash; both fixed but stableinfo.json
    // was written before the fixes). Pass180 uses this to enable the
    // prefix-fallback path for templated specialisations.
    private readonly Dictionary<ulong, string> _hashToName;

    public string SourceDirectory { get; }
    public int FileCount => _byHash.Count;
    public int HashToNameCount => _hashToName.Count;

    private ShaderTypeSeedRegistry(string sourceDir, Dictionary<ulong, EngineUbMetadata> byHash, Dictionary<ulong, string> hashToName)
    {
        SourceDirectory = sourceDir;
        _byHash = byHash;
        _hashToName = hashToName;
        // Sort by descending prefix length so longer matches win (avoids
        // `TBasePassPSF...` getting eaten by a shorter `TBase...` seed).
        _byNamePrefixDesc = byHash.Values
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .Select(m => (Prefix: m.Name, Meta: m))
            .OrderByDescending(t => t.Prefix.Length)
            .ToList();
    }

    public static ShaderTypeSeedRegistry Empty { get; } = new(string.Empty,
        new Dictionary<ulong, EngineUbMetadata>(),
        new Dictionary<ulong, string>());

    // Loads seeds from a directory tree, optionally narrowed by game folder.
    // Same priority pattern as `EngineUbMetadataRegistry.LoadForGame`:
    //   1. `<directory>/<gameVersionEnum>/_ShaderType/` (game-specific overrides)
    //   2. `<directory>/<base UE folder>/_ShaderType/`  (when `tryBaseFallback`)
    //   3. Recursive sweep under `<directory>` for any other `<X>_<hash>_MetaData.json`
    //      whose path contains `_ShaderType` (catches hand-organised dumps)
    public static ShaderTypeSeedRegistry LoadForGame(
        string? directory, string? gameVersionEnum, bool tryBaseFallback,
        Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[ShaderTypeSeed] Directory not set or missing: {directory ?? "<null>"} — $Globals/loose-param names will stay anonymous.");
            return Empty;
        }

        Dictionary<ulong, EngineUbMetadata> byHash = new();
        // Same version-scoped roots as the engine-UB registry (game-specific
        // folder + base UE major.minor folder, e.g. `5.4.4/`). The per-file
        // `/_ShaderType/` path filter below keeps this to ShaderType seeds.
        // Scoping to one engine version stops a cook's loose-param class from
        // pulling a stale LAYOUT_FIELD member list out of a different version.
        List<string> scanRoots = EngineUbMetadataRegistry.BuildScanRoots(directory, gameVersionEnum, tryBaseFallback);

        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<ulong, string> hashToName = new();
        int loaded = 0, skipped = 0;
        // Match the engine-UB loader's deserialiser options exactly so JSON
        // shape compat is guaranteed. ShaderParamType is decorated with
        // Newtonsoft's StringEnumConverter for the GUI side, but the seed
        // loader runs on System.Text.Json — without the explicit converter,
        // every "Type": "Float" / "Int" string would fail with JsonException.
        JsonSerializerOptions jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        foreach (string root in scanRoots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*_MetaData.json", SearchOption.AllDirectories))
            {
                if (!file.Replace('\\', '/').Contains("/_ShaderType/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenFiles.Add(Path.GetFullPath(file))) continue;
                if (!TryParseHashFromFilename(file, out ulong hash))
                {
                    skipped++;
                    continue;
                }
                if (byHash.ContainsKey(hash))
                {
                    // First-wins: scan order prefers game-specific overrides, so a
                    // later file with the same hash is silently dropped.
                    continue;
                }
                try
                {
                    string json = File.ReadAllText(file);
                    EngineUbMetadata? entry = JsonSerializer.Deserialize<EngineUbMetadata>(json, jsonOpts);
                    if (entry == null)
                    {
                        skipped++;
                        continue;
                    }
                    byHash[hash] = entry;
                    loaded++;
                }
                catch (Exception ex)
                {
                    logError?.Invoke($"[ShaderTypeSeed] {file}: parse failed — {ex.GetType().Name}: {ex.Message}");
                    skipped++;
                }
            }
        }

        // Load `_HashToName.json` from each scan root. Multiple roots may
        // contribute (game-specific overrides + base UE fallback); first-wins
        // on hash collision matches the per-class seed precedence.
        foreach (string root in scanRoots)
        {
            string indexPath = Path.Combine(root, root.EndsWith("_ShaderType", StringComparison.OrdinalIgnoreCase) ? "_HashToName.json" : Path.Combine("_ShaderType", "_HashToName.json"));
            // Also try a recursive sweep under the root for `_HashToName.json`
            // (so the top-level `<directory>` root catches both
            // `GAME_UE5_1/_ShaderType/_HashToName.json` and a hand-organised
            // copy at any other depth).
            List<string> indexCandidates = new();
            if (File.Exists(indexPath)) indexCandidates.Add(indexPath);
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "_HashToName.json", SearchOption.AllDirectories))
                {
                    if (!indexCandidates.Contains(f, StringComparer.OrdinalIgnoreCase)) indexCandidates.Add(f);
                }
            }
            catch { /* ignore missing dirs */ }

            foreach (string idx in indexCandidates)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(idx));
                    if (doc.RootElement.TryGetProperty("Entries", out JsonElement entries)
                        && entries.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty p in entries.EnumerateObject())
                        {
                            if (!ulong.TryParse(p.Name, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong h))
                            {
                                continue;
                            }
                            if (p.Value.ValueKind == JsonValueKind.String)
                            {
                                hashToName.TryAdd(h, p.Value.GetString() ?? string.Empty);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logError?.Invoke($"[ShaderTypeSeed] {idx}: hash-to-name parse failed — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[ShaderTypeSeed] Loaded {loaded} ShaderType seed(s){gameTag} from '{directory}' ({skipped} skipped); hash-to-name={hashToName.Count}. Scan roots: {string.Join(" -> ", scanRoots)}");
        return new ShaderTypeSeedRegistry(directory, byHash, hashToName);
    }

    public EngineUbMetadata? Lookup(ulong typeHash)
    {
        return _byHash.TryGetValue(typeHash, out EngineUbMetadata? meta) ? meta : null;
    }

    public bool TryLookup(string hashHex, out EngineUbMetadata meta)
    {
        meta = null!;
        if (string.IsNullOrWhiteSpace(hashHex)) return false;
        string s = hashHex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong hash))
        {
            return false;
        }
        if (_byHash.TryGetValue(hash, out EngineUbMetadata? found))
        {
            meta = found;
            return true;
        }
        return false;
    }

    // Resolve cook's `ShaderTypeHash` to its source-declared class name
    // via `_HashToName.json`. Returns null when the hash isn't indexed
    // (class not declared via IMPLEMENT_*_SHADER_TYPE in the engine root
    // we scanned).
    public string? ResolveTypeName(string hashHex)
    {
        if (string.IsNullOrWhiteSpace(hashHex)) return null;
        string s = hashHex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong h)) return null;
        return _hashToName.TryGetValue(h, out string? name) ? name : null;
    }

    // Three-tier lookup. First tries the exact `FShaderType::HashedName`
    // match (only hits for non-templated bases). Then resolves the cook's
    // hash to a source name via `_HashToName.json` (covers templated
    // specialisations even when the cook's `ShaderTypeName` is empty).
    // Finally falls back to longest-prefix string match on whichever name
    // we have so templated specialisations like
    // `TLightMapDensityPSFNoLightMapPolicy` (cooked) recover names from
    // the bare base seed `TLightMapDensityPS`. The base class's
    // `LAYOUT_FIELD(FShaderParameter, ...)` declarations are inherited by
    // every macro-instantiated specialisation, so the loose-parameter
    // names are the same across all policies.
    //
    // Returns true if ANY lookup succeeded; `matchedBy` describes which.
    public bool TryLookupWithFallback(string hashHex, string? cookedTypeName, out EngineUbMetadata meta, out string matchedBy)
    {
        meta = null!;
        matchedBy = string.Empty;
        if (TryLookup(hashHex, out meta))
        {
            matchedBy = "exact-hash";
            return true;
        }
        // Fill in cookedTypeName from the index when the export-side
        // ShaderTypeName is empty (Stage 19 root cause).
        string? effectiveName = !string.IsNullOrWhiteSpace(cookedTypeName)
            ? cookedTypeName
            : ResolveTypeName(hashHex);
        if (string.IsNullOrWhiteSpace(effectiveName)) return false;
        foreach (var (prefix, m) in _byNamePrefixDesc)
        {
            if (effectiveName.StartsWith(prefix, StringComparison.Ordinal))
            {
                meta = m;
                matchedBy = string.IsNullOrWhiteSpace(cookedTypeName)
                    ? $"hash-to-name+prefix-of:{prefix}"
                    : $"prefix-of:{prefix}";
                return true;
            }
        }
        return false;
    }

    // Filename convention: `<ClassName>_<HashedName:016X>_MetaData.json`.
    // Pulls the 16-hex-char token between the last `_` before `_MetaData`.
    private static bool TryParseHashFromFilename(string filePath, out ulong hash)
    {
        hash = 0;
        string name = Path.GetFileNameWithoutExtension(filePath);
        const string suffix = "_MetaData";
        if (!name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string trimmed = name[..^suffix.Length];
        int lastUnderscore = trimmed.LastIndexOf('_');
        if (lastUnderscore < 0) return false;
        string hashPart = trimmed[(lastUnderscore + 1)..];
        if (hashPart.Length != 16) return false;
        return ulong.TryParse(hashPart, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash);
    }
}
