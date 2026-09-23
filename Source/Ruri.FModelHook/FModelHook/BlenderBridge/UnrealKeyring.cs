using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AssetRipper.Import.Logging;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using Newtonsoft.Json.Linq;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// The keys and the reflection schema a title PUBLISHES: the live document when the network can
/// be reached, the last one that was reached when it cannot.
///
/// THE NETWORK IS THE TRUTH. Every mount asks the endpoint and overwrites what is kept, so a
/// title that re-keys is followed the same day and no stale copy is ever preferred to a reachable
/// one. What is kept exists for exactly one case: the endpoint is down or the machine is offline,
/// and a build that opened yesterday has to open today. So the folder holds ONE key document and
/// ONE schema per title, each replaced in place -- nothing accumulates, and there is no
/// content-addressed pile to sweep.
///
/// A published key document is a HISTORY, not a manifest of the build in front of us: it
/// accumulates every archive key the title has ever used and says nothing about which patch each
/// belongs to (Infinity Nikki's is four hundred kilobytes of them, while the install here has
/// five locked archives). The whole list is submitted and the archive GUIDs decide -- a key whose
/// guid no archive carries is simply never used. That is also why this is a document to fetch
/// rather than a form to fill: picking the right few by hand would mean knowing which guids this
/// particular build's archives carry.
///
/// Parsing is in memory either way: CUE4Parse reads a .usmap out of a byte array as readily as
/// out of a file, so the bytes go from the socket (or the kept file) straight into the parser.
///
/// An endpoint is stated exactly as FModel states one -- a url and a JSONPath naming the values --
/// so a configuration a user already tested there works here unchanged. It is READ here rather
/// than through FModel's own reader: that reader normalises keys through a helper whose class
/// also holds WPF window code, so touching it from a host that is not the FModel app faults on
/// PresentationFramework and every key silently becomes no key.
/// </summary>
public static class UnrealKeyring
{
    private const string HexPrefix = "0x";
    private const int KeyHexLength = 64;
    private const int FetchTimeoutSeconds = 120;
    private const string KeptFolder = "RuriRipperHook";
    private const string KeptUnrealFolder = "Unreal";
    private const string KeptKeysFile = "keys.json";
    private const string SchemaExtension = ".usmap";
    private const string SchemaPattern = "*" + SchemaExtension;

    /// <summary>Where the last reachable answer is kept, for the day the endpoint is not reachable.</summary>
    public static string KeptRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), KeptFolder, KeptUnrealFolder);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Keyset> Fetched = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Schema> Schemas = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = NewClient();

    /// <summary>
    /// One title's published keys, and a fingerprint of the set so a session opened under one key
    /// set is never mistaken for one opened under another.
    /// </summary>
    public sealed record Keyset(IReadOnlyList<KeyValuePair<FGuid, FAesKey>> Keys, string Token)
    {
        public static readonly Keyset None = new([], string.Empty);
    }

    /// <summary>
    /// One title's published reflection schema, already parsed: the provider a mount reads
    /// properties through, the name the endpoint gave it, and a fingerprint of its bytes.
    /// </summary>
    public sealed record Schema(UsmapTypeMappingsProvider? Provider, string Name, string Token)
    {
        public static readonly Schema None = new(null, string.Empty, string.Empty);
    }

    /// <summary>The keys this title publishes, fetched on first ask and kept for the process.</summary>
    public static Keyset Keys(UnrealTitle? title) => Keys(title, refresh: false);

    /// <summary>
    /// The same keys, optionally fetched again -- what a mount asks for when archives are still
    /// locked after the first set was submitted. "The archives did not open" is the only honest
    /// test of whether a key set is current, so it is the one that triggers a re-fetch; nothing
    /// here compares version strings.
    /// </summary>
    public static Keyset Keys(UnrealTitle? title, bool refresh)
    {
        if (title?.Keys is null)
        {
            return Keyset.None;
        }
        lock (Gate)
        {
            if (!refresh && Fetched.TryGetValue(title.Product, out Keyset? kept))
            {
                return kept;
            }
            Keyset read = ReadKeys(title);
            Fetched[title.Product] = read;
            return read;
        }
    }

    /// <summary>
    /// The reflection schema this title publishes, parsed from the bytes the endpoint hands over.
    /// Fetched once per process; a build whose endpoint does not answer gets none, and the mount
    /// says so rather than reading properties it cannot name.
    /// </summary>
    public static Schema Mappings(UnrealTitle? title)
    {
        if (title?.Mappings is null)
        {
            return Schema.None;
        }
        lock (Gate)
        {
            if (Schemas.TryGetValue(title.Product, out Schema? kept))
            {
                return kept;
            }
            Schema read = ReadSchema(title);
            Schemas[title.Product] = read;
            return read;
        }
    }

    private static Keyset ReadKeys(UnrealTitle title)
    {
        string kept = Path.Combine(KeptRoot, title.Product, KeptKeysFile);
        string document = Fetch(title.Keys!.Url);
        bool live = document.Length > 0;
        if (!live)
        {
            document = ReadKept(kept);
            if (document.Length > 0)
            {
                Logger.Warning(LogCategory.Import,
                    $"[Unreal] {title.Product}: '{title.Keys!.Url}' is not reachable; reading the last keys it published.");
            }
        }
        JToken[] selected = Select(document, title.Keys!.Path);
        if (selected.Length == 0)
        {
            Logger.Warning(LogCategory.Import,
                $"[Unreal] {title.Product}: no keys from '{title.Keys!.Url}' and none kept -- encrypted archives stay closed.");
            return Keyset.None;
        }
        List<KeyValuePair<FGuid, FAesKey>> keys = new();
        string main = Normalise(selected.ElementAtOrDefault(0)?.ToString());
        if (main.Length > 0)
        {
            keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(main)));
        }
        if (selected.ElementAtOrDefault(1) is JArray published)
        {
            foreach (JToken entry in published)
            {
                string guid = entry["guid"]?.ToString() ?? string.Empty;
                string key = Normalise(entry["key"]?.ToString());
                if (guid.Length > 0 && key.Length > 0)
                {
                    keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(guid), new FAesKey(key)));
                }
            }
        }
        if (keys.Count == 0)
        {
            Logger.Warning(LogCategory.Import,
                $"[Unreal] {title.Product}: '{title.Keys!.Url}' answered with no usable key -- encrypted archives stay closed.");
            return Keyset.None;
        }
        if (live)
        {
            Keep(kept, Encoding.UTF8.GetBytes(document));
        }
        Logger.Info(LogCategory.Import,
            $"[Unreal] {title.Product}: {keys.Count} archive key(s) {(live ? "from" : "last kept from")} '{title.Keys!.Url}'.");
        return new Keyset(keys, Fingerprint(keys));
    }

    private static Schema ReadSchema(UnrealTitle title)
    {
        string folder = Path.Combine(KeptRoot, title.Product);
        JToken[] selected = Select(Fetch(title.Mappings!.Url), title.Mappings!.Path);
        string url = selected.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
        string name = selected.ElementAtOrDefault(1)?.ToString() ?? string.Empty;
        if (name.Length == 0 && url.Length > 0)
        {
            name = url[(url.LastIndexOf('/') + 1)..];
        }
        byte[] payload = url.Length > 0 ? Download(url) : [];
        bool live = payload.Length > 0;
        if (!live)
        {
            (name, payload) = ReadKeptSchema(folder);
            if (payload.Length > 0)
            {
                Logger.Warning(LogCategory.Import,
                    $"[Unreal] {title.Product}: '{title.Mappings!.Url}' is not reachable; reading the last schema it published ('{name}').");
            }
            else
            {
                Logger.Warning(LogCategory.Import,
                    $"[Unreal] {title.Product}: '{title.Mappings!.Url}' named no reflection schema and none is kept.");
                return Schema.None;
            }
        }
        try
        {
            PublishedSchema provider = new(payload);
            if (live)
            {
                KeepSchema(folder, name, payload);
            }
            Logger.Info(LogCategory.Import,
                $"[Unreal] {title.Product}: reflection schema '{name}' ({payload.Length / 1024} KiB) "
                + $"{(live ? "from" : "last kept from")} '{title.Mappings!.Url}' ({provider.MappingsForGame?.Types.Count ?? 0} structs).");
            return new Schema(provider, name, Fingerprint(payload));
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import,
                $"[Unreal] {title.Product}: '{name}' could not be read as a .usmap: {exception.Message}");
            return Schema.None;
        }
    }

    /// <summary>The schema kept for this title, newest first -- what an unreachable endpoint falls back to.</summary>
    private static (string Name, byte[] Payload) ReadKeptSchema(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return (string.Empty, []);
        }
        string best = string.Empty;
        DateTime bestTime = DateTime.MinValue;
        foreach (string file in Directory.EnumerateFiles(folder, SchemaPattern))
        {
            DateTime written = File.GetLastWriteTimeUtc(file);
            if (written > bestTime)
            {
                bestTime = written;
                best = file;
            }
        }
        try
        {
            return best.Length > 0 ? (Path.GetFileName(best), File.ReadAllBytes(best)) : (string.Empty, []);
        }
        catch (IOException)
        {
            return (string.Empty, []);
        }
    }

    /// <summary>
    /// Keep this schema as the one to fall back to, and drop whichever one it replaces: a title
    /// has exactly one current schema, so the folder never grows a pile of old ones.
    /// </summary>
    private static void KeepSchema(string folder, string name, byte[] payload)
    {
        string path = Path.Combine(folder, name);
        Keep(path, payload);
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, SchemaPattern))
            {
                if (!string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] Could not drop the schema '{name}' replaced in '{folder}': {exception.Message}");
        }
    }

    private static string ReadKept(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void Keep(string path, byte[] payload)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, payload);
        }
        catch (IOException exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] Could not keep '{path}': {exception.Message}");
        }
    }

    /// <summary>A schema that lives in the bytes it was handed, with no file behind it.</summary>
    private sealed class PublishedSchema : UsmapTypeMappingsProvider
    {
        private readonly byte[] bytes;

        public PublishedSchema(byte[] bytes)
        {
            this.bytes = bytes;
            Load(bytes);
        }

        public override void Reload() => Load(bytes);
    }

    /// <summary>A 256-bit key as CUE4Parse takes it: upper case hex behind an 0x, or nothing when it is not one.</summary>
    private static string Normalise(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }
        ReadOnlySpan<char> hex = key.AsSpan().Trim();
        if (hex.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[HexPrefix.Length..];
        }
        return hex.Length == KeyHexLength ? HexPrefix + hex.ToString().ToUpperInvariant() : string.Empty;
    }

    private static JToken[] Select(string document, string path)
    {
        if (document.Length == 0)
        {
            return [];
        }
        try
        {
            return JToken.Parse(document).SelectTokens(path).ToArray();
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] '{path}' did not read the published document: {exception.Message}");
            return [];
        }
    }

    private static HttpClient NewClient()
    {
        HttpClient client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            nameof(Ruri) + "." + nameof(FModelHook), typeof(UnrealKeyring).Assembly.GetName().Version?.ToString() ?? "1.0"));
        return client;
    }

    private static string Fetch(string url)
    {
        try
        {
            using HttpResponseMessage response = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning(LogCategory.Import, $"[Unreal] '{url}' answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                return string.Empty;
            }
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] '{url}' could not be read: {exception.Message}");
            return string.Empty;
        }
    }

    private static byte[] Download(string url)
    {
        try
        {
            using HttpResponseMessage response = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning(LogCategory.Import, $"[Unreal] '{url}' answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                return [];
            }
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] '{url}' could not be downloaded: {exception.Message}");
            return [];
        }
    }

    private static string Fingerprint(IReadOnlyList<KeyValuePair<FGuid, FAesKey>> keys)
    {
        StringBuilder text = new();
        foreach ((FGuid guid, FAesKey key) in keys)
        {
            text.Append(guid).Append('=').Append(key).Append(';');
        }
        return Fingerprint(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string Fingerprint(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload))[..16];
}
