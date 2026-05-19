using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Minimal hash→name lookup loader for the sister indexes alongside
// `_ShaderType/_HashToName.json`:
//
//   `_VertexFactoryType/_HashToName.json`  → FVertexFactoryType::HashedName
//   `_ShaderPipelineType/_HashToName.json` → FShaderPipelineType::HashedName
//
// Same hash math as FShaderType (CityHash64WithSeed(UPPER(name), seed=0))
// — we don't need per-class seeds for these, just the name. Pass145 loads
// instances of this class so Pass146 can backfill blank
// `VertexFactoryTypeName` / `PipelineTypeName` in container records the
// cook left empty.
internal sealed class HashNameIndex
{
    private readonly Dictionary<ulong, string> _hashToName;

    public string SourceDirectory { get; }
    public int Count => _hashToName.Count;

    private HashNameIndex(string sourceDir, Dictionary<ulong, string> hashToName)
    {
        SourceDirectory = sourceDir;
        _hashToName = hashToName;
    }

    public static HashNameIndex Empty { get; } = new(string.Empty, new Dictionary<ulong, string>());

    // Load `_HashToName.json` for a given index subfolder (e.g.
    // `_VertexFactoryType`). Scan-root priority mirrors ShaderTypeSeedRegistry:
    //   1. `<directory>/<gameVersionEnum>/<subfolder>/_HashToName.json`
    //   2. `<directory>/<base UE folder>/<subfolder>/_HashToName.json` when fallback enabled
    //   3. Recursive sweep under `<directory>` for any matching file path.
    public static HashNameIndex LoadForGame(
        string? directory, string subfolder, string? gameVersionEnum, bool tryBaseFallback,
        Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[{subfolder}] Directory not set or missing: {directory ?? "<null>"} — name backfill disabled.");
            return Empty;
        }

        List<string> scanRoots = new();
        if (!string.IsNullOrEmpty(gameVersionEnum))
        {
            string specific = Path.Combine(directory, gameVersionEnum, subfolder);
            if (Directory.Exists(specific)) scanRoots.Add(specific);
        }
        if (tryBaseFallback
            && !string.IsNullOrEmpty(gameVersionEnum)
            && !gameVersionEnum.StartsWith("GAME_UE", StringComparison.Ordinal)
            && EngineUbMetadataRegistry.TryDeriveBaseUeFromEGameForShaderTypes(gameVersionEnum, out string baseUe)
            && !string.Equals(baseUe, gameVersionEnum, StringComparison.Ordinal))
        {
            string baseDir = Path.Combine(directory, baseUe, subfolder);
            if (Directory.Exists(baseDir)) scanRoots.Add(baseDir);
        }
        // Recursive sweep under root for any `<subfolder>/_HashToName.json`.
        if (Directory.Exists(directory))
        {
            try
            {
                string needle = $"/{subfolder}/_HashToName.json";
                foreach (string f in Directory.EnumerateFiles(directory, "_HashToName.json", SearchOption.AllDirectories))
                {
                    if (f.Replace('\\', '/').EndsWith(needle, StringComparison.OrdinalIgnoreCase)
                        && !scanRoots.Contains(Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
                    {
                        scanRoots.Add(Path.GetDirectoryName(f)!);
                    }
                }
            }
            catch { /* ignore missing dirs */ }
        }

        Dictionary<ulong, string> hashToName = new();
        foreach (string root in scanRoots)
        {
            string file = Path.Combine(root, "_HashToName.json");
            if (!File.Exists(file)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Entries", out JsonElement entries)
                    && entries.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in entries.EnumerateObject())
                    {
                        if (!ulong.TryParse(p.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                        {
                            continue;
                        }
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            // First-wins on collision (won't happen for legit
                            // FName-style canonical names).
                            hashToName.TryAdd(h, p.Value.GetString() ?? string.Empty);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logError?.Invoke($"[{subfolder}] {file}: parse failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[{subfolder}] Loaded {hashToName.Count} hash→name entries{gameTag} from '{directory}'.");
        return new HashNameIndex(directory, hashToName);
    }

    public string? ResolveName(string? hashHex)
    {
        if (string.IsNullOrWhiteSpace(hashHex)) return null;
        string s = hashHex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h)) return null;
        return _hashToName.TryGetValue(h, out string? name) ? name : null;
    }
}
