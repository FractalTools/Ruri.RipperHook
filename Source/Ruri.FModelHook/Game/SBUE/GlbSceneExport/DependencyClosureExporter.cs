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
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Writes the CLOSURE LAYER under `<output>/<map>/Assets/`.
//
// "Closure" = the transitive set of every asset the map's umap (and the cell
// worlds + external actors aggregated by WorldActorCollector) reference,
// directly or indirectly. The rule the user signed off on is "every byte" —
// not "every byte the renderer needs", not "every byte FModel can show", every
// asset the package graph touches must round-trip into <map>_Assets/.
//
// Sources of references walked, per package:
//   (1) FORMAT-NATIVE IMPORT LIST.
//       * IoStore packages (UE4.27+ cooked, UE5 default): the lazy
//         IoPackage.ImportedPackages[] is the engine's own resolved direct
//         import set (IoPackage.cs:201-209). Pulling it is constant-time and
//         hands us already-loaded IoPackages, no re-resolution.
//       * Legacy Package: iterate ImportMap[]; for every FObjectImport, walk
//         OuterIndex up to the outer-most FObjectImport, whose ObjectName is
//         the package mount path (Package.cs:254-271). Skip "/Script/*" since
//         those are code packages with no asset content.
//   (2) PROPERTY-TREE REFERENCES.
//       The format-native import list catches direct ImportMap entries, but a
//       cooked asset can also carry references inside its property tree:
//       FSoftObjectPath (SoftObjectProperty / SoftClassProperty), AssetObject
//       paths (string-backed), FPackageIndex inside delegate / interface /
//       script-struct payloads. Each export's property tree is walked end-to-
//       end so we never miss a soft reference the import list does not list.
//
// BFS frontier per depth is processed in parallel: package loading + property-
// tree walking are I/O-bound and CUE4Parse providers serialize their own
// shared state internally, so the worker fan-out is naturally safe. Visited
// is a ConcurrentDictionary keyed by the canonical package path (the same
// "/Mount/Path/Asset" key the provider hands out) so a graph cycle or a fan-
// in (a thousand mesh actors referencing the same material) is walked once.
//
// Per closure package the exporter writes:
//   * `<package>.json` — full property tree of every export via the same
//     UObjectConverter / FStructFallbackConverter / FPropertyTagTypeConverter
//     chain that FModel's `Save Properties` uses (CUE4ParseViewModel.cs:671:
//     `JsonConvert.SerializeObject(result.GetDisplayData(saveProperties),
//     Formatting.Indented)`). That is the byte-equivalent lossless dump.
//   * Format-native binary sidecar where CUE4Parse-Conversion has an Exporter
//     for the type — Mesh / Material / Anim / Landscape via `new Exporter(...)`
//     (IExporter.cs:117-130), Texture via `texture.Decode().Encode(...)` since
//     UTexture is not in the Exporter dispatch table but the decode+encode
//     pipeline is the same one MaterialExporter2 runs for sidecar PNGs
//     (MaterialExporter2.cs:58-69).
//
// Per-package failures are logged + recorded as manifest.RecordDroppedAsset
// (the audit-trail rule). The BFS continues so a single bad import never
// kills the whole closure.
public sealed class DependencyClosureExporter
{
    // Bounded BFS so a hostile / mis-resolved graph cannot recurse forever.
    // Cooked open-worlds in shipping titles measured here stay under ~6
    // hops from the umap to the deepest material parameter texture; 64 is a
    // generous ceiling that still bails on a stuck cycle.
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

        // Use the same default exporter options the render layer uses so a
        // mesh / material / texture exported here matches a render-layer
        // sidecar byte-for-byte if both happen to write the same asset.
        var exporterOptions = new ExporterOptions();

        // Visited set is keyed by IPackage.Name. The form differs across
        // package implementations — IoPackage.Name is the mount-path form
        // ("/Game/.../Asset"), legacy Package.Name is the file-key form
        // ("<Project>/Content/.../Asset"). Both are stable, monotonic per
        // package instance, and unique per asset, so a string compare is a
        // valid dedup key as long as we never mix the two within the same
        // BFS — which we don't, because every reference vehicle either
        // hands back an IPackage directly (whose .Name we read) or returns
        // a `/Mount/...` path the provider.TryLoadPackage routes through
        // the same load path the format-native import sources use.
        var visited = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // BFS frontier: starts with the world's own package so the root and
        // its directly-imported assets share the same per-depth fan-out.
        var frontier = new List<IPackage> { rootPackage };
        visited[rootPackage.Name] = 1;

        // World Partition cooks split a map across many sibling .umap cells
        // (path-discovered, NOT referenced by the root umap's ImportMap) and
        // hide every actor in standalone One-File-Per-Actor .uasset packages
        // under __ExternalActors__/<map>/ (also path-discovered). Without
        // these seeds the BFS would walk only what the persistent level
        // statically imports and miss every external actor's owned content.
        // WorldActorCollector already loaded them for the render + lossless
        // layers; we re-discover the SAME file keys here so the closure
        // catches each external-actor package's transitive asset closure.
        SeedPathDiscoveredCompanionPackages(rootPackage, frontier, visited);

        int depth = 0;
        int closureCount = 0;
        var writeCounters = new ClosureWriteCounters();
        var droppedReasons = new ConcurrentBag<string>();
        // Auxiliary log lines that need to land on _manifest.Notes — that
        // list is NOT thread-safe, so workers drop into this bag and the
        // parent thread folds it into the manifest after each depth slice.
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

            // Fold the worker-emitted notes into the manifest single-threaded.
            while (pendingManifestNotes.TryTake(out var note))
            {
                _manifest.Notes.Add(note);
            }

            // Manifest counters are filled here so we keep RecordAsset off the
            // hot fan-out path; the per-frontier count we add equals what the
            // workers wrote out in this depth slice.
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
            // Depth limit hit. Record the remaining frontier as dropped so the
            // audit log makes the cutoff visible; the rest of the run still
            // succeeds.
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

    // Cross-thread counters shared by every Parallel.ForEach worker. Pulled
    // into a class because C# does not let a lambda capture a ref local and
    // then forward it by ref to a helper, and we still want lock-free atomic
    // increments to keep the BFS hot path nailed to the file I/O it is doing.
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

    // Write the lossless JSON dump (every export's property tree, byte-
    // equivalent to FModel's `Save Properties` output) PLUS a format-native
    // binary sidecar for every export the CUE4Parse-Conversion Exporter knows
    // how to materialize. Both layers run for the same export — JSON is the
    // catch-all so even an exotic export type with no Exporter still round-
    // trips; the binary is the convenience for downstream tools that prefer
    // a native mesh / material / texture file.
    private void ExportPackage(
        IPackage package,
        string assetsOutputDirectory,
        ExporterOptions exporterOptions,
        ClosureWriteCounters writeCounters)
    {
        string packageRelativePath = StripLeadingSlash(package.Name);
        string packageJsonPath = Path.Combine(
            assetsOutputDirectory,
            packageRelativePath.Replace('/', Path.DirectorySeparatorChar) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(packageJsonPath)!);

        // Mirror FModel's "Save Properties" path: dump every export in the
        // package as a JSON array. Lazy serialization forces each export's
        // property tree, so a deferred export that has never been touched in
        // the render layer still resolves here.
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

        // Per-export binary sidecar: each export goes through `new Exporter(...)`
        // (Mesh / Material / Anim / Landscape) or the Decode/Encode pipeline
        // (Texture). Anything not in the dispatch table is skipped — the JSON
        // above is already the lossless representation, so a missing binary
        // sidecar is not data loss, just a missing convenience format.
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
                // Sidecar miss is recorded but does not abort the package —
                // the JSON has already landed and the BFS keeps going.
            }
        }
    }

    // CUE4Parse Lazy<UObject>[] requires reading .Value to force deserialization;
    // a failing export must NOT poison the whole package, so each slot is
    // tried independently and a null is materialized on failure (the JSON
    // serializer renders `null` for that slot, preserving the ExportMap
    // length and indices). The closure pass treats per-export failure as
    // local diagnostic noise, not a dropped-asset event, because the package
    // itself is still walked.
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

    // Dispatch table that mirrors CUE4Parse-Conversion's own Exporter switch
    // (IExporter.cs:117-130). Texture export is wired manually since UTexture
    // is not in that switch — the Decode/Encode pipeline (which is what
    // MaterialExporter2 already uses for sidecar PNGs) is the canonical
    // CUE4Parse texture-out path.
    private bool TryExportBinarySidecar(
        UObject export,
        ExporterOptions exporterOptions,
        ref DirectoryInfo? binarySidecarRoot,
        string binarySidecarDirectoryPath)
    {
        // Dispatch by run-time type. The first block routes everything the
        // CUE4Parse-Conversion Exporter() factory natively supports (mesh /
        // material / anim / landscape) through one call; the second handles
        // textures via Decode/Encode because UTexture is NOT in the factory's
        // switch but the decode pipeline is the canonical path.
        if (export is UStaticMesh
            or USkeletalMesh
            or USkeleton
            or UAnimSequence
            or UAnimMontage
            or UAnimComposite
            or UMaterialInterface
            or ALandscapeProxy)
        {
            var exporter = new Exporter(export, exporterOptions);
            binarySidecarRoot ??= EnsureBinarySidecarRoot(binarySidecarDirectoryPath);
            return exporter.TryWriteToDir(binarySidecarRoot, out _, out _);
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

    // Texture-only sidecar path. UTexture (and subtypes UTexture2D /
    // UTextureCube / UVolumeTexture / ULightMapTexture2D / ...) all expose
    // Decode + Encode through CUE4Parse_Conversion.Textures, the same pair
    // MaterialExporter2 uses (MaterialExporter2.cs:60-67). Failure paths
    // (no mip table / unsupported pixel format / null decode result) return
    // false so the JSON-only fallback remains visible in the manifest.
    private bool WriteTextureSidecar(UTexture texture, ExporterOptions exporterOptions, DirectoryInfo binarySidecarRoot)
    {
        var decoded = texture.Decode(exporterOptions.Platform);
        if (decoded is null) return false;

        byte[] imageBytes = decoded.Encode(exporterOptions.TextureFormat, exporterOptions.ExportHdrTexturesAsHdr, out string extension);
        if (imageBytes.Length == 0) return false;

        string safeName = MakeFilesystemSafe(texture.Name);
        string outputPath = Path.Combine(binarySidecarRoot.FullName, safeName + "." + extension);
        File.WriteAllBytes(outputPath, imageBytes);
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

    // ---- Reference collection ------------------------------------------------

    // Universal package reference harvest. Drives BFS by yielding the set of
    // package paths a single package references, regardless of legacy /
    // IoStore origin. The same canonical key the visited set uses lands in
    // the returned set so dedup is free.
    private IEnumerable<string> CollectReferencedPackagePaths(IPackage package, ConcurrentBag<string> pendingManifestNotes)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);

        // (1) Format-native import list. Both branches yield canonical
        // package paths the provider can re-load.
        CollectFormatNativeImports(package, references, pendingManifestNotes);

        // (2) Property-tree references. Walks every materialized export's
        // FPropertyTag tree and harvests FPackageIndex / FSoftObjectPath /
        // AssetObjectProperty / Interface / Delegate package paths. The
        // walk is type-driven and recurses through arrays / sets / maps /
        // optionals / struct fallbacks the same way the CUE4Parse JSON
        // serializer does, so anything the JSON dump would name is on the
        // ref list.
        CollectPropertyTreeReferences(package, references);

        return references;
    }

    private void CollectFormatNativeImports(IPackage package, HashSet<string> references, ConcurrentBag<string> pendingManifestNotes)
    {
        switch (package)
        {
            case CUE4Parse.UE4.Assets.IoPackage ioPackage:
            {
                // IoPackage exposes the lazy-resolved IoPackages directly
                // (IoPackage.cs:201-209). Each non-null entry is itself a
                // valid IPackage whose Name is the canonical path.
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
                // Legacy package: walk every FObjectImport up to the outer-
                // most import. The outer-most ObjectName is the package mount
                // path Unreal uses ("/Game/..."/"/Script/..."). Mirrors the
                // resolution path inside Package.cs:254-271.
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
                // Unknown IPackage implementation: fall through to the
                // property-tree pass only. Recorded as a note on the manifest
                // for downstream auditing (queued through pendingManifestNotes
                // because we are on a worker thread).
                pendingManifestNotes.Add($"closure: unknown package impl '{package.GetType().FullName}' for '{package.Name}'; using property-tree references only.");
                break;
            }
        }
    }

    private static string? ResolveOutermostImportPackagePath(CUE4Parse.UE4.Assets.Package legacyPackage, FObjectImport import)
    {
        // Mirrors the outer-chase inside Package.cs:254-271 with one extra
        // safeguard: if any step along the chain points back into an EXPORT
        // of this same package (the same branch Package.cs:262 short-
        // circuits on), the outermost is local — not a cross-package
        // reference — so we return null and the caller drops it from the
        // closure set.
        var outerMostIndex = import.OuterIndex;
        var outerMostImport = import;
        var importMap = legacyPackage.ImportMap;
        // Empty/null OuterIndex on the input means `import` itself is the
        // outermost entry and its ObjectName is already the package mount
        // path.
        while (outerMostIndex is not null && !outerMostIndex.IsNull)
        {
            if (outerMostIndex.IsExport)
            {
                // Local export reference, not a cross-package import.
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
                // Per-export deserialization failure is already logged by
                // MaterializeExports for the JSON pass; skip the property
                // walk here too.
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

    // Type-driven recursion that covers the same surface FModel's JSON
    // serializer renders for a property tag. Every reference vehicle the
    // engine emits into a cooked package is enumerated explicitly; new
    // property kinds added later in CUE4Parse will fall through the default
    // arm silently (the format-native import list still catches their direct
    // imports, so closure correctness only degrades on a soft reference the
    // import list does not list — at which point the new property kind must
    // be added here).
    private void WalkPropertyTagTypeReferences(FPropertyTagType? tagType, HashSet<string> references)
    {
        if (tagType is null) return;

        switch (tagType)
        {
            // FPackageIndex-backed properties: ObjectProperty + its derived
            // ClassProperty / WeakObjectProperty cover the typed reference
            // cases the engine cooks. ResolvedObject is a Lazy resolve; the
            // package path is the outermost-import-mount name of the resolved
            // owner.
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

            // AssetObjectProperty / AssetClassProperty: serialized as a plain
            // path string ("/Game/.../Asset.AssetName"); strip the sub-object
            // suffix and treat the head as a package path.
            case AssetObjectProperty assetObjectProperty:
            {
                AddPackagePathString(assetObjectProperty.Value, references);
                break;
            }

            case StrProperty stringProperty when LooksLikePackagePath(stringProperty.Value):
            {
                // SoftObjectPath in older cooks falls back to a raw FString
                // payload via StrProperty; treat it the same way.
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
                // Primitive kinds (bool / int / float / FName / FText / byte /
                // ...) carry no package reference. The format-native import
                // list still catches their owning packages' direct imports.
                break;
        }
    }

    // FScriptStruct.StructType is an IUStruct; the loaded payload is either
    // an FStructFallback (which is an IPropertyHolder we can recurse) or a
    // hand-rolled struct type. Hand-rolled types that themselves carry
    // reference fields (FSoftObjectPath, FPackageIndex, FInstancedStruct, ...)
    // are dispatched here. Anything not enumerated falls through silently —
    // the property-tree walk catches references in the parent property too,
    // so the only loss vector is a struct that holds a reference inside a
    // field that is neither IPropertyHolder nor one of the explicit types
    // below. Add cases here when such a struct surfaces in a real cook.
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

            case FInstancedStruct instancedStruct when instancedStruct.NonConstIUSturct is { } innerStruct:
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
        // Exports point inside this package; they are NOT references to walk.
        if (index.IsExport) return;

        // ResolvedObject is Lazy — it walks the outer chain inside CUE4Parse
        // up to the package node, so .Package.Name is the canonical owner
        // path. Failures inside the resolver are non-fatal here (the import
        // list pass already handled the direct-import case).
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
        // FSoftObjectPath.AssetPathName encodes "/Mount/.../Asset.AssetName".
        // Strip the trailing `.AssetName` (or `:SubObject`) so the closure
        // queues the *package*, not a sub-object.
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
        // "/Game/.../Asset.SubObject:NestedThing" -> "/Game/.../Asset".
        // The colon-form is Unreal's sub-path separator (FSoftObjectPath.ToString:
        // `$"{AssetPathName.Text}:{SubPathString}"`); the dot-form is the
        // outer.export delimiter inside a package.
        int colonIndex = rawPath.IndexOf(':');
        string head = colonIndex >= 0 ? rawPath[..colonIndex] : rawPath;
        int dotIndex = head.LastIndexOf('.');
        return dotIndex >= 0 ? head[..dotIndex] : head;
    }

    private static bool IsScriptPackagePath(string packagePath)
    {
        // Code packages have no asset content; the engine never cooks an
        // FObjectImport into "/Script/Engine.UStaticMesh" as a thing to
        // serialize, so chasing them is pointless and pollutes the closure.
        return packagePath.StartsWith("/Script/", StringComparison.Ordinal);
    }

    private static bool LooksLikePackagePath(string? text)
    {
        // The single string-typed reference vehicle (legacy AssetObjectPath
        // serialized as StrProperty) starts with the Unreal mount separator.
        // Plain user strings never do, so this is a cheap negative filter.
        return !string.IsNullOrEmpty(text) && text[0] == '/' && text.IndexOf('.') > 0;
    }

    private static string StripLeadingSlash(string path)
    {
        return string.IsNullOrEmpty(path) ? path : path[0] == '/' ? path[1..] : path;
    }

    // Seed the BFS with any path-discovered World Partition / OFPA companion
    // packages whose existence the umap's ImportMap does NOT document.
    // Mirrors WorldActorCollector.ScanProviderFiles + BuildExternalActorPrefix
    // (WorldActorCollector.cs:285-322) so the closure walks exactly the
    // packages the render + lossless layers already see.
    //
    // For each discovered file key:
    //   * try the provider load — fall back silently if the file is empty /
    //     unreadable; the WorldActorCollector run already logged the same.
    //   * mark in visited and append to frontier so depth-0 fans them out
    //     alongside the root umap's direct imports.
    //
    // Path forms:
    //   * Legacy Package.Name == file-key-minus-extension already (e.g.
    //     "Oni_Valley_VFX/Content/.../Oni_Valley") so we can scan Files
    //     directly with it.
    //   * IoPackage.Name == mount path (e.g. "/Game/Oni_Project/.../Oni_Valley");
    //     the provider's FixPath translates "/Game" to "<ProjectName>/Content"
    //     which is what the Files keys look like for IoStore-cooked content.
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

    // Map IPackage.Name to the provider Files key form. For legacy Package
    // the name is already file-key-minus-extension; for IoPackage it is the
    // mount path that the provider's FixPath() converts to "<Project>/Content/..."
    // (AbstractFileProvider.cs:476-520). The trailing ".uasset" the FixPath
    // appends to extensionless input is stripped here so the result matches
    // the StripExtension form used by the WorldActorCollector scan.
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

    // "<ProjectName>/Content/Maps/MyMap" -> "<ProjectName>/Content/__ExternalActors__/Maps/MyMap/".
    // 1:1 of WorldActorCollector.BuildExternalActorPrefix (WorldActorCollector.cs:313-322).
    // Mirrors Unreal's OFPA layout: "/Game/Maps/MyMap" becomes
    // "/Game/__ExternalActors__/Maps/MyMap", and "/Game" maps to "<Project>/Content"
    // in provider Files keys.
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
            // The same lookup CUE4Parse internal resolvers use; works for both
            // IoStore and legacy chunked containers because the provider hides
            // the format.
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
