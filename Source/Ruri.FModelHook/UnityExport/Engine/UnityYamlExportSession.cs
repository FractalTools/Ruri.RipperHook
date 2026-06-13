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
// ProcessedAssetCollection that every converted object lands in, plus the YAML
// writer. Convert() turns one UE export into a Unity object via the registry;
// ExportAll() writes each as a .asset + .meta, sharing ONE container so
// cross-asset PPtrs (material -> texture, mesh -> ...) resolve to real GUIDs.
public sealed class UnityYamlExportSession
{
    private readonly GameBundle _bundle;
    private readonly ProcessedAssetCollection _collection;
    private readonly UnityVersion _version;
    private readonly DefaultYamlExporter _exporter = new();
    private readonly List<IUnityObjectBase> _converted = new();
    private readonly Action<string> _logError;

    public int ConvertedCount => _converted.Count;
    public ProcessedAssetCollection Collection => _collection;

    public UnityYamlExportSession(UnityVersion version, Action<string> logError)
    {
        _version = version;
        _logError = logError;
        _bundle = new GameBundle();
        // AddNewProcessedCollection MUST carry the version, else version dispatch
        // falls to the Texture2D_3_5 antique layout with different field names
        // (FModelHook design note).
        _collection = _bundle.AddNewProcessedCollection("RuriUnityExport", version);
    }

    // Convert one UE export into a Unity object (or null if unmapped). Per-asset
    // failures are logged with full context and swallowed — one bad asset must
    // never sink a multi-thousand-asset run.
    public IUnityObjectBase? Convert(UObject source)
    {
        try
        {
            IUnityObjectBase? converted = MapperRegistry.Convert(source, _collection);
            if (converted != null) _converted.Add(converted);
            return converted;
        }
        catch (Exception ex)
        {
            _logError($"[UnityExport] convert failed: {ex.Message}");
            return null;
        }
    }

    // Write every converted object as {projectDirectory}/{GetBestDirectory}/{name}.asset
    // (+ .meta). Returns the number of assets actually written.
    public int ExportAll(string projectDirectory)
    {
        // One export collection per asset (each assigns a fresh GUID + writes a .meta).
        List<IExportCollection> collections = new(_converted.Count);
        foreach (IUnityObjectBase asset in _converted)
        {
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
