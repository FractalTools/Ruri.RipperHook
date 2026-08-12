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
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ruri.FModelHook.Game.SBUE.ShaderDecompiler;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace Ruri.FModelHook.Game.SBUE.Headless;

public static class HeadlessShaderExportRunner
{
    public sealed class Options
    {
        public required HeadlessGameConfig Config { get; init; }
        public IReadOnlyList<string>? ArchiveNameFilter { get; init; }
        public bool SkipGlobal { get; init; }
        public bool SplitVariants { get; init; }
        public bool ListArchivesOnly { get; init; }
        public bool SkipDecompile { get; init; }
        public string? FindAssetSubstring { get; init; }
        public string? MaterialFilter { get; init; }
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

    private static AbstractVfsFileProvider MountProvider(HeadlessGameConfig cfg, Action<string> log, Action<string> logError, out bool mappingsLoaded)
    {
        if (cfg.HasUnsupportedVersioning)
            logError("[Headless] WARNING: this game's settings carry custom version/option/map-struct overrides which the headless mount does not yet replicate. Mount may misparse — fall back to the GUI if assets fail to load.");

        InitNativeCodecs(cfg, log, logError);

        var versions = new VersionContainer(cfg.UeVersion, cfg.TexturePlatform);
        var provider = new DefaultFileProvider(cfg.GameDirectory, SearchOption.AllDirectories, isCaseInsensitive: true, versions: versions);
        provider.ReadShaderMaps = true;        provider.Initialize();

        int submitted = provider.SubmitKeys(BuildKeys(cfg));
        provider.PostMount();
        log($"[Headless] Mounted '{provider.ProjectName}' — VFS={provider.MountedVfs.Count}, files={provider.Files.Count}, keys submitted={submitted}.");

        mappingsLoaded = LoadMappings(provider, cfg, log, logError);

        try { provider.LoadVirtualPaths(); }
        catch (Exception ex) { logError($"[Headless] LoadVirtualPaths failed (continuing): {ex.Message}"); }

        return provider;
    }

    public static RunResult Run(Options options)
    {
        HeadlessGameConfig cfg = options.Config;
        Action<string> log = options.Log;
        Action<string> logError = options.LogError;

        AbstractVfsFileProvider provider = MountProvider(cfg, log, logError, out bool mappingsLoaded);

        if (!string.IsNullOrWhiteSpace(options.FindAssetSubstring))
        {
            var matches = provider.Files.Keys
                .Where(k => k.IndexOf(options.FindAssetSubstring!, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            log($"[Headless] --find-asset '{options.FindAssetSubstring}': {matches.Count} match(es).");
            foreach (string m in matches) log($"[Headless]   {m}");
            return new RunResult { MappingsLoaded = mappingsLoaded, ProjectName = provider.ProjectName ?? string.Empty };
        }

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

        if (options.ListArchivesOnly)
        {
            foreach (GameFile entry in archives.OrderBy(f => f.Size))
            {
                log($"[Headless]   {entry.Size,12:N0}  {entry.Path}");
            }
            return new RunResult
            {
                ArchivesProcessed = 0,
                MaterialInterfaces = 0,
                MappingsLoaded = mappingsLoaded,
                ProjectName = provider.ProjectName ?? string.Empty,
            };
        }

        if (string.IsNullOrWhiteSpace(options.MaterialFilter))
        {
            var clearedDecompiledRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameFile entry in archives)
            {
                string ebp = Path.Combine(cfg.RawDataDirectory, entry.PathWithoutExtension).Replace('\\', '/');
                string decompiledRoot = Path.Combine(Path.GetDirectoryName(ebp)!, "Decompiled");
                if (!clearedDecompiledRoots.Add(decompiledRoot) || !Directory.Exists(decompiledRoot)) continue;
                try
                {
                    Directory.Delete(decompiledRoot, true);
                    log($"[Headless] Cleared stale decompiled output: {decompiledRoot}");
                }
                catch (Exception ex)
                {
                    logError($"[Headless] Failed to clear decompiled output {decompiledRoot}: {ex.Message}");
                }
            }
        }

        int processed = 0;
        foreach (GameFile entry in archives)
        {
            string exportBasePath = Path.Combine(cfg.RawDataDirectory, entry.PathWithoutExtension).Replace('\\', '/');
            log($"[Headless] ({processed + 1}/{archives.Count}) {entry.Path}");
            ShaderArchiveExporter.ProcessArchive(exportState, entry, exportBasePath, options.SplitVariants, options.SkipDecompile, options.MaterialFilter);
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

    public sealed class ExportAssetResult
    {
        public int PackagesLoaded { get; set; }
        public int ExportsWritten { get; set; }
        public int ExportsSkippedUnsupported { get; set; }
        public bool MappingsLoaded { get; set; }
    }

    public static ExportAssetResult ExportAssetPackages(HeadlessGameConfig cfg, IReadOnlyList<string> packagePaths, string outputDir, CUE4Parse_Conversion.Options.ExportOptions exportOptions, Action<string> log, Action<string> logError)
    {
        AbstractVfsFileProvider provider = MountProvider(cfg, log, logError, out bool mappingsLoaded);
        var result = new ExportAssetResult { MappingsLoaded = mappingsLoaded };
        var outDir = new DirectoryInfo(outputDir);
        Directory.CreateDirectory(outputDir);

        foreach (string packagePath in packagePaths)
        {
            CUE4Parse.UE4.Assets.IPackage package;
            try
            {
                package = provider.LoadPackage(packagePath);
                result.PackagesLoaded++;
            }
            catch (Exception ex)
            {
                logError($"[Headless] --export-asset: failed to load '{packagePath}': {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (CUE4Parse.UE4.Assets.Exports.UObject export in package.GetExports())
            {
                try
                {
                    if (export is CUE4Parse.UE4.Assets.Exports.Texture.UTexture texture)
                    {
                        CUE4Parse_Conversion.Textures.CTexture? decoded =
                            TextureStripExport.Decode(texture, exportOptions.TexturePlatform, out int slices);
                        if (decoded is null)
                        {
                            logError($"[Headless] --export-asset: '{export.Name}' decode returned null (unsupported pixel format?).");
                            result.ExportsSkippedUnsupported++;
                            continue;
                        }
                        byte[] imageBytes = decoded.Encode(exportOptions.TextureFormat, exportOptions.ExportHdrTexturesAsHdr, out string extension);
                        if (imageBytes.Length == 0)
                        {
                            logError($"[Headless] --export-asset: '{export.Name}' encoded to 0 bytes.");
                            result.ExportsSkippedUnsupported++;
                            continue;
                        }
                        Directory.CreateDirectory(outDir.FullName);
                        string texturePath = Path.Combine(outDir.FullName, export.Name + "." + extension);
                        File.WriteAllBytes(texturePath, imageBytes);
                        TextureStripExport.WriteSliceCount(texturePath, slices);
                        TextureStripExport.WriteFloatSidecar(texturePath, decoded);
                        result.ExportsWritten++;
                        log($"[Headless] --export-asset: wrote texture {export.Name} " +
                            $"({texture.Format}, {decoded.Width}x{decoded.Height}{(slices > 1 ? $" 条带×{slices}" : "")}) -> {texturePath}");
                        continue;
                    }

                    if (export.ExportType is "MaterialParameterCollection")
                    {
                        Directory.CreateDirectory(outDir.FullName);
                        string mpcPath = Path.Combine(outDir.FullName, export.Name + ".json");
                        File.WriteAllText(mpcPath, JsonConvert.SerializeObject(export, Formatting.Indented));
                        result.ExportsWritten++;
                        log($"[Headless] --export-asset: wrote MaterialParameterCollection {export.Name} -> {mpcPath}");
                        continue;
                    }

                    var session = new CUE4Parse_Conversion.ExportSession();
                    session.Add(export);
                    var exportResults = session
                        .RunAsync(outDir.FullName, exportOptions)
                        .GetAwaiter()
                        .GetResult();
                    if (exportResults.Count > 0 && exportResults[0] is { Success: true } exportResult)
                    {
                        result.ExportsWritten++;
                        string savedFilePath = exportResult.DiskFilePaths is { Count: > 0 } diskPaths
                            ? string.Join(", ", diskPaths)
                            : outDir.FullName;
                        log($"[Headless] --export-asset: wrote {export.Name} -> {savedFilePath}");
                    }
                    else
                    {
                        logError($"[Headless] --export-asset: '{export.Name}' ({export.ExportType}) from '{packagePath}' failed to write.");
                    }
                }
                catch (NotSupportedException)
                {
                    result.ExportsSkippedUnsupported++;
                }
                catch (Exception ex)
                {
                    logError($"[Headless] --export-asset: '{export.Name}' ({export.ExportType}) from '{packagePath}' threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return result;
    }

    public sealed class MaterialShaderLocation
    {
        public string MaterialPath { get; set; } = string.Empty;
        public string OwningMaterialPath { get; set; } = string.Empty;
        public string ResourceHash { get; set; } = string.Empty;
        public List<string> ArchivePaths { get; set; } = new();
    }

    public static List<MaterialShaderLocation> FindShaderArchivesForMaterials(HeadlessGameConfig cfg, IReadOnlyList<string> materialPaths, Action<string> log, Action<string> logError)
    {
        AbstractVfsFileProvider provider = MountProvider(cfg, log, logError, out _);

        var locations = new List<MaterialShaderLocation>();
        var allTargetHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string materialPath in materialPaths)
        {
            CUE4Parse.UE4.Assets.IPackage package;
            try
            {
                package = provider.LoadPackage(materialPath);
            }
            catch (Exception ex)
            {
                logError($"[Headless] --find-shader-for-material: failed to load '{materialPath}': {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (CUE4Parse.UE4.Assets.Exports.UObject export in package.GetExports())
            {
                if (export is not UMaterialInterface material) continue;

                UMaterialInterface owner = material;
                int depth = 0;
                while ((owner.LoadedMaterialResources == null || owner.LoadedMaterialResources.Count == 0)
                       && owner is UMaterialInstance instance && instance.Parent != null && depth < 16)
                {
                    owner = (UMaterialInterface)instance.Parent;
                    depth++;
                }

                if (owner.LoadedMaterialResources == null || owner.LoadedMaterialResources.Count == 0)
                {
                    log($"[Headless]   {materialPath}: no compiled shader-map found up the parent chain (walked {depth} level(s), stopped at '{owner.Name}').");
                    continue;
                }

                string ownerPath = ReferenceEquals(owner, material) ? materialPath : (owner.GetPathName());
                foreach (var resource in owner.LoadedMaterialResources)
                {
                    FMaterialShaderMap? shaderMap = resource.LoadedShaderMap;
                    if (shaderMap == null) continue;
                    string? hash = shaderMap.ResourceHash?.ToString() ?? shaderMap.Code?.ResourceHash.ToString();
                    if (string.IsNullOrWhiteSpace(hash)) continue;
                    allTargetHashes.Add(hash);
                    locations.Add(new MaterialShaderLocation { MaterialPath = materialPath, OwningMaterialPath = ownerPath, ResourceHash = hash });
                }
            }
        }

        if (allTargetHashes.Count == 0)
        {
            log("[Headless] --find-shader-for-material: no ResourceHash resolved for any given material (inline shader map missing?).");
            return locations;
        }

        foreach (GameFile file in provider.Files.Values)
        {
            if (!file.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase)) continue;
            HashSet<string> archiveHashes;
            try
            {
                var headerAr = file.CreateReader();
                var archive = new FShaderCodeArchive(headerAr);
                if (archive.SerializedShaders is not FIoStoreShaderCodeArchive ioArchive) continue;
                archiveHashes = ioArchive.ShaderMapHashes.Select(h => h.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logError($"[Headless] --find-shader-for-material: failed to read archive header '{file.Path}': {ex.Message}");
                continue;
            }

            foreach (MaterialShaderLocation loc in locations)
            {
                if (archiveHashes.Contains(loc.ResourceHash)) loc.ArchivePaths.Add(file.Path);
            }
        }

        foreach (MaterialShaderLocation loc in locations)
        {
            string ownerNote = string.Equals(loc.MaterialPath, loc.OwningMaterialPath, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" (owned by parent template '{loc.OwningMaterialPath}')";
            log($"[Headless]   {loc.MaterialPath}{ownerNote} hash={loc.ResourceHash} archives=[{string.Join(", ", loc.ArchivePaths)}]");
        }
        return locations;
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
            catch {}
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

        try
        {
            string detexPath = Path.Combine(dataDir, CUE4Parse_Conversion.Textures.BC.DetexHelper.DLL_NAME);
            CUE4Parse_Conversion.Textures.BC.DetexHelper.LoadDll(detexPath);
            CUE4Parse_Conversion.Textures.BC.DetexHelper.Initialize(detexPath);
        }
        catch (Exception ex) { logError($"[Headless] Detex init failed: {ex.Message}"); }

        log("[Headless] Native codecs initialised (Oodle + Zlib + Detex).");
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
