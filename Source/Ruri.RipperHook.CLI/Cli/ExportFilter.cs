using System.Reflection;
using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using Ruri.RipperHook.Bridge;
using MonoModHook = MonoMod.RuntimeDetour.Hook;

namespace Ruri.RipperHook.CLI;

internal static class ExportFilter
{
    public sealed record Failure(string Name, int? ClassId, string Error, string Stack);

    /// <summary>
    /// What instantiating one exported prefab needs: the pointer a scene's PrefabInstance
    /// names as its source, and the pointer to the prefab's ROOT Transform — the object a
    /// placement's position/rotation/scale modifications have to target.
    /// </summary>
    public sealed record PrefabExport(string RootName, MetaPtr SourcePrefab, MetaPtr RootTransform);

    public static HashSet<int> AllowedClassIds { get; private set; } = new();
    public static Regex[] NameRegexes { get; private set; } = [];
    public static int SmokeTestLimit { get; private set; }
    public static bool FailFast { get; private set; } = true;

    public static int Considered { get; private set; }
    public static int Exported { get; private set; }
    public static Dictionary<int, int> ExportedByType { get; } = new();
    public static List<Failure> Failures { get; } = new();

    /// <summary>
    /// Every asset this export wrote, recorded from the LIVE object as its collection was
    /// serialized — see <see cref="ExportedAssetIndex"/> for why the files left on disk
    /// cannot be joined on afterwards. Null unless a caller asked to capture; building it
    /// costs a pointer per exported asset, which only a scene write has any use for.
    /// </summary>
    public static ExportedAssetIndex? Captured { get; private set; }

    /// <summary>
    /// Normalized container path -> what a scene needs to instantiate that prefab. Keyed by
    /// EVERY path the prefab's own assets are filed under, not just one: a placement names
    /// the prefab, and which object inside the collection carries that path is the exporter's
    /// business, not a thing to guess at from this side.
    /// </summary>
    public static Dictionary<string, PrefabExport> CapturedPrefabs { get; } = new(StringComparer.Ordinal);

    private static MonoModHook? _exportHook;
    private static bool _enabled;

    public static void Configure(HashSet<int> allowedClassIds, Regex[] names, int smokeTestLimit,
        bool failFast, bool capture = false)
    {
        AllowedClassIds = allowedClassIds;
        NameRegexes = names;
        SmokeTestLimit = smokeTestLimit;
        FailFast = failFast;
        Considered = 0;
        Exported = 0;
        ExportedByType.Clear();
        Failures.Clear();
        Captured = capture ? new ExportedAssetIndex() : null;
        CapturedPrefabs.Clear();
        _enabled = true;
    }

    public static void Install()
    {
        if (_exportHook != null) return;

        var method = typeof(ProjectExporter).GetMethod(nameof(ProjectExporter.Export),
            new[] { typeof(GameBundle), typeof(CoreConfiguration), typeof(FileSystem) });
        if (method == null)
        {
            Logger.Warning(LogCategory.Export, "ExportFilter: ProjectExporter.Export not found; filtering disabled.");
            return;
        }

        _exportHook = new MonoModHook(method,
            (Action<Action<ProjectExporter, GameBundle, CoreConfiguration, FileSystem>, ProjectExporter, GameBundle, CoreConfiguration, FileSystem>)
            ((orig, self, fileCollection, options, fileSystem) =>
            {
                if (!_enabled)
                {
                    orig(self, fileCollection, options, fileSystem);
                    return;
                }

                FilteredExport(self, fileCollection, options, fileSystem);
            }));
    }

    private static void FilteredExport(ProjectExporter exporter, GameBundle bundle, CoreConfiguration options, FileSystem fileSystem)
    {
        var type = typeof(ProjectExporter);
        var createCollections = type.GetMethod("CreateCollections", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ProjectExporter.CreateCollections not found");
        var preStarted = type.GetField(nameof(ProjectExporter.EventExportPreparationStarted), BindingFlags.NonPublic | BindingFlags.Instance);
        var preFinished = type.GetField(nameof(ProjectExporter.EventExportPreparationFinished), BindingFlags.NonPublic | BindingFlags.Instance);
        var started = type.GetField(nameof(ProjectExporter.EventExportStarted), BindingFlags.NonPublic | BindingFlags.Instance);
        var progress = type.GetField(nameof(ProjectExporter.EventExportProgressUpdated), BindingFlags.NonPublic | BindingFlags.Instance);
        var finished = type.GetField(nameof(ProjectExporter.EventExportFinished), BindingFlags.NonPublic | BindingFlags.Instance);

        Invoke(preStarted, exporter);
        var collectionsObj = createCollections.Invoke(exporter, new object[] { bundle });
        var collections = ((IEnumerable<IExportCollection>)collectionsObj!).ToList();
        Invoke(preFinished, exporter);

        Invoke(started, exporter);

        var containerType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("AssetRipper.Export.UnityProjects.ProjectAssetContainer"))
            .FirstOrDefault(t => t != null)
            ?? throw new InvalidOperationException("ProjectAssetContainer not found");

        var container = (IExportContainer)Activator.CreateInstance(containerType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { exporter, options, bundle.FetchAssets(), collections },
            culture: null)!;

        var currentCollectionField = containerType.GetProperty("CurrentCollection")
            ?? throw new InvalidOperationException("ProjectAssetContainer.CurrentCollection not found");

        int exportableCount = collections.Count(c => c.Exportable);
        int currentExportable = 0;
        int considered = 0;
        int exported = 0;

        for (int i = 0; i < collections.Count; i++)
        {
            IExportCollection collection = collections[i];
            currentCollectionField.SetValue(container, collection);
            if (!collection.Exportable)
            {
                InvokeProgress(progress, exporter, i, collections.Count);
                continue;
            }

            currentExportable++;
            considered++;
            Considered++;

            if (!CollectionMatches(collection, out int? primaryClassId))
            {
                InvokeProgress(progress, exporter, i, collections.Count);
                continue;
            }

            if (SmokeTestLimit > 0 && primaryClassId is int limitClassId)
            {
                if (ExportedByType.TryGetValue(limitClassId, out int already) && already >= SmokeTestLimit)
                {
                    InvokeProgress(progress, exporter, i, collections.Count);
                    continue;
                }
            }

            Logger.Info(LogCategory.ExportProgress, $"({currentExportable}/{exportableCount}) Exporting '{collection.Name}'");
            try
            {
                bool ok = collection.Export(container, options.ProjectRootPath, fileSystem);
                if (!ok)
                {
                    Logger.Warning(LogCategory.ExportProgress, $"Failed to export '{collection.Name}' ({collection.GetType().Name})");
                }
                else
                {
                    exported++;
                    Exported++;
                    if (primaryClassId is int classId)
                    {
                        ExportedByType[classId] = ExportedByType.GetValueOrDefault(classId) + 1;
                    }
                    if (Captured is not null)
                    {
                        Capture(Captured, container, collection);
                    }
                }
            }
            catch (Exception ex)
            {
                Failures.Add(new Failure(collection.Name, primaryClassId, $"{ex.GetType().Name}: {ex.Message}", ex.ToString()));
                Logger.Error(LogCategory.ExportProgress, $"Failed to export '{collection.Name}' ({ex.GetType().Name}: {ex.Message})", ex);
                if (FailFast)
                {
                    InvokeFinished(finished, exporter);
                    throw;
                }
            }

            InvokeProgress(progress, exporter, i, collections.Count);
        }

        InvokeFinished(finished, exporter);
    }

    /// <summary>
    /// Record what this collection just wrote, from the objects themselves: the asset's own
    /// name and the exporter's own <c>{fileID, guid}</c> pointer at it. Taken HERE, while the
    /// collection is still in hand, because the file names it leaves behind are sanitized,
    /// de-suffixed and uniquified — a name is not recoverable from them.
    /// </summary>
    private static void Capture(ExportedAssetIndex index, IExportContainer container, IExportCollection collection)
    {
        foreach (IUnityObjectBase asset in collection.Assets)
        {
            MetaPtr pointer;
            try
            {
                pointer = collection.CreateExportPointer(container, asset, isLocal: false);
            }
            catch (Exception ex)
            {
                Logger.Debug(LogCategory.Export, $"ExportFilter: no pointer for '{asset.GetBestName()}' ({ex.GetType().Name})");
                continue;
            }
            if (pointer.GUID.IsZero)
            {
                continue;
            }
            index.Add(asset.OriginalPath, asset.GetBestName(), (int)asset.ClassID,
                pointer.FileID, pointer.GUID.ToString(), (int)pointer.AssetType);
        }

        // A prefab is instantiated, not referenced like a mesh: a scene names the prefab as a
        // source and then overrides the ROOT transform, so both pointers are taken here rather
        // than rediscovered from the written .prefab (whose file name is just as lossy).
        if (collection is PrefabExportCollection prefab)
        {
            try
            {
                ITransform root = prefab.RootGameObject.GetTransform();
                PrefabExport export = new(
                    prefab.RootGameObject.Name,
                    prefab.GenerateMetaPtrForPrefab(),
                    collection.CreateExportPointer(container, root, isLocal: false));
                foreach (IUnityObjectBase asset in collection.Assets)
                {
                    if (asset.OriginalPath is { Length: > 0 } path
                        && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        CapturedPrefabs[ExportedAssetIndex.Normalize(path)] = export;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(LogCategory.Export,
                    $"ExportFilter: prefab '{collection.Name}' has no usable root transform ({ex.GetType().Name}: {ex.Message}); "
                    + "placements of it cannot be instantiated.");
            }
        }
    }

    private static bool CollectionMatches(IExportCollection collection, out int? primaryClassId)
    {
        primaryClassId = null;

        IUnityObjectBase? first = collection.Assets.FirstOrDefault();
        if (first != null)
        {
            primaryClassId = (int)first.ClassID;
        }

        if (AllowedClassIds.Count > 0)
        {
            if (primaryClassId is null) return false;
            if (!AllowedClassIds.Contains(primaryClassId.Value)) return false;
        }

        if (NameRegexes.Length > 0)
        {
            string name = collection.Name ?? string.Empty;
            if (!NameRegexes.Any(r => r.IsMatch(name))) return false;
        }

        return true;
    }

    private static void Invoke(FieldInfo? eventField, ProjectExporter exporter)
    {
        if (eventField?.GetValue(exporter) is Delegate del)
        {
            del.DynamicInvoke();
        }
    }

    private static void InvokeProgress(FieldInfo? eventField, ProjectExporter exporter, int i, int n)
    {
        if (eventField?.GetValue(exporter) is Delegate del)
        {
            del.DynamicInvoke(i, n);
        }
    }

    private static void InvokeFinished(FieldInfo? eventField, ProjectExporter exporter) => Invoke(eventField, exporter);
}
