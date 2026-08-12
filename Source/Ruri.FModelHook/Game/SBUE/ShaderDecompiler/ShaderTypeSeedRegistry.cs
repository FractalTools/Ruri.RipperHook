using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal sealed class ShaderTypeSeedRegistry
{
    private readonly Dictionary<ulong, EngineUbMetadata> _byHash;
    private readonly List<(string Prefix, EngineUbMetadata Meta)> _byNamePrefixDesc;
    private readonly Dictionary<ulong, string> _hashToName;

    public string SourceDirectory { get; }
    public int FileCount => _byHash.Count;
    public int HashToNameCount => _hashToName.Count;

    private ShaderTypeSeedRegistry(string sourceDir, Dictionary<ulong, EngineUbMetadata> byHash, Dictionary<ulong, string> hashToName)
    {
        SourceDirectory = sourceDir;
        _byHash = byHash;
        _hashToName = hashToName;
        _byNamePrefixDesc = byHash.Values
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .Select(m => (Prefix: m.Name, Meta: m))
            .OrderByDescending(t => t.Prefix.Length)
            .ToList();
    }

    public static ShaderTypeSeedRegistry Empty { get; } = new(string.Empty,
        new Dictionary<ulong, EngineUbMetadata>(),
        new Dictionary<ulong, string>());

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
        List<string> scanRoots = EngineUbMetadataRegistry.BuildScanRoots(directory, gameVersionEnum, tryBaseFallback);

        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<ulong, string> hashToName = new();
        int loaded = 0, skipped = 0;
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

        foreach (string root in scanRoots)
        {
            string indexPath = Path.Combine(root, root.EndsWith("_ShaderType", StringComparison.OrdinalIgnoreCase) ? "_HashToName.json" : Path.Combine("_ShaderType", "_HashToName.json"));
            List<string> indexCandidates = new();
            if (File.Exists(indexPath)) indexCandidates.Add(indexPath);
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "_HashToName.json", SearchOption.AllDirectories))
                {
                    if (!indexCandidates.Contains(f, StringComparer.OrdinalIgnoreCase)) indexCandidates.Add(f);
                }
            }
            catch {}

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

    public string? ResolveTypeName(string hashHex)
    {
        if (string.IsNullOrWhiteSpace(hashHex)) return null;
        string s = hashHex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong h)) return null;
        return _hashToName.TryGetValue(h, out string? name) ? name : null;
    }

    public bool TryLookupWithFallback(string hashHex, string? cookedTypeName, out EngineUbMetadata meta, out string matchedBy)
    {
        meta = null!;
        matchedBy = string.Empty;
        if (TryLookup(hashHex, out meta))
        {
            matchedBy = "exact-hash";
            return true;
        }
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
