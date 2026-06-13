using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;

namespace Ruri.FModelHook.UnityExport.Engine;

// Minimal IExportContainer for synthetic export. A faithful reduction of
// AssetRipper.Export.UnityProjects.ProjectAssetContainer (ProjectAssetContainer.cs:49-90)
// minus the scene / BuildSettings machinery: it holds the asset -> export-collection
// map so a cross-asset PPtr resolves to the right (fileID, GUID, type), and tracks
// CurrentCollection so a same-file pointer stays local (no GUID). Dragging the full
// ProjectExporter in would be the wrong path (FModelHook design note) — this is the
// short, correct one.
public sealed class MinimalExportContainer : IExportContainer
{
    private readonly Dictionary<IUnityObjectBase, IExportCollection> _assetCollections = new();

    public MinimalExportContainer(UnityVersion exportVersion, IReadOnlyList<IExportCollection> collections)
    {
        ExportVersion = exportVersion;
        CurrentCollection = null!;
        foreach (IExportCollection collection in collections)
            foreach (IUnityObjectBase asset in collection.Assets)
                _assetCollections[asset] = collection;
    }

    public IExportCollection CurrentCollection { get; set; }
    public UnityVersion ExportVersion { get; }
    public AssetCollection File => CurrentCollection.File;

    public long GetExportID(IUnityObjectBase asset)
        => _assetCollections.TryGetValue(asset, out IExportCollection? collection)
            ? collection.GetExportID(this, asset)
            : ExportIdHandler.GetMainExportID(asset);

    public MetaPtr CreateExportPointer(IUnityObjectBase asset)
        => _assetCollections.TryGetValue(asset, out IExportCollection? collection)
            ? collection.CreateExportPointer(this, asset, collection == CurrentCollection)
            : MetaPtr.CreateMissingReference(asset.ClassID, AssetType.Meta);

    // The YAML exporter serializes inline, so every type is Serialized
    // (matches YamlExporterBase.ToExportType).
    public AssetType ToExportType(Type type) => AssetType.Serialized;

    // No scene/duplicate handling in the synthetic single-collection path.
    public UnityGuid ScenePathToGUID(string name) => default;
    public bool IsSceneDuplicate(int sceneID) => false;
}
