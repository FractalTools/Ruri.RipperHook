using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class DependencyClosureExporter
{
    private const int MaxClosureDepth = 64;

    private readonly IFileProvider _provider;
    private readonly Action<string> _log;
    private readonly Action<string> _logError;
    private readonly SceneManifest _manifest;

    public DependencyClosureExporter(
        IFileProvider provider,
        Action<string> log,
        Action<string> logError,
        SceneManifest manifest)
    {
        _provider = provider;
        _log = log;
        _logError = logError;
        _manifest = manifest;
    }

    public void ExportClosure(IPackage? rootPackage, string assetsOutputDirectory)
    {
        Directory.CreateDirectory(assetsOutputDirectory);
        if (rootPackage is null)
        {
            _log("[GlbScene] Closure: no root package; skipped.");
            return;
        }

        ExportOptions exporterOptions = SbueExportOptions.Create(EMeshFormat.ActorX);

        var visited = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        var frontier = new List<IPackage> { rootPackage };
        visited[rootPackage.Name] = 1;

        SeedPathDiscoveredCompanionPackages(rootPackage, frontier, visited);

        int depth = 0;
        int closureCount = 0;
        var writeCounters = new ClosureWriteCounters();
        var droppedReasons = new ConcurrentBag<string>();
        var pendingManifestNotes = new ConcurrentBag<string>();

        while (frontier.Count > 0 && depth < MaxClosureDepth)
        {
            var nextFrontier = new ConcurrentBag<IPackage>();
            int frontierStart = closureCount;

            Parallel.ForEach(frontier, currentPackage =>
            {
                try
                {
                    ExportPackage(
                        currentPackage,
                        assetsOutputDirectory,
                        exporterOptions,
                        writeCounters);
                }
                catch (Exception ex)
                {
                    string reason = $"export-package '{currentPackage.Name}': {ex.Message}";
                    droppedReasons.Add(reason);
                    _logError($"[GlbScene] Closure export failed for '{currentPackage.Name}': {ex.Message}");
                }

                IEnumerable<string> referencedPackagePaths;
                try
                {
                    referencedPackagePaths = CollectReferencedPackagePaths(currentPackage, pendingManifestNotes);
                }
                catch (Exception ex)
                {
                    string reason = $"collect-refs '{currentPackage.Name}': {ex.Message}";
                    droppedReasons.Add(reason);
                    _logError($"[GlbScene] Closure ref-walk failed for '{currentPackage.Name}': {ex.Message}");
                    return;
                }

                foreach (string referencedPackagePath in referencedPackagePaths)
                {
                    if (string.IsNullOrEmpty(referencedPackagePath)) continue;
                    if (!visited.TryAdd(referencedPackagePath, 1)) continue;
                    if (!TryLoadPackageByPath(referencedPackagePath, out var referencedPackage)) continue;
                    nextFrontier.Add(referencedPackage);
                }
            });

            while (pendingManifestNotes.TryTake(out var note))
            {
                _manifest.Notes.Add(note);
            }

            foreach (var package in frontier)
            {
                _manifest.RecordAsset(package.Name);
                closureCount++;
            }

            int newCount = closureCount - frontierStart;
            _log($"[GlbScene] Closure depth {depth}: {newCount} package(s) ({nextFrontier.Count} new ref(s) queued).");

            frontier = new List<IPackage>(nextFrontier);
            depth++;
        }

        if (frontier.Count > 0)
        {
            foreach (var package in frontier)
            {
                _manifest.RecordDroppedAsset($"depth-limit ({MaxClosureDepth}) reached at '{package.Name}'");
            }
            _logError($"[GlbScene] Closure depth limit ({MaxClosureDepth}) reached; {frontier.Count} package(s) recorded as dropped.");
        }

        foreach (string reason in droppedReasons)
        {
            _manifest.RecordDroppedAsset(reason);
        }

        _log($"[GlbScene] Closure: {closureCount} asset package(s) walked, "
             + $"{writeCounters.WrittenJsonCount} JSON file(s), {writeCounters.WrittenBinaryCount} binary sidecar(s), "
             + $"{droppedReasons.Count} dropped -> {assetsOutputDirectory}");
    }

    private sealed class ClosureWriteCounters
    {
        private long _writtenJsonCount;
        private long _writtenBinaryCount;

        public long WrittenJsonCount => System.Threading.Interlocked.Read(ref _writtenJsonCount);
        public long WrittenBinaryCount => System.Threading.Interlocked.Read(ref _writtenBinaryCount);

        public void IncrementJsonWritten()
        {
            System.Threading.Interlocked.Increment(ref _writtenJsonCount);
        }

        public void IncrementBinaryWritten()
        {
            System.Threading.Interlocked.Increment(ref _writtenBinaryCount);
        }
    }

    private void ExportPackage(
        IPackage package,
        string assetsOutputDirectory,
        ExportOptions exporterOptions,
        ClosureWriteCounters writeCounters)
    {
        string packageRelativePath = StripLeadingSlash(package.Name);
        string packageJsonPath = Path.Combine(
            assetsOutputDirectory,
            packageRelativePath.Replace('/', Path.DirectorySeparatorChar) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(packageJsonPath)!);

        UObject[] exportSnapshot = MaterializeExports(package);

        try
        {
            string json = JsonConvert.SerializeObject(exportSnapshot, Formatting.Indented);
            File.WriteAllText(packageJsonPath, json);
            writeCounters.IncrementJsonWritten();
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Closure JSON write failed for '{package.Name}': {ex.Message}");
            return;
        }

        string binarySidecarDirectory = Path.Combine(
            assetsOutputDirectory,
            packageRelativePath.Replace('/', Path.DirectorySeparatorChar) + "_Files");
        DirectoryInfo? binarySidecarRoot = null;

        foreach (var export in exportSnapshot)
        {
            if (export is null) continue;
            try
            {
                if (TryExportBinarySidecar(export, exporterOptions, ref binarySidecarRoot, binarySidecarDirectory))
                {
                    writeCounters.IncrementBinaryWritten();
                }
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Closure binary sidecar failed for '{package.Name}::{export.Name}' ({export.ExportType}): {ex.Message}");
            }
        }
    }

    private UObject[] MaterializeExports(IPackage package)
    {
        var lazyExports = package.ExportsLazy;
        var materialized = new UObject[lazyExports.Length];
        for (int i = 0; i < lazyExports.Length; i++)
        {
            try
            {
                materialized[i] = lazyExports[i].Value;
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Closure export[{i}] of '{package.Name}' failed to deserialize: {ex.Message}");
                materialized[i] = null!;
            }
        }
        return materialized;
    }

    private bool TryExportBinarySidecar(
        UObject export,
        ExportOptions exporterOptions,
        ref DirectoryInfo? binarySidecarRoot,
        string binarySidecarDirectoryPath)
    {
        if (export is UStaticMesh
            or USkeletalMesh
            or USkeleton
            or UAnimSequence
            or UAnimMontage
            or UAnimComposite
            or UMaterialInterface
            or ALandscapeProxy)
        {
            binarySidecarRoot ??= EnsureBinarySidecarRoot(binarySidecarDirectoryPath);
            var session = new ExportSession();
            session.Add(export);
            IReadOnlyList<ExportResult> results = session
                .RunAsync(binarySidecarRoot.FullName, exporterOptions)
                .GetAwaiter()
                .GetResult();
            return results.Count > 0 && results[0].Success;
        }

        if (export is UTexture texture)
        {
            binarySidecarRoot ??= EnsureBinarySidecarRoot(binarySidecarDirectoryPath);
            return WriteTextureSidecar(texture, exporterOptions, binarySidecarRoot);
        }

        return false;
    }

    private static DirectoryInfo EnsureBinarySidecarRoot(string binarySidecarDirectoryPath)
    {
        Directory.CreateDirectory(binarySidecarDirectoryPath);
        return new DirectoryInfo(binarySidecarDirectoryPath);
    }

    private bool WriteTextureSidecar(UTexture texture, ExportOptions exporterOptions, DirectoryInfo binarySidecarRoot)
    {
        var decoded = TextureStripExport.Decode(texture, exporterOptions.TexturePlatform, out int slices);
        if (decoded is null) return false;

        byte[] imageBytes = decoded.Encode(exporterOptions.TextureFormat, exporterOptions.ExportHdrTexturesAsHdr, out string extension);
        if (imageBytes.Length == 0) return false;

        string safeName = MakeFilesystemSafe(texture.Name);
        string outputPath = Path.Combine(binarySidecarRoot.FullName, safeName + "." + extension);
        File.WriteAllBytes(outputPath, imageBytes);
        TextureStripExport.WriteSliceCount(outputPath, slices);
        TextureStripExport.WriteFloatSidecar(outputPath, decoded);
        return true;
    }

    private static string MakeFilesystemSafe(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unnamed";
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = c switch
            {
                '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' => '_',
                _ => c,
            };
        }
        return new string(buffer);
    }


    private IEnumerable<string> CollectReferencedPackagePaths(IPackage package, ConcurrentBag<string> pendingManifestNotes)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);

        CollectFormatNativeImports(package, references, pendingManifestNotes);

        CollectPropertyTreeReferences(package, references);

        return references;
    }

    private void CollectFormatNativeImports(IPackage package, HashSet<string> references, ConcurrentBag<string> pendingManifestNotes)
    {
        switch (package)
        {
            case CUE4Parse.UE4.Assets.IoPackage ioPackage:
            {
                CUE4Parse.UE4.Assets.IoPackage?[] importedPackages;
                try
                {
                    importedPackages = ioPackage.ImportedPackages.Value;
                }
                catch (Exception ex)
                {
                    _logError($"[GlbScene] Closure ImportedPackages.Value failed for '{package.Name}': {ex.Message}");
                    importedPackages = Array.Empty<CUE4Parse.UE4.Assets.IoPackage?>();
                }
                foreach (var importedPackage in importedPackages)
                {
                    if (importedPackage is null) continue;
                    string referencedName = importedPackage.Name;
                    if (string.IsNullOrEmpty(referencedName)) continue;
                    if (IsScriptPackagePath(referencedName)) continue;
                    references.Add(referencedName);
                }
                break;
            }

            case CUE4Parse.UE4.Assets.Package legacyPackage:
            {
                foreach (var import in legacyPackage.ImportMap)
                {
                    string? outerMostName = ResolveOutermostImportPackagePath(legacyPackage, import);
                    if (string.IsNullOrEmpty(outerMostName)) continue;
                    if (IsScriptPackagePath(outerMostName)) continue;
                    references.Add(outerMostName);
                }
                break;
            }

            default:
            {
                pendingManifestNotes.Add($"closure: unknown package impl '{package.GetType().FullName}' for '{package.Name}'; using property-tree references only.");
                break;
            }
        }
    }

    private static string? ResolveOutermostImportPackagePath(CUE4Parse.UE4.Assets.Package legacyPackage, FObjectImport import)
    {
        var outerMostIndex = import.OuterIndex;
        var outerMostImport = import;
        var importMap = legacyPackage.ImportMap;
        while (outerMostIndex is not null && !outerMostIndex.IsNull)
        {
            if (outerMostIndex.IsExport)
            {
                return null;
            }
            int arrayIndex = -outerMostIndex.Index - 1;
            if (arrayIndex < 0 || arrayIndex >= importMap.Length) return null;
            outerMostImport = importMap[arrayIndex];
            if (outerMostImport.OuterIndex is null || outerMostImport.OuterIndex.IsNull) break;
            outerMostIndex = outerMostImport.OuterIndex;
        }
        return outerMostImport.ObjectName.Text;
    }

    private void CollectPropertyTreeReferences(IPackage package, HashSet<string> references)
    {
        foreach (var lazyExport in package.ExportsLazy)
        {
            UObject? export;
            try
            {
                export = lazyExport.Value;
            }
            catch
            {
                continue;
            }
            if (export is null) continue;

            WalkPropertyHolderReferences(export, references);
        }
    }

    private void WalkPropertyHolderReferences(IPropertyHolder holder, HashSet<string> references)
    {
        foreach (var propertyTag in holder.Properties)
        {
            WalkPropertyTagTypeReferences(propertyTag.Tag, references);
        }
    }

    private void WalkPropertyTagTypeReferences(FPropertyTagType? tagType, HashSet<string> references)
    {
        if (tagType is null) return;

        switch (tagType)
        {
            case FPropertyTagType<FPackageIndex> objectProperty:
            {
                AddPackageIndexReference(objectProperty.Value, references);
                break;
            }

            case FPropertyTagType<FSoftObjectPath> softObjectProperty:
            {
                AddSoftObjectPathReference(softObjectProperty.Value, references);
                break;
            }

            case AssetObjectProperty assetObjectProperty:
            {
                AddPackagePathString(assetObjectProperty.Value, references);
                break;
            }

            case StrProperty stringProperty when LooksLikePackagePath(stringProperty.Value):
            {
                AddPackagePathString(stringProperty.Value, references);
                break;
            }

            case InterfaceProperty interfaceProperty when interfaceProperty.Value is { Object: { } scriptInterfaceObject }:
            {
                AddPackageIndexReference(scriptInterfaceObject, references);
                break;
            }

            case DelegateProperty delegateProperty when delegateProperty.Value is { Object: { } scriptDelegateObject }:
            {
                AddPackageIndexReference(scriptDelegateObject, references);
                break;
            }

            case MulticastDelegateProperty multicastDelegateProperty when multicastDelegateProperty.Value is { InvocationList: { } invocationList }:
            {
                foreach (var scriptDelegate in invocationList)
                {
                    if (scriptDelegate is null) continue;
                    AddPackageIndexReference(scriptDelegate.Object, references);
                }
                break;
            }

            case ArrayProperty arrayProperty when arrayProperty.Value is { Properties: { } arrayProperties }:
            {
                foreach (var element in arrayProperties)
                {
                    WalkPropertyTagTypeReferences(element, references);
                }
                break;
            }

            case SetProperty setProperty when setProperty.Value is { Properties: { } setProperties }:
            {
                foreach (var element in setProperties)
                {
                    WalkPropertyTagTypeReferences(element, references);
                }
                break;
            }

            case MapProperty mapProperty when mapProperty.Value is { Properties: { } mapProperties }:
            {
                foreach (var entry in mapProperties)
                {
                    WalkPropertyTagTypeReferences(entry.Key, references);
                    WalkPropertyTagTypeReferences(entry.Value, references);
                }
                break;
            }

            case OptionalProperty optionalProperty when optionalProperty.Value is { } innerProperty:
            {
                WalkPropertyTagTypeReferences(innerProperty, references);
                break;
            }

            case StructProperty structProperty when structProperty.Value is { StructType: { } structType }:
            {
                WalkStructTypeReferences(structType, references);
                break;
            }

            default:
                break;
        }
    }

    private void WalkStructTypeReferences(IUStruct structType, HashSet<string> references)
    {
        switch (structType)
        {
            case IPropertyHolder propertyHolder:
            {
                WalkPropertyHolderReferences(propertyHolder, references);
                break;
            }

            case FSoftObjectPath softObjectPath:
            {
                AddSoftObjectPathReference(softObjectPath, references);
                break;
            }

            case FInstancedStruct instancedStruct when instancedStruct.ScriptStruct?.StructType is { } innerStruct:
            {
                WalkStructTypeReferences(innerStruct, references);
                break;
            }

            default:
                break;
        }
    }

    private void AddPackageIndexReference(FPackageIndex? index, HashSet<string> references)
    {
        if (index is null || index.IsNull) return;
        if (index.IsExport) return;

        ResolvedObject? resolvedObject;
        try
        {
            resolvedObject = index.ResolvedObject;
        }
        catch
        {
            return;
        }

        string? packagePath = resolvedObject?.Package?.Name;
        if (string.IsNullOrEmpty(packagePath)) return;
        if (IsScriptPackagePath(packagePath)) return;
        references.Add(packagePath);
    }

    private void AddSoftObjectPathReference(FSoftObjectPath softObjectPath, HashSet<string> references)
    {
        AddPackagePathString(softObjectPath.AssetPathName.Text, references);
    }

    private static void AddPackagePathString(string? rawPath, HashSet<string> references)
    {
        if (string.IsNullOrEmpty(rawPath)) return;
        string packagePath = StripSubObjectSuffix(rawPath);
        if (string.IsNullOrEmpty(packagePath)) return;
        if (IsScriptPackagePath(packagePath)) return;
        references.Add(packagePath);
    }

    private static string StripSubObjectSuffix(string rawPath)
    {
        int colonIndex = rawPath.IndexOf(':');
        string head = colonIndex >= 0 ? rawPath[..colonIndex] : rawPath;
        int dotIndex = head.LastIndexOf('.');
        return dotIndex >= 0 ? head[..dotIndex] : head;
    }

    private static bool IsScriptPackagePath(string packagePath)
    {
        return packagePath.StartsWith("/Script/", StringComparison.Ordinal);
    }

    private static bool LooksLikePackagePath(string? text)
    {
        return !string.IsNullOrEmpty(text) && text[0] == '/' && text.IndexOf('.') > 0;
    }

    private static string StripLeadingSlash(string path)
    {
        return string.IsNullOrEmpty(path) ? path : path[0] == '/' ? path[1..] : path;
    }

    private void SeedPathDiscoveredCompanionPackages(IPackage rootPackage, List<IPackage> frontier, ConcurrentDictionary<string, byte> visited)
    {
        string? rootFileKey = TryDeriveFileKey(rootPackage);
        if (string.IsNullOrEmpty(rootFileKey)) return;

        string generatedCellPrefix = rootFileKey + "/";
        string? externalActorPrefix = BuildExternalActorPrefix(rootFileKey);
        var providerFiles = _provider.Files;

        foreach (var key in providerFiles.Keys)
        {
            string canonicalKey = StripExtension(key);
            bool isGeneratedCell = key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
                && canonicalKey.StartsWith(generatedCellPrefix, StringComparison.OrdinalIgnoreCase);
            bool isExternalActor = externalActorPrefix != null
                && key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && key.StartsWith(externalActorPrefix, StringComparison.OrdinalIgnoreCase);
            if (!isGeneratedCell && !isExternalActor) continue;

            if (!TryLoadPackageByGameFileKey(key, out var companionPackage)) continue;
            string companionName = companionPackage.Name;
            if (string.IsNullOrEmpty(companionName)) continue;
            if (!visited.TryAdd(companionName, 1)) continue;
            frontier.Add(companionPackage);
        }
    }

    private string? TryDeriveFileKey(IPackage rootPackage)
    {
        string rootPackagePath = rootPackage.Name;
        if (string.IsNullOrEmpty(rootPackagePath)) return null;

        try
        {
            string fixedPath = _provider.FixPath(rootPackagePath);
            return StripExtension(fixedPath);
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Closure FixPath failed for '{rootPackagePath}': {ex.Message}");
            return null;
        }
    }

    private static string? BuildExternalActorPrefix(string mainWorldFileKey)
    {
        const string contentSegment = "/Content/";
        int index = mainWorldFileKey.IndexOf(contentSegment, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        string head = mainWorldFileKey[..(index + "/Content".Length)];
        string rest = mainWorldFileKey[(index + contentSegment.Length)..];
        return $"{head}/__ExternalActors__/{rest}/";
    }

    private static string StripExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash ? path[..dot] : path;
    }

    private bool TryLoadPackageByGameFileKey(string gameFileKey, out IPackage package)
    {
        package = null!;
        try
        {
            if (_provider.Files.TryGetValue(gameFileKey, out var gameFile))
            {
                var loaded = _provider.LoadPackage(gameFile);
                if (loaded is not null)
                {
                    package = loaded;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Closure file-key load of '{gameFileKey}' failed: {ex.Message}");
        }
        return false;
    }

    private bool TryLoadPackageByPath(string packagePath, out IPackage package)
    {
        package = null!;
        try
        {
            if (_provider.TryLoadPackage(packagePath, out var loaded))
            {
                package = loaded;
                return loaded is not null;
            }
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Closure load of '{packagePath}' failed: {ex.Message}");
        }
        return false;
    }
}
