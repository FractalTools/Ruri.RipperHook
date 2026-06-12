using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;
using Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

namespace Ruri.FModelHook.Game.SBUE.Headless;

// Headless shader-archive export + decompile driver. Builds a CUE4Parse
// `DefaultFileProvider` straight from a parsed `HeadlessGameConfig` (AES keys
// + mappings + version), mounts the game, then runs the SAME per-archive
// pipeline the FModel hook uses (`ShaderArchiveExporter`) — with NO FModel WPF
// host, no dispatcher, no hidden window. This is the "直接 CLI + 配置好的设置
// 直接反编译" path: read config, mount, decompile.
public static class HeadlessShaderExportRunner
{
    public sealed class Options
    {
        public required HeadlessGameConfig Config { get; init; }
        // Archive-name allow-list tokens (substring match). Empty/null = all.
        public IReadOnlyList<string>? ArchiveNameFilter { get; init; }
        public bool SkipGlobal { get; init; }
        public bool SplitVariants { get; init; }
        // Build the cache + sidecars + .ushaderlib without decompiling (the
        // master archive's 261k-shader decompile is a multi-hour job; this lets
        // a fast --decompile-only iterate afterwards against the full cache).
        public bool SkipDecompile { get; init; }
        public Action<string> Log { get; init; } = _ => { };
        public Action<string> LogError { get; init; } = _ => { };
    }

    public sealed class RunResult
    {
        public int ArchivesProcessed { get; set; }
        public int MaterialInterfaces { get; set; }
        public bool MappingsLoaded { get; set; }
        public string ProjectName { get; set; } = string.Empty;
    }

    public static RunResult Run(Options options)
    {
        HeadlessGameConfig cfg = options.Config;
        Action<string> log = options.Log;
        Action<string> logError = options.LogError;

        if (cfg.HasUnsupportedVersioning)
            logError("[Headless] WARNING: this game's settings carry custom version/option/map-struct overrides which the headless mount does not yet replicate. Mount may misparse — fall back to the GUI if assets fail to load.");

        // 1. Native codecs. Oodle auto-downloads into <Output>/.data when
        //    absent; zlib is downloaded if missing/stale (mirrors FModel).
        InitNativeCodecs(cfg, log, logError);

        // 2. Build the provider. InfinityNikki and the other targeted forks
        //    mount through the vanilla DefaultFileProvider; only the EGame
        //    version + AES key set differ, both of which come from config.
        var versions = new VersionContainer(cfg.UeVersion, cfg.TexturePlatform);
        var provider = new DefaultFileProvider(cfg.GameDirectory, SearchOption.AllDirectories, isCaseInsensitive: true, versions: versions);
        provider.ReadShaderMaps = true;   // needed so UMaterial deserializes inline shader maps
        provider.Initialize();

        // 3. Submit keys (main under the zero GUID + every dynamic key under
        //    its own GUID). CUE4Parse uses only the GUIDs it actually needs.
        int submitted = provider.SubmitKeys(BuildKeys(cfg));
        provider.PostMount();
        log($"[Headless] Mounted '{provider.ProjectName}' — VFS={provider.MountedVfs.Count}, files={provider.Files.Count}, keys submitted={submitted}.");

        // 4. Mappings — MANDATORY for UE5 IoStore material packages. Without a
        //    .usmap every material LoadPackage throws MappingException and the
        //    scan extracts zero materials (every shader -> UnknownMaterial).
        bool mappingsLoaded = LoadMappings(provider, cfg, log, logError);

        // Resolve /Game/ virtual aliases so package lookups by content path
        // succeed regardless of the on-disk mount-point spelling.
        try { provider.LoadVirtualPaths(); }
        catch (Exception ex) { logError($"[Headless] LoadVirtualPaths failed (continuing): {ex.Message}"); }

        // 5. Drive the shared per-archive pipeline over every matching
        //    .ushaderbytecode entry.
        var exportState = new ExportPipelineState
        {
            Provider = provider,
            ProjectOutputRoot = Path.Combine(cfg.RawDataDirectory, provider.ProjectName ?? "UnknownProject"),
            Log = log,
            LogError = logError,
        };

        List<GameFile> archives = provider.Files.Values
            .Where(f => IsTargetArchive(f, options))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        log($"[Headless] {archives.Count} shader archive(s) selected for export.");

        int processed = 0;
        foreach (GameFile entry in archives)
        {
            string exportBasePath = Path.Combine(cfg.RawDataDirectory, entry.PathWithoutExtension).Replace('\\', '/');
            log($"[Headless] ({processed + 1}/{archives.Count}) {entry.Path}");
            ShaderArchiveExporter.ProcessArchive(exportState, entry, exportBasePath, options.SplitVariants, options.SkipDecompile);
            processed++;
        }

        return new RunResult
        {
            ArchivesProcessed = processed,
            MaterialInterfaces = exportState.Root.MaterialInterfaces.Count,
            MappingsLoaded = mappingsLoaded,
            ProjectName = provider.ProjectName ?? string.Empty,
        };
    }

    private static bool IsTargetArchive(GameFile file, Options options)
    {
        if (!file.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase)) return false;
        if (options.SkipGlobal && file.Name.IndexOf("ShaderArchive-Global", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        IReadOnlyList<string>? filter = options.ArchiveNameFilter;
        if (filter == null || filter.Count == 0) return true;
        foreach (string token in filter)
        {
            if (file.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static IEnumerable<KeyValuePair<FGuid, FAesKey>> BuildKeys(HeadlessGameConfig cfg)
    {
        var keys = new List<KeyValuePair<FGuid, FAesKey>>();
        if (!string.IsNullOrWhiteSpace(cfg.MainAesKey))
            keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(cfg.MainAesKey)));
        foreach (HeadlessGameConfig.DynamicAesKey dk in cfg.DynamicKeys)
        {
            try { keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(dk.Guid), new FAesKey(dk.Key))); }
            catch { /* a malformed dynamic key is skipped; the rest still mount */ }
        }
        return keys;
    }

    private static void InitNativeCodecs(HeadlessGameConfig cfg, Action<string> log, Action<string> logError)
    {
        string dataDir = Path.Combine(cfg.OutputDirectory, ".data");
        Directory.CreateDirectory(dataDir);
        try
        {
            string oodlePath = Path.Combine(dataDir, OodleHelper.OODLE_NAME_OLD);
            if (!File.Exists(oodlePath)) oodlePath = Path.Combine(dataDir, OodleHelper.OODLE_NAME_CURRENT);
            OodleHelper.InitializeAsync(oodlePath).GetAwaiter().GetResult();
        }
        catch (Exception ex) { logError($"[Headless] Oodle init failed: {ex.Message}"); }

        try
        {
            string zlibPath = Path.Combine(dataDir, ZlibHelper.DLL_NAME);
            if (!File.Exists(zlibPath)) ZlibHelper.DownloadDllAsync(zlibPath).GetAwaiter().GetResult();
            ZlibHelper.InitializeAsync(zlibPath).GetAwaiter().GetResult();
        }
        catch (Exception ex) { logError($"[Headless] Zlib init failed: {ex.Message}"); }

        log("[Headless] Native codecs initialised (Oodle + Zlib).");
    }

    private static bool LoadMappings(AbstractVfsFileProvider provider, HeadlessGameConfig cfg, Action<string> log, Action<string> logError)
    {
        string? path = ResolveMappingsFile(cfg, log, logError);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            logError("[Headless] No .usmap mappings resolved — UE5 IoStore material packages will fail to deserialize (UnknownMaterial / no material-ball symbols). Provide a local .usmap or a reachable mapping endpoint.");
            return false;
        }

        provider.MappingsContainer = path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)
            ? new JmapTypeMappingsProvider(path)
            : new FileUsmapTypeMappingsProvider(path);
        log($"[Headless] Mappings loaded from '{Path.GetFileName(path)}'.");
        return true;
    }

    // Resolve the type-mappings file. Priority:
    //   1. explicit local override (endpoint.Overwrite + FilePath)
    //   2. newest cached *.usmap / *.jmap under <Output>/.data
    //   3. download from the mapping endpoint into <Output>/.data
    private static string? ResolveMappingsFile(HeadlessGameConfig cfg, Action<string> log, Action<string> logError)
    {
        if (!string.IsNullOrWhiteSpace(cfg.MappingLocalFile) && File.Exists(cfg.MappingLocalFile))
            return cfg.MappingLocalFile;

        string dataDir = Path.Combine(cfg.OutputDirectory, ".data");
        if (Directory.Exists(dataDir))
        {
            FileInfo? newest = new DirectoryInfo(dataDir)
                .EnumerateFiles("*.*")
                .Where(f => f.Extension.Equals(".usmap", StringComparison.OrdinalIgnoreCase)
                            || f.Name.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase)
                            || f.Name.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest != null) return newest.FullName;
        }

        if (!string.IsNullOrWhiteSpace(cfg.MappingEndpointUrl))
        {
            try { return DownloadMappings(cfg, dataDir, log); }
            catch (Exception ex) { logError($"[Headless] Mapping download failed: {ex.Message}"); }
        }
        return null;
    }

    private static string? DownloadMappings(HeadlessGameConfig cfg, string dataDir, Action<string> log)
    {
        Directory.CreateDirectory(dataDir);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ruri.FModelHook");
        string body = client.GetStringAsync(cfg.MappingEndpointUrl).GetAwaiter().GetResult();

        JToken token = JToken.Parse(body);
        JObject? entry = token switch
        {
            JArray arr when arr.Count > 0 => arr[0] as JObject,
            JObject obj => obj,
            _ => null,
        };
        string? url = (string?)entry?["url"] ?? (string?)entry?["Url"];
        string? fileName = (string?)entry?["filename"] ?? (string?)entry?["fileName"] ?? (string?)entry?["FileName"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName)) return null;

        string dest = Path.Combine(dataDir, fileName!);
        if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
        {
            byte[] bytes = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(dest, bytes);
            log($"[Headless] Downloaded mappings '{fileName}' ({bytes.Length / 1024} KB).");
        }
        return dest;
    }
}
