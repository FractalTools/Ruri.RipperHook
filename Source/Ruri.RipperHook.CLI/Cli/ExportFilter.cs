using System.Reflection;
using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated;
using MonoModHook = MonoMod.RuntimeDetour.Hook;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// Re-implementation of <see cref="ProjectExporter.Export"/> installed via MonoMod hook so the CLI
/// can:
///   - filter collections by ClassID (--types) and asset-name regex (--names),
///   - cap exports per ClassID (--smoke-test-limit),
///   - capture per-collection exceptions into a structured failure list (--fail-fast=false),
///   - rethrow on first failure when --fail-fast is on.
///
/// This keeps the AOP rule: AssetRipper itself is not modified.
/// </summary>
internal static class ExportFilter
{
    public sealed record Failure(string Name, int? ClassId, string Error, string Stack);

    public static HashSet<int> AllowedClassIds { get; private set; } = new();
    public static Regex[] NameRegexes { get; private set; } = [];
    public static int SmokeTestLimit { get; private set; }
    public static bool FailFast { get; private set; } = true;

    public static int Considered { get; private set; }
    public static int Exported { get; private set; }
    public static Dictionary<int, int> ExportedByType { get; } = new();
    public static List<Failure> Failures { get; } = new();

    private static MonoModHook? _exportHook;
    private static bool _enabled;

    public static void Configure(HashSet<int> allowedClassIds, Regex[] names, int smokeTestLimit, bool failFast)
    {
        AllowedClassIds = allowedClassIds;
        NameRegexes = names;
        SmokeTestLimit = smokeTestLimit;
        FailFast = failFast;
        Considered = 0;
        Exported = 0;
        ExportedByType.Clear();
        Failures.Clear();
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
        // Mirror ProjectExporter.Export's structure but interpose filter+catch around each
        // collection. We have to use reflection because CreateCollections/CreateCollection are
        // private. (We never edit AssetRipper itself.)
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

    private static bool CollectionMatches(IExportCollection collection, out int? primaryClassId)
    {
        primaryClassId = null;

        // Inspect first asset for ClassID (collection.Name is already shader name for shader collections).
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
