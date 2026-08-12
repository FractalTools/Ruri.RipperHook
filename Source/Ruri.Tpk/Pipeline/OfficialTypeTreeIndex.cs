using AssetRipper.Primitives;
using AssetRipper.Tpk.TypeTrees.Json;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ruri.Tpk.Pipeline;

internal sealed class OfficialTypeTreeIndex
{
    private const string ListUrl = "https://api.github.com/repos/AssetRipper/TypeTreeDumps/git/trees/main:InfoJson";
    private const string RawUrlFormat = "https://raw.githubusercontent.com/AssetRipper/TypeTreeDumps/main/InfoJson/{0}.json";

    private readonly HttpClient _http;
    private readonly Dictionary<UnityVersion, UnityInfo> _fetched = new();
    private List<UnityVersion>? _available;

    public OfficialTypeTreeIndex()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ruri.Tpk", "1.0"));
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public IReadOnlyList<UnityVersion> Available => _available ??= FetchAvailable();

    private List<UnityVersion> FetchAvailable()
    {
        string json = _http.GetStringAsync(ListUrl).GetAwaiter().GetResult();
        JsonElement root = JsonDocument.Parse(json).RootElement;
        if (root.TryGetProperty("truncated", out JsonElement truncated) && truncated.GetBoolean())
        {
            throw new InvalidOperationException(
                $"[Drift] {ListUrl} was truncated by GitHub -- the version list would be incomplete and the "
                + "nearest-version snap unreliable. Fetch the listing another way before diffing.");
        }

        List<UnityVersion> versions = new();
        foreach (JsonElement entry in root.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "blob")
            {
                continue;
            }
            string name = entry.GetProperty("path").GetString() ?? string.Empty;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (UnityVersion.TryParse(name[..^5], out UnityVersion version, out string? customEngine)
                && string.IsNullOrEmpty(customEngine))
            {
                versions.Add(version);
            }
        }

        if (versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"[Drift] {ListUrl} returned no parseable version dumps. Upstream layout changed, or the request was rate-limited "
                + "(set GITHUB_TOKEN to raise the limit).");
        }

        versions.Sort();
        return versions;
    }

    public UnityVersion ResolveClosest(UnityVersion claimed)
    {
        UnityVersion? best = null;
        (int Tier, int Distance, int Tie) bestRank = default;

        foreach (UnityVersion candidate in Available)
        {
            if (candidate.Major != claimed.Major)
            {
                continue;
            }

            (int Tier, int Distance, int Tie) rank;
            if (candidate == claimed)
            {
                rank = (0, 0, 0);
            }
            else if (candidate.Minor == claimed.Minor && candidate.Build == claimed.Build)
            {
                int typeDistance = Math.Abs(SuffixRank(candidate) - SuffixRank(claimed));
                rank = (1, typeDistance, candidate.Type == UnityVersionType.Final ? 0 : 1);
            }
            else if (candidate.Minor == claimed.Minor)
            {
                rank = (2, Math.Abs(candidate.Build - claimed.Build), 0);
            }
            else
            {
                rank = (3, Math.Abs(candidate.Minor - claimed.Minor), Math.Abs(candidate.Build - claimed.Build));
            }

            if (best is null || Compare(rank, bestRank) < 0)
            {
                best = candidate;
                bestRank = rank;
            }
        }

        return best ?? throw new InvalidOperationException(
            $"[Drift] No official Unity {claimed.Major}.x dump exists upstream to diff a game claiming {claimed} against.");

        static int Compare((int Tier, int Distance, int Tie) left, (int Tier, int Distance, int Tie) right)
        {
            int cmp = left.Tier.CompareTo(right.Tier);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = left.Distance.CompareTo(right.Distance);
            return cmp != 0 ? cmp : left.Tie.CompareTo(right.Tie);
        }

        static int SuffixRank(UnityVersion version) => version.Type switch
        {
            UnityVersionType.Alpha => 0,
            UnityVersionType.Beta => 1000,
            UnityVersionType.China => 2000,
            UnityVersionType.Final => 3000,
            UnityVersionType.Patch => 4000,
            _ => 5000,
        } + version.TypeNumber;
    }

    public UnityInfo Fetch(UnityVersion version)
    {
        if (_fetched.TryGetValue(version, out UnityInfo? cached))
        {
            return cached;
        }

        string url = string.Format(RawUrlFormat, version.ToString());
        Console.WriteLine($"[Drift] fetching official {version} ({url})");
        using Stream stream = _http.GetStreamAsync(url).GetAwaiter().GetResult();
        UnityInfo info = UnityInfo.FromStream(stream)
            ?? throw new InvalidDataException($"[Drift] {url} did not deserialize into a type tree dump.");

        _fetched.Add(version, info);
        return info;
    }
}
