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
using Ruri.FModelHook.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ruri.FModelHook.ShaderDecompiler;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace Ruri.FModelHook.ShaderDecompiler.Headless;

public static class HeadlessMount
{
    public static AbstractVfsFileProvider MountProvider(HeadlessGameConfig cfg, Action<string> log, Action<string> logError, out bool mappingsLoaded)
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

    public static List<MaterialShaderLocation> FindShaderArchivesForMaterials(HeadlessGameConfig cfg, IReadOnlyList<string> materialPaths, Action<string> log, Action<string> logError) =>
        FindShaderArchivesForMaterials(MountProvider(cfg, log, logError, out _), materialPaths, log, logError);

    /// <summary>
    /// Where the compiled shader maps of the given materials live. Only those material packages
    /// are loaded and only the archives' header tables are read, so a host that just wants one
    /// character's shaders never pays for the rest of the install.
    /// </summary>
    public static List<MaterialShaderLocation> FindShaderArchivesForMaterials(AbstractVfsFileProvider provider, IReadOnlyList<string> materialPaths, Action<string> log, Action<string> logError)
    {
        var locations = new List<MaterialShaderLocation>();
        using ShaderMapCatalog catalog = ShaderMapCatalog.Open(provider, log, logError);
        foreach (string materialPath in materialPaths)
        {
            foreach (ShaderMapTarget target in new MaterialSubject(materialPath).Resolve(provider, log, logError))
            {
                MaterialShaderLocation location = new()
                {
                    MaterialPath = target.AssetPath,
                    OwningMaterialPath = target.OwningAssetPath,
                    ResourceHash = target.ShaderMapHash,
                };
                if (catalog.TryPlace(target.ShaderMapHash, out ShaderMapCatalog.Placement placement))
                {
                    location.ArchivePaths.Add(placement.ArchivePath);
                }
                locations.Add(location);
            }
        }

        foreach (MaterialShaderLocation location in locations)
        {
            string ownerNote = string.Equals(location.MaterialPath, location.OwningMaterialPath, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" (owned by parent template '{location.OwningMaterialPath}')";
            log($"[Headless]   {location.MaterialPath}{ownerNote} hash={location.ResourceHash} archives=[{string.Join(", ", location.ArchivePaths)}]");
        }
        return locations;
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
