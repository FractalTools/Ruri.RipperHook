using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Ruri.FModelHook.ShaderDecompiler;

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

    public static HashNameIndex LoadForGame(
        string? directory, string subfolder, string? gameVersionEnum, bool tryBaseFallback,
        Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[{subfolder}] Directory not set or missing: {directory ?? "<null>"} — name backfill disabled.");
            return Empty;
        }

        List<string> versionRoots = EngineUbMetadataRegistry.BuildScanRoots(directory, gameVersionEnum, tryBaseFallback);
        List<string> scanRoots = new();
        string needle = $"/{subfolder}/_HashToName.json";
        foreach (string versionRoot in versionRoots)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(versionRoot, "_HashToName.json", SearchOption.AllDirectories))
                {
                    if (f.Replace('\\', '/').EndsWith(needle, StringComparison.OrdinalIgnoreCase)
                        && !scanRoots.Contains(Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
                    {
                        scanRoots.Add(Path.GetDirectoryName(f)!);
                    }
                }
            }
            catch {}
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
