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
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Shaders;
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
        // Enumerate target archives (name + size) and return WITHOUT exporting
        // or decompiling. The provider is still fully mounted (so the archive
        // list is the real one the export path would see), but Pass 005-200
        // never run — a fast way to pick a small in-game archive for a smoke test.
        public bool ListArchivesOnly { get; init; }
        // Build the cache + sidecars + .ushaderlib without decompiling (the
        // master archive's 261k-shader decompile is a multi-hour job; this lets
        // a fast --decompile-only iterate afterwards against the full cache).
        public bool SkipDecompile { get; init; }
        // Mount (full AppSettings-driven AES key set + mappings — the SAME
        // provider the shader export path uses, unlike --export-map-direct's
        // single-key mount which can't handle a game with 1000+ dynamic keys),
        // print every file path containing this substring, then return WITHOUT
        // touching the shader-archive pipeline at all. Asset discovery: find a
        // SkeletalMesh/material/texture's package path before exporting it.
        public string? FindAssetSubstring { get; init; }
        // Narrow decompile OUTPUT to shader-maps belonging to this material
        // (path substring match, see Pass150's MaterialPathVariants). The
        // incremental-export escape hatch for a huge archive (the master
        // archive's full decompile is a multi-hour, 261k-shader job): find
        // the archive that owns a specific material's shaders first (see
        // FindShaderArchivesForMaterials), then re-run scoped to JUST that
        // material so only its handful of shader-maps get decompiled. When
        // set, both the outer stale-output wipe here AND Pass180's own
        // recreate-dir logic skip clearing the shared Decompiled/<library>
        // folder, so a filtered run is additive on top of whatever a prior
        // full or differently-filtered run already emitted there.
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

    // Shared mount sequence (native codecs -> provider -> AES keys -> mappings
    // -> virtual paths) used by every headless entry point that needs the SAME
    // full AppSettings-driven provider — shader export (Run), asset discovery
    // (--find-asset), and direct asset export (ExportAssetPackages) all funnel
    // through this so a game needing 1000+ dynamic keys mounts identically
    // regardless of which command is invoked.
    private static AbstractVfsFileProvider MountProvider(HeadlessGameConfig cfg, Action<string> log, Action<string> logError, out bool mappingsLoaded)
    {
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
        mappingsLoaded = LoadMappings(provider, cfg, log, logError);

        // Resolve /Game/ virtual aliases so package lookups by content path
        // succeed regardless of the on-disk mount-point spelling.
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

        // --list-archives: enumerate the (real, post-mount) archive set and
        // return before any export work. Sorted by size so a smoke test can
        // grab the smallest in-game archive.
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

        // Wipe the `Decompiled/` output root up-front so a killed or prior run
        // can NEVER leave stale `.shader` files mixed with this run's fresh
        // output (user directive: 每次导出 shader 时清空目录). Pass180's per-archive
        // recreate only fires once that archive reaches the decompile phase, so
        // a mid-run kill (or an archive this run doesn't reach) used to keep
        // stale files around. Every archive shares one `Decompiled` root under
        // its Content dir; clear each distinct root exactly once before emitting.
        // Material-filtered runs are additive by design (see MaterialFilter's
        // doc comment) — skip the stale-output wipe entirely so a targeted
        // re-run never discards a prior full/other-filter export sharing the
        // same Decompiled/<library> folder.
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

    // Direct single/multi-asset export — mesh + material + texture, no shader
    // pipeline involved at all. Mounts through the SAME full AppSettings-driven
    // provider as Run/--find-asset, loads each given package, and for every
    // export CUE4Parse-Conversion's own `Exporter` class supports (the EXACT
    // same dispatch FModel's GUI "Export" button uses — UAnimSequence/
    // UAnimMontage/UAnimComposite/UMaterialInterface/USkeletalMesh/USkeleton/
    // UStaticMesh/ALandscapeProxy) writes it to `outputDir`. A SkeletalMesh
    // export cascades into its own referenced materials + their textures
    // automatically (`MeshExporter`/`MaterialExporter2` internals); packages
    // with no supported export (Blueprints, DataTables, UI assets that aren't
    // textures) are counted as skipped, not treated as an error.
    public static ExportAssetResult ExportAssetPackages(HeadlessGameConfig cfg, IReadOnlyList<string> packagePaths, string outputDir, CUE4Parse_Conversion.ExporterOptions exportOptions, Action<string> log, Action<string> logError)
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
                    var exporter = new CUE4Parse_Conversion.Exporter(export, exportOptions);
                    if (exporter.TryWriteToDir(outDir, out string label, out string savedFilePath))
                    {
                        result.ExportsWritten++;
                        log($"[Headless] --export-asset: wrote {label} -> {savedFilePath}");
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
        // The material that ACTUALLY owns the compiled shader-map. Often
        // different from MaterialPath: a MaterialInstance with only NUMERIC
        // parameter overrides (no static-switch override) reuses its PARENT's
        // exact compiled permutation and carries zero LoadedMaterialResources
        // of its own — the shader is genuinely shared with every sibling
        // instance of that parent template, not unique to this one instance.
        public string OwningMaterialPath { get; set; } = string.Empty;
        public string ResourceHash { get; set; } = string.Empty;
        public List<string> ArchivePaths { get; set; } = new();
    }

    // Targeted, INCREMENTAL alternative to the full Tier1 bridge scan (which
    // walks the whole container-header-owning package set, ~10 min cold):
    // when you already know exactly which material(s) you want, load ONLY
    // those packages (a handful of LoadPackage calls, seconds) to read their
    // authoritative inline `LoadedShaderMap.ResourceHash`, then check EVERY
    // mounted `.ushaderbytecode` archive's own `ShaderMapHashes` (a cheap
    // header-only read via `FShaderCodeArchive`, no code-body decompression)
    // for a match. Answers "which archive do I even need to decompile" without
    // touching the shader-decompile pipeline or the material-linking bridge at
    // all — the natural next step after --find-asset locates a material's
    // package path, before spending minutes decompiling a huge archive with
    // --material-filter.
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

                // Walk up the Parent chain when this instance owns no compiled
                // shader-map of its own (only numeric param overrides, no
                // static-switch divergence from its parent) — the shader IS
                // the parent template's, shared with every sibling instance.
                // Capped depth as a defensive guard against a malformed/cyclic
                // parent reference; a real UE material chain is a handful of
                // levels deep at most.
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

        // Detex — BC/ASTC/ETC block-compression decoder needed to write
        // texture pixels as PNG. The shader-export path never touches this
        // (it never decodes texture bytes), so it stayed uninitialized until
        // --export-asset needed it: every texture-bearing export (any
        // material, since MaterialExporter2 decodes its referenced textures)
        // threw "Detex decompression failed: not initialized" without this.
        // `LoadDll` extracts the embedded resource DLL if the cached copy is
        // missing (no network download needed, unlike Oodle/Zlib).
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
