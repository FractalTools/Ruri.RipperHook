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

    public string SourceDirectory { get; }
    public int FileCount => _byHash.Count;

    private ShaderTypeSeedRegistry(string sourceDir, Dictionary<ulong, EngineUbMetadata> byHash)
    {
        SourceDirectory = sourceDir;
        _byHash = byHash;
    }

    public static ShaderTypeSeedRegistry Empty { get; } = new(string.Empty,
        new Dictionary<ulong, EngineUbMetadata>());

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
        List<string> scanRoots = new();
        if (!string.IsNullOrEmpty(gameVersionEnum))
        {
            string specific = Path.Combine(directory, gameVersionEnum, "_ShaderType");
            if (Directory.Exists(specific)) scanRoots.Add(specific);
        }
        if (tryBaseFallback
            && !string.IsNullOrEmpty(gameVersionEnum)
            && !gameVersionEnum.StartsWith("GAME_UE", StringComparison.Ordinal)
            && EngineUbMetadataRegistry.TryDeriveBaseUeFromEGameForShaderTypes(gameVersionEnum, out string baseUe)
            && !string.Equals(baseUe, gameVersionEnum, StringComparison.Ordinal))
        {
            string baseDir = Path.Combine(directory, baseUe, "_ShaderType");
            if (Directory.Exists(baseDir)) scanRoots.Add(baseDir);
        }
        // Recursive sweep — only files under a `_ShaderType` folder qualify so
        // we don't pull in regular engine-UB seeds (which live at the parent
        // level and would just fail JSON-schema sniffing anyway).
        if (Directory.Exists(directory)) scanRoots.Add(directory);

        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
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

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[ShaderTypeSeed] Loaded {loaded} ShaderType seed(s){gameTag} from '{directory}' ({skipped} skipped). Scan roots: {string.Join(" -> ", scanRoots)}");
        return new ShaderTypeSeedRegistry(directory, byHash);
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
