using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.UnityExport.Engine;

// Owns the lifetime of one synthetic Unity export: a GameBundle + a single
// ProcessedAssetCollection (wrapped in a ConversionContext) that every converted
// object lands in, plus the YAML writer. Convert() turns one UE export into a
// Unity object via the registry (deduplicated); ExportAll() writes each as a
// .asset + .meta, sharing ONE container so cross-asset PPtrs resolve to real GUIDs.
public sealed class UnityYamlExportSession
{
    private readonly GameBundle _bundle;
    private readonly ConversionContext _context;
    private readonly UnityVersion _version;
    private readonly DefaultYamlExporter _exporter = new();
    private readonly Action<string> _logError;

    public int ConvertedCount => _context.Converted.Count;

    public UnityYamlExportSession(UnityVersion version, Action<string> logError)
    {
        _version = version;
        _logError = logError;
        _bundle = new GameBundle();
        // AddNewProcessedCollection MUST carry the version, else version dispatch
        // falls to the Texture2D_3_5 antique layout with different field names
        // (FModelHook design note).
        ProcessedAssetCollection collection = _bundle.AddNewProcessedCollection("RuriUnityExport", version);
        _context = new ConversionContext(collection);
    }

    // Convert one UE export into a Unity object (or null if unmapped). Per-asset
    // failures are logged with full context and swallowed — one bad asset must
    // never sink a multi-thousand-asset run.
    public IUnityObjectBase? Convert(UObject source)
    {
        try
        {
            return _context.Convert(source);
        }
        catch (Exception ex)
        {
            _logError($"[UnityExport] convert failed: {ex.Message}");
            return null;
        }
    }

    // Write every converted object (including ones pulled in transitively as
    // references) as {projectDirectory}/{GetBestDirectory}/{name}.{ext} (+ .meta).
    // Returns the number of assets actually written.
    public int ExportAll(string projectDirectory)
    {
        // Objects claimed by a group (prefab/scene) are written as part of that
        // group's single file, not as standalone .asset files.
        HashSet<IUnityObjectBase> grouped = new();
        foreach (IExportCollection group in _context.ExportGroups)
            foreach (IUnityObjectBase asset in group.Assets)
                grouped.Add(asset);

        // Group collections first, then one export collection per remaining asset
        // (each assigns a fresh GUID + writes a .meta).
        List<IExportCollection> collections = new(_context.ExportGroups);
        foreach (IUnityObjectBase asset in _context.Converted)
        {
            if (grouped.Contains(asset))
                continue;
            if (_exporter.TryCreateCollection(asset, out IExportCollection? collection))
                collections.Add(collection);
        }

        // One shared container resolves cross-asset pointers through the GUID map.
        MinimalExportContainer container = new(_version, collections);

        int written = 0;
        foreach (IExportCollection collection in collections)
        {
            container.CurrentCollection = collection;
            try
            {
                if (collection.Export(container, projectDirectory, LocalFileSystem.Instance))
                    written++;
            }
            catch (Exception ex)
            {
                _logError($"[UnityExport] export failed for '{collection.Name}': {ex.Message}");
            }
        }
        return written;
    }
}
