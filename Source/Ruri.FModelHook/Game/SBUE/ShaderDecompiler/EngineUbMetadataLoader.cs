using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CUE4Parse.UE4.Versions;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Loads engine-UB metadata JSONs from a directory and serves
// `(UBName, LayoutHash)` lookups. Filename convention enforced for
// O(1) hash-keyed dispatch even with hundreds of files; full directory
// scan only happens once at startup.
//
// Resolution is hash-first:
//   - If `(name, hash)` matches a loaded file: use it (canonical hit).
//   - Else if `name` matches at least one file but hash differs: log
//     a "shape drift" warning (engine version mismatch likely) and
//     return null so the caller emits a placeholder. Never emit a
//     wrong name silently.
//   - Else: return null (no metadata for this UB).
internal sealed class EngineUbMetadataRegistry
{
    private readonly Dictionary<(string Name, uint Hash), EngineUbMetadata> _byNameAndHash;
    private readonly Dictionary<string, List<uint>> _hashesByName;

    public string SourceDirectory { get; }
    public int FileCount => _byNameAndHash.Count;

    private EngineUbMetadataRegistry(string sourceDir, Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Dictionary<string, List<uint>> hashesByName)
    {
        SourceDirectory = sourceDir;
        _byNameAndHash = byNameAndHash;
        _hashesByName = hashesByName;
    }

    public static EngineUbMetadataRegistry Empty { get; } = new(string.Empty,
        new Dictionary<(string, uint), EngineUbMetadata>(),
        new Dictionary<string, List<uint>>(StringComparer.Ordinal));

    public static EngineUbMetadataRegistry Load(string? directory, Action<string>? log = null, Action<string>? logError = null)
        => LoadForGame(directory, gameVersionEnum: null, tryBaseFallback: true, log, logError);

    // Game-aware loader. When `gameVersionEnum` is set (e.g. "GAME_InfinityNikki"
    // or "GAME_UE5_4" as captured from FModel's EGame enum at export time):
    //   1. Loads from `<directory>/<gameVersionEnum>/` FIRST (game-specific overrides
    //      — modded UEs, project-specific UB layouts).
    //   2. When `tryBaseFallback` is true and the game enum doesn't already start
    //      with `GAME_UE`, loads from `<directory>/<base UE folder>/`
    //      (e.g. GAME_UE5_4 derived from GAME_InfinityNikki = GAME_UE5_4 + 2)
    //      — base UE layouts shared by all games on that engine version.
    //      Toggleable from the user's `ShaderDecompilerSettings.TryMatchBaseEngineVersion`:
    //      most games (~99%) don't customize CB layouts so this is virtually
    //      always correct and dramatically reduces manual seeding work; the
    //      flag exists to opt out for the rare modded engine where the base
    //      seeds would silently mis-name a drifted layout.
    //   3. Finally scans any other files under `<directory>` (recursive) for
    //      hand-organised metadata that doesn't follow the GAME_<X> convention.
    // Earlier sources take precedence on (Name, Hash) collision — the
    // game-specific folder wins over the base UE folder.
    //
    // When `gameVersionEnum` is null/empty, falls back to a single recursive
    // scan of `directory` (legacy behaviour) — every JSON is loaded with
    // no priority discrimination.
    public static EngineUbMetadataRegistry LoadForGame(string? directory, string? gameVersionEnum, bool tryBaseFallback = true, Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[EngineUbMetadata] Directory not set or missing: {directory ?? "<null>"} — engine UB members will stay anonymous.");
            return Empty;
        }

        Dictionary<(string, uint), EngineUbMetadata> byNameAndHash = new();
        Dictionary<string, List<uint>> hashesByName = new(StringComparer.Ordinal);
        int loaded = 0, skipped = 0;

        // Resolve the version-scoped roots (see `BuildScanRoots`). The shared
        // builder is also used by the sister registries (ShaderType / VF /
        // pipeline) so every symbol source scopes to the same engine version.
        List<string> scanRoots = BuildScanRoots(directory, gameVersionEnum, tryBaseFallback);

        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in scanRoots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*_MetaData.json", SearchOption.AllDirectories))
            {
                // Skip `_ShaderType/` sibling subtree — those are
                // ShaderType-keyed seeds owned by ShaderTypeSeedRegistry, not
                // engine UB seeds. They share the `*_MetaData.json` glob but
                // their `LayoutHash` field is always 0x00000000 (FShaderType
                // is hash-keyed by FHashedName, not by UB layout hash), so
                // loading them here pollutes the (Name, Hash) map with 0x0
                // entries and counts every one as "skipped" via the down-
                // stream null/zero validation in TryLoadFile.
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/_ShaderType/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenFiles.Add(Path.GetFullPath(file))) continue;
                if (TryLoadFile(file, byNameAndHash, hashesByName, logError)) loaded++;
                else skipped++;
            }
        }

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[EngineUbMetadata] Loaded {loaded} layout(s){gameTag} from '{directory}' ({skipped} skipped). Scan roots: {string.Join(" -> ", scanRoots)}");

        // Diagnostic: re-compute the hash from each loaded seed and compare to
        // its declared layoutHash. Surfaces seed files where the recorded
        // resource list / cbuffer size is internally inconsistent (i.e. the
        // values in the JSON would NOT reproduce the declared hash through
        // FRHIUniformBufferLayoutInitializer::ComputeHash). This is a strict
        // self-check on the seed authoring — never affects lookup behaviour.
        VerifySeedHashesForDiagnostics(byNameAndHash, log);

        return new EngineUbMetadataRegistry(directory, byNameAndHash, hashesByName);
    }

    // Mirrors `FRHIUniformBufferLayoutInitializer::ComputeHash` byte-for-byte
    // (UE 5.1 `RHIResources.h` / UE 5.4 `RHIUniformBufferLayoutInitializer.h`).
    // Inputs are exactly what the engine folds in: ConstantBufferSize (uint16
    // masked low bits), BindingFlags (uint8), `StaticSlot != INVALID` bit,
    // then for each resource MemberOffset (uint16) plus a 4/2/1 byte XOR fold
    // of MemberType consuming Resources from the END. Returns 0xFFFFFFFF if
    // any resource has an unknown UBMT_* type (silent for diagnostics — the
    // caller logs the mismatch).
    internal static uint ComputeLayoutHash(uint constantBufferSize, byte bindingFlags, bool hasStaticSlot, IReadOnlyList<EngineUbResourceSlot> resources, bool ue55Plus = false)
    {
        uint h = ((constantBufferSize & 0xFFFFu) << 16) | ((uint)bindingFlags << 8) | (uint)(hasStaticSlot ? 1 : 0);
        for (int i = 0; i < resources.Count; i++)
            h ^= (uint)(resources[i].Offset & 0xFFFFu);
        int n = resources.Count;
        // Consume from the END in groups of 4/2/1, matching the unrolled fold
        // in UE's source.
        while (n >= 4)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 0;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 8;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 16;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 24;
        }
        while (n >= 2)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 0;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 16;
        }
        while (n > 0)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF);
        }
        return h;
    }

    // EUniformBufferBaseType (RHIDefinitions.h). UE 5.5 inserted
    // UBMT_RDG_TEXTURE_NON_PIXEL_SRV at slot 13, shifting every later enum +1
    // and appending UBMT_RESOURCE_COLLECTION at 24. The diagnostic MUST use the
    // table for the seed's own engine version or every 5.5+ seed's self-check
    // would false-mismatch (the fold reads a wrong integer for the shifted
    // types). Mirrors Ruri.UEShaderTpkDumper.Core.UbmtTables.
    private static int UbmtValue(string typeName, bool ue55Plus)
    {
        // Types added in 5.5 carry their exact final ordinal directly and are
        // NOT subject to the generic shift below. Pre-5.5 they don't exist —
        // return the dumper's friendly aliases so a stray reference doesn't
        // hard-diverge the diagnostic.
        if (typeName == "UBMT_RDG_TEXTURE_NON_PIXEL_SRV") return ue55Plus ? 13 : 12; // ~ RDG_TEXTURE_SRV
        if (typeName == "UBMT_RESOURCE_COLLECTION")        return ue55Plus ? 24 : 22; // ~ RENDER_TARGET_BINDING_SLOTS

        int v = typeName switch
        {
            "UBMT_INVALID"                       => 0,
            "UBMT_BOOL"                          => 1,
            "UBMT_INT32"                         => 2,
            "UBMT_UINT32"                        => 3,
            "UBMT_FLOAT32"                       => 4,
            "UBMT_TEXTURE"                       => 5,
            "UBMT_SRV"                           => 6,
            "UBMT_UAV"                           => 7,
            "UBMT_SAMPLER"                       => 8,
            "UBMT_RDG_TEXTURE"                   => 9,
            "UBMT_RDG_TEXTURE_ACCESS"            => 10,
            "UBMT_RDG_TEXTURE_ACCESS_ARRAY"      => 11,
            "UBMT_RDG_TEXTURE_SRV"               => 12,
            "UBMT_RDG_TEXTURE_UAV"               => 13,
            "UBMT_RDG_BUFFER_ACCESS"             => 14,
            "UBMT_RDG_BUFFER_ACCESS_ARRAY"       => 15,
            "UBMT_RDG_BUFFER_SRV"                => 16,
            "UBMT_RDG_BUFFER_UAV"                => 17,
            "UBMT_RDG_UNIFORM_BUFFER"            => 18,
            "UBMT_NESTED_STRUCT"                 => 19,
            "UBMT_INCLUDED_STRUCT"               => 20,
            "UBMT_REFERENCED_STRUCT"             => 21,
            "UBMT_RENDER_TARGET_BINDING_SLOTS"   => 22,
            _ => -1, // unknown — hash will diverge, surfaces as MISMATCH in log
        };
        // 5.5 inserted NON_PIXEL_SRV at slot 13, shifting the original 13..22
        // members up by +1. Apply only to those existing members.
        if (ue55Plus && v >= 13) v += 1;
        return v;
    }

    private static byte BindingFlagsValue(string name) => name switch
    {
        "Shader" => 1,
        "Static" => 2,
        "StaticAndShader" => 3,
        _ => 1,
    };

    private static void VerifySeedHashesForDiagnostics(Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Action<string>? log)
    {
        if (log == null) return;
        int matched = 0, mismatched = 0;
        foreach (var kvp in byNameAndHash)
        {
            EngineUbMetadata meta = kvp.Value;
            uint declared = kvp.Key.Item2;
            byte bf = BindingFlagsValue(meta.BindingFlags);
            bool hasStaticSlot = string.Equals(meta.BindingFlags, "Static", StringComparison.Ordinal)
                              || string.Equals(meta.BindingFlags, "StaticAndShader", StringComparison.Ordinal);
            uint cbSize = (uint)meta.ConstantBufferSize;
            bool ue55Plus = SeedIsUe55Plus(meta.EngineVersion);
            uint computedAsIs = ComputeLayoutHash(cbSize, bf, hasStaticSlot, meta.Resources, ue55Plus);
            if (computedAsIs == declared)
            {
                matched++;
                continue;
            }
            // Probe: try common alignments (round up to 16) — if the seed
            // recorded the unaligned numeric end but the engine folded the
            // aligned C++ struct sizeof, this catches the off-by-padding.
            uint aligned16 = (cbSize + 15u) & ~15u;
            uint computedAligned = ComputeLayoutHash(aligned16, bf, hasStaticSlot, meta.Resources, ue55Plus);
            mismatched++;
            log($"[EngineUbMetadata][HashVerify] MISMATCH name={meta.Name} declared=0x{declared:X8} computed(cbsize={cbSize})=0x{computedAsIs:X8}  align16(cbsize={aligned16})=0x{computedAligned:X8}{(computedAligned == declared ? "  <- align16 reproduces declared" : "")}");
        }
        log($"[EngineUbMetadata][HashVerify] {matched} matched, {mismatched} mismatched of {byNameAndHash.Count} loaded seeds.");
    }

    // True when a seed's recorded EngineVersion ("5.5.4", "5.6.1", "5.7.4", …)
    // is UE 5.5 or later — picks the shifted UBMT enum table for the hash
    // self-check. Best-effort: an unparseable version defaults to pre-5.5.
    private static bool SeedIsUe55Plus(string? engineVersion)
    {
        if (string.IsNullOrWhiteSpace(engineVersion)) return false;
        string[] parts = engineVersion.Split('.');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0], out int major)
            && int.TryParse(parts[1], out int minor)
            && (major > 5 || (major == 5 && minor >= 5));
    }

    // Derives the base UE major.minor `EGame` name (e.g. "GAME_UE5_4")
    // for any game-specific value (e.g. "GAME_InfinityNikki") by enum
    // arithmetic — no hand-maintained string table.
    //
    // EGame encoding (CUE4Parse/UE4/Versions/EGame.cs:225-229):
    //   GameUe<F>Base = 0x<F>000000
    //   GAME_UE<F>_<X>     = GameUe<F>Base + (X << 16)   // base versions
    //   GAME_<GameSpecific> = GAME_UE<F>_<X> + n         // n=1..255 offset
    // Masking with 0xFFFF0000 keeps family + major.minor bits and drops
    // the per-game offset, yielding the parent base unchanged. Casting
    // back to EGame and ToString() round-trips to the base member name
    // because every game-specific value's parent is by construction a
    // declared base — and when CUE4Parse adds new entries this stays
    // correct automatically.
    private static bool TryDeriveBaseUeFromEGame(string gameVersionEnum, out string baseUeName)
    {
        baseUeName = string.Empty;
        if (!Enum.TryParse<EGame>(gameVersionEnum, ignoreCase: false, out EGame game)) return false;
        EGame baseValue = (EGame)((uint)game & 0xFFFF0000u);
        string asName = baseValue.ToString();
        if (!asName.StartsWith("GAME_UE", StringComparison.Ordinal)) return false;
        baseUeName = asName;
        return true;
    }

    // Same enum-arithmetic base-version derivation, exposed for sibling
    // registries that need to apply the GAME_<X> → GAME_UE<F>_<X> fallback
    // (e.g. `ShaderTypeSeedRegistry`). Kept as a thin pass-through wrapper
    // so the actual logic stays in one place.
    internal static bool TryDeriveBaseUeFromEGameForShaderTypes(string gameVersionEnum, out string baseUeName)
        => TryDeriveBaseUeFromEGame(gameVersionEnum, out baseUeName);

    // Shared scan-root resolver for every source-truth registry (engine UB,
    // ShaderType, VF/pipeline hash indexes). CRITICAL CORRECTNESS RULE: a cook
    // is ONE engine version, so seeds from OTHER versions must never enter the
    // lookup pool. Mixing versions lets a cook UB collide with a wrong-version
    // seed (same key, different numeric layout → dedup keeps an arbitrary one)
    // and silently emit a wrong member layout (e.g. a scalar declared as an
    // array). So when ANY version-scoped folder is found we return ONLY those
    // and DO NOT fall back to the load-everything recursive sweep.
    //
    // Folders are organised two ways in the wild:
    //   * `<EGame enum>/`     — e.g. `GAME_InfinityNikki/`, `GAME_UE5_4/`
    //   * `<X.Y.Z version>/`  — e.g. `5.4.4/` (what the TpkDumper emits)
    // We resolve both for the game's own enum and for its base UE major.minor.
    // Callers scan each returned root recursively with their own file glob.
    internal static List<string> BuildScanRoots(string directory, string? gameVersionEnum, bool tryBaseFallback)
    {
        List<string> scanRoots = new();
        bool foundVersionScoped = false;

        // 1. Game-specific enum folder (overrides for modded UEs / project UBs).
        if (!string.IsNullOrEmpty(gameVersionEnum))
        {
            string specific = Path.Combine(directory, gameVersionEnum);
            if (Directory.Exists(specific)) { scanRoots.Add(specific); foundVersionScoped = true; }
        }

        // 2. Base UE version folders (enum-named AND X.Y.Z-named). Loaded when
        //    the game enum already names a base UE (`GAME_UE5_4` IS the base),
        //    or when the base-fallback toggle is on for a game-specific
        //    derivative. Modded forks rarely re-shuffle unmodified leaf UBs
        //    (e.g. NiagaraMeshVF, IndirectLightingCache), so the matching
        //    same-version base seeds recover those safely — the layout hash
        //    still gates every apply, so a modded UB simply won't match.
        bool gameIsBaseUe = !string.IsNullOrEmpty(gameVersionEnum)
            && gameVersionEnum.StartsWith("GAME_UE", StringComparison.Ordinal);
        if ((gameIsBaseUe || tryBaseFallback)
            && TryGetEngineMajorMinor(gameVersionEnum, out int major, out int minor))
        {
            string baseEnumDir = Path.Combine(directory, $"GAME_UE{major}_{minor}");
            if (Directory.Exists(baseEnumDir) && !scanRoots.Contains(baseEnumDir))
            {
                scanRoots.Add(baseEnumDir);
                foundVersionScoped = true;
            }
            foreach (string verDir in EnumerateVersionStringFolders(directory, major, minor))
            {
                if (!scanRoots.Contains(verDir)) { scanRoots.Add(verDir); foundVersionScoped = true; }
            }
        }

        // 3. Last resort: when NOTHING version-scoped matched (unorganised drop,
        //    or an engine version we have no folder for), sweep the whole root
        //    so a flat install still works. Callers dedup, so this is safe.
        if (!foundVersionScoped) scanRoots.Add(directory);
        return scanRoots;
    }

    // Resolve the engine major.minor for a game enum. When the enum already
    // names a base UE (`GAME_UE5_4`) parse it directly; otherwise derive the
    // base via the masking helper above (`GAME_InfinityNikki` → `GAME_UE5_4`)
    // and parse that. Returns false when the enum is null/empty or doesn't
    // resolve to a `GAME_UE<major>_<minor>` form. Exposed internal so sibling
    // registries (ShaderType / VF / pipeline) can apply the same scoping.
    internal static bool TryGetEngineMajorMinor(string? gameVersionEnum, out int major, out int minor)
    {
        major = 0; minor = 0;
        if (string.IsNullOrEmpty(gameVersionEnum)) return false;
        string baseName = gameVersionEnum;
        if (!baseName.StartsWith("GAME_UE", StringComparison.Ordinal)
            && !TryDeriveBaseUeFromEGame(gameVersionEnum, out baseName))
        {
            return false;
        }
        const string prefix = "GAME_UE";
        if (!baseName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string rest = baseName.Substring(prefix.Length); // e.g. "5_4"
        int underscore = rest.IndexOf('_');
        if (underscore <= 0 || underscore >= rest.Length - 1) return false;
        return int.TryParse(rest.AsSpan(0, underscore), out major)
            && int.TryParse(rest.AsSpan(underscore + 1), out minor);
    }

    // Enumerate immediate subfolders of `directory` whose name is an X.Y or
    // X.Y.Z version string for the requested engine major.minor — e.g. for
    // (5, 4) this matches "5.4", "5.4.4", "5.4.10" but NOT "5.40". This is the
    // folder convention the TpkDumper emits (`<out-root>/<X.Y.Z>/`). Exposed
    // internal so sibling registries reuse the exact same matching.
    internal static IEnumerable<string> EnumerateVersionStringFolders(string directory, int major, int minor)
    {
        string exact = $"{major}.{minor}";
        string prefix = exact + ".";
        foreach (string dir in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(dir);
            if (string.Equals(name, exact, StringComparison.Ordinal)
                || name.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return dir;
            }
        }
    }

    // Cache the serializer options. JsonStringEnumConverter accepts "Float",
    // "Int", etc. — matches the generator's PascalCase enum names and the
    // way the standard parameter types are annotated for Newtonsoft. Both
    // converters must agree on the wire format or seeds round-trip-fail.
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static bool TryLoadFile(string file, Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Dictionary<string, List<uint>> hashesByName, Action<string>? logError)
    {
        JsonSerializerOptions jsonOpts = s_jsonOpts;
        try
        {
            string json = File.ReadAllText(file);
            EngineUbMetadata? entry = JsonSerializer.Deserialize<EngineUbMetadata>(json, jsonOpts);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LayoutHashHex))
            {
                logError?.Invoke($"[EngineUbMetadata] {file}: missing 'name' or 'layoutHash' — skipped.");
                return false;
            }
            // Idempotent: re-derive typed Textures/Samplers/Buffers/UAVs from
            // the canonical Resources list when the seed omits them, so a
            // minimal hand-authored seed (Resources only) still produces
            // typed buckets for the consumers downstream.
            EnsureTypedBucketsPopulated(entry);
            uint hash = entry.ParsedHash();
            var key = (entry.Name, hash);
            if (byNameAndHash.ContainsKey(key))
            {
                // Silent skip — the game-specific folder already won this
                // (Name, Hash). Hand-organised dupes elsewhere in the tree
                // are absorbed without warning so the user can keep
                // experimental copies next to the seeds.
                return false;
            }
            byNameAndHash[key] = entry;
            if (!hashesByName.TryGetValue(entry.Name, out List<uint>? list))
            {
                list = new List<uint>();
                hashesByName[entry.Name] = list;
            }
            list.Add(hash);
            return true;
        }
        catch (Exception ex)
        {
            logError?.Invoke($"[EngineUbMetadata] {file}: parse failed — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Materialise the typed-bucket views (Textures / Samplers / Buffers /
    // UAVs) from the canonical flat `Resources` list whenever the seed
    // didn't already include them. Lets the generator emit a minimal seed
    // (Resources only) and have the runtime still see typed buckets.
    private static void EnsureTypedBucketsPopulated(EngineUbMetadata meta)
    {
        if (meta.Resources.Count == 0) return;
        bool anyBucket = meta.Textures.Count > 0 || meta.Samplers.Count > 0
                       || meta.Buffers.Count > 0  || meta.UAVs.Count > 0;
        if (anyBucket) return;
        foreach (EngineUbResourceSlot slot in meta.Resources)
        {
            switch (slot.UbmtType)
            {
                case "UBMT_TEXTURE":
                case "UBMT_RDG_TEXTURE":
                case "UBMT_RDG_TEXTURE_ACCESS":
                case "UBMT_RDG_TEXTURE_ACCESS_ARRAY":
                    meta.Textures.Add(new TextureParameter
                    {
                        Name = slot.Name,
                        NameIndex = -1,
                        Index = slot.Index,
                        SamplerIndex = -1,
                        MultiSampled = false,
                        Dim = 2,
                    });
                    break;
                case "UBMT_SAMPLER":
                    meta.Samplers.Add(new SamplerParameter
                    {
                        Name = slot.Name,
                        Sampler = (uint)slot.Index,
                        BindPoint = slot.Index,
                    });
                    break;
                case "UBMT_UAV":
                case "UBMT_RDG_TEXTURE_UAV":
                case "UBMT_RDG_BUFFER_UAV":
                    meta.UAVs.Add(new UAVParameter
                    {
                        Name = slot.Name,
                        NameIndex = -1,
                        Index = slot.Index,
                        OriginalIndex = slot.Index,
                    });
                    break;
                default: // UBMT_SRV, UBMT_RDG_*_SRV, UBMT_RDG_BUFFER_*, UBMT_RDG_UNIFORM_BUFFER, etc.
                    meta.Buffers.Add(new BufferBindingParameter
                    {
                        Name = slot.Name,
                        NameIndex = -1,
                        Index = slot.Index,
                        ArraySize = 0,
                    });
                    break;
            }
        }
    }

    public EngineUbMetadata? Lookup(string ubName, uint layoutHash)
    {
        if (string.IsNullOrEmpty(ubName)) return null;
        return _byNameAndHash.TryGetValue((ubName, layoutHash), out EngineUbMetadata? meta) ? meta : null;
    }

    // Hash-only reverse lookup. When the cook stripped the UB name (leaving
    // SPIRV-Cross / dxil-spirv generated `CB<N>UBO` placeholders), the
    // ResourceTableLayoutHashes entry survives — and engine UB layout hashes
    // are documented as collision-resistant 32-bit XOR folds keyed on the
    // exact (cb size, binding flags, resource type sequence) tuple.
    // If exactly ONE seed has the given hash across the entire engine
    // metadata set, that's an unambiguous match — caller can synthesize the
    // missing name. Returns null when zero OR multiple seeds collide on
    // the hash. (For >1, caller falls back to anonymous placeholders rather
    // than guessing — strictly source-truth.)
    public EngineUbMetadata? LookupByHashOnly(uint layoutHash)
    {
        EngineUbMetadata? hit = null;
        foreach (var kvp in _byNameAndHash)
        {
            if (kvp.Key.Hash != layoutHash) continue;
            if (hit != null) return null; // collision — refuse to guess
            hit = kvp.Value;
        }
        return hit;
    }

    // Name-only fallback. When the cook's UB hash doesn't match any seed for
    // the same UB name (modded engine, custom UE fork, or game-specific
    // additions), pick the seed whose `ConstantBufferSize` is the LARGEST
    // that still fits within `cookCbSize` bytes (so applying the seed's
    // members won't overflow the cook's actual buffer). UE rarely re-shuffles
    // existing UB member offsets — game-side mods almost always APPEND new
    // fields — so applying a strict prefix of the latest matching vanilla
    // seed is the safe recovery: every named member sits at the correct
    // offset, and the modded tail naturally gets `_TODO_missing_seed_field`
    // via LayoutBuilder's tail-fill.
    //
    // Returns null when no seed exists for `ubName`, or when all candidate
    // seeds are larger than the cook's actual buffer (would overflow).
    // `cookCbSize` = (cook's flat array register count) * 16, derivable from
    // the SPIR-V flat `_m0[N]` array OR from the cooked
    // ParameterMapInfo.UniformBuffers entry's size.
    public EngineUbMetadata? LookupByNameWithSizeCap(string ubName, int cookCbSize)
    {
        if (string.IsNullOrEmpty(ubName) || cookCbSize <= 0) return null;
        EngineUbMetadata? best = null;
        int bestSize = -1;
        foreach (var kvp in _byNameAndHash)
        {
            if (!string.Equals(kvp.Key.Name, ubName, StringComparison.Ordinal)) continue;
            int seedSize = kvp.Value.ConstantBufferSize;
            if (seedSize <= 0 || seedSize > cookCbSize) continue;
            if (seedSize > bestSize)
            {
                best = kvp.Value;
                bestSize = seedSize;
            }
        }
        return best;
    }

    // For diagnostics: returns true iff at least one file matches `ubName`
    // (any hash). Used by the symbolizer to log "shape-drift" warnings:
    // we have metadata for `View` but the cook's hash doesn't match any
    // of them — almost certainly a different engine version layout.
    public bool HasAnyForName(string ubName, out IReadOnlyList<uint> knownHashes)
    {
        if (_hashesByName.TryGetValue(ubName, out List<uint>? list))
        {
            knownHashes = list;
            return true;
        }
        knownHashes = Array.Empty<uint>();
        return false;
    }
}

// Translates an EngineUbMetadata into the SerializedProgramData shape the
// patcher / rewriter consume. Trivial now that the metadata already stores
// the cbuffer directly as a ConstantBufferParameter — same plumbing as
// MaterialConstantBufferReader produces for the Material UB.
internal static class EngineUbMetadataTranslator
{
    public static ConstantBufferParameter ToConstantBufferParameter(EngineUbMetadata meta)
    {
        if (meta.ConstantBuffer != null)
        {
            // Defensive: if a hand-authored seed left the cbuffer name blank,
            // fill it with the UB name so downstream pretty-printers see
            // something useful.
            if (string.IsNullOrWhiteSpace(meta.ConstantBuffer.Name))
                meta.ConstantBuffer.Name = meta.Name;
            return meta.ConstantBuffer;
        }
        // Empty placeholder — UBs that are pure resource holders (no numeric
        // members) still need a cbuffer entry so the rewriter has a name to
        // anchor against.
        return new ConstantBufferParameter
        {
            Name = meta.Name,
            NameIndex = -1,
            VectorParameters = Array.Empty<VectorParameter>(),
            MatrixParameters = Array.Empty<MatrixParameter>(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = 0,
            IsPartialCB = false,
        };
    }
}
