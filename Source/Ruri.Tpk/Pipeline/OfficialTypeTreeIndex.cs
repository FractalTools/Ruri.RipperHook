using AssetRipper.Primitives;
using AssetRipper.Tpk.TypeTrees.Json;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ruri.Tpk.Pipeline;

/// <summary>
/// The official Unity type trees, read straight off AssetRipper/TypeTreeDumps over the network and
/// kept in memory. Nothing is written to disk on purpose: these dumps are upstream's data, a stale
/// local copy is worse than no copy (it silently diffs a game against the wrong engine), and the
/// whole point of the drift pass is to be re-runnable against whatever upstream has today.
///
/// A custom engine does not report a real Unity build. EndField says <c>2021.3.34f5</c> and
/// <c>2021.3.34f0</c> -- neither exists; the official release is <c>2021.3.34f1</c>. So the version
/// a game claims is treated as a COORDINATE to snap to the nearest official dump, never as a key to
/// look up directly.
/// </summary>
internal sealed class OfficialTypeTreeIndex
{
    private const string ListUrl = "https://api.github.com/repos/AssetRipper/TypeTreeDumps/contents/InfoJson";
    private const string RawUrlFormat = "https://raw.githubusercontent.com/AssetRipper/TypeTreeDumps/main/InfoJson/{0}.json";

    private readonly HttpClient _http;
    private readonly Dictionary<UnityVersion, UnityInfo> _fetched = new();
    private List<UnityVersion>? _available;

    public OfficialTypeTreeIndex()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub rejects API calls without a User-Agent outright.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ruri.Tpk", "1.0"));
        // Unauthenticated is 60 requests/hour, which one drift pass fits inside comfortably (one
        // listing plus one fetch per distinct engine). A token only matters when re-running often.
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Every official version upstream publishes a dump for, ascending. Fetched once.</summary>
    public IReadOnlyList<UnityVersion> Available => _available ??= FetchAvailable();

    private List<UnityVersion> FetchAvailable()
    {
        string json = _http.GetStringAsync(ListUrl).GetAwaiter().GetResult();
        List<UnityVersion> versions = new();
        foreach (JsonElement entry in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "file")
            {
                continue;
            }
            string name = entry.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // A custom-engine suffix means it is not a stock Unity release, so it cannot serve as the
            // baseline a fork is measured against.
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

    /// <summary>
    /// The official dump closest to what a game claims. Closeness is lexicographic on how much of the
    /// version has to be given up: an exact hit first, then the same Major.Minor.Build with a
    /// different release suffix (EndField's whole case), then the nearest Build inside the same
    /// Major.Minor, and only then the nearest Minor / Major. Snapping is never allowed to cross a
    /// major version -- a 2021 game diffed against 2019 would report drift that is really just
    /// engine evolution.
    /// </summary>
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
                // Same build, different release suffix: rank by how far the suffix moved, preferring
                // a final release (f) since that is what a fork almost always branched from.
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

        // Orders release suffixes the way Unity ships them, so "how far apart" is a real distance.
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

    /// <summary>The parsed dump for an official version, fetched into memory once per process.</summary>
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
