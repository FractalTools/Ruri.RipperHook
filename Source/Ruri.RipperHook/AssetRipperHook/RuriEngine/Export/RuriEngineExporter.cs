using AssetRipper.Assets;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated.Classes.ClassID_114;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class RuriEngineExporter(RuriEngineMapping mapping, EngineTypes types, GapReport gaps) : IAssetExporter
{
    private readonly EntityDocumentBuilder builder = new(mapping, types, gaps);

    public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
    {
        ClassRow row = mapping.RowOf(asset.ClassName);
        if (row.Kind == ClassKind.Payload)
        {
            exportCollection = null;
            return false;
        }
        if (IsConvertible(asset))
        {
            exportCollection = new EntityFileCollection(this, GroupOf(asset), mapping, builder, gaps);
            return true;
        }
        if (asset.Collection is not UnityYamlAssetCollection { Entry.Kind: ProjectFileKind.Script })
        {
            gaps.Add(GapCategories.UnmappedClass, row.Kind == ClassKind.Scripted ? $"{asset.ClassName} (component of a game object)" : asset.ClassName);
        }
        exportCollection = new SkipExportCollection(this, asset);
        return true;
    }

    public AssetType ToExportType(IUnityObjectBase asset) => AssetType.Serialized;

    public bool ToUnknownExportType(Type type, out AssetType assetType)
    {
        assetType = AssetType.Serialized;
        return false;
    }

    private bool IsConvertible(IUnityObjectBase asset)
    {
        return mapping.RowOf(asset.ClassName).Kind switch
        {
            ClassKind.Scripted => asset is IMonoBehaviour { GameObjectP: null },
            ClassKind.Mapped => true,
            _ => false,
        };
    }

    private IReadOnlyList<IUnityObjectBase> GroupOf(IUnityObjectBase asset)
    {
        if (asset.Collection is not UnityYamlAssetCollection yaml)
        {
            return [asset];
        }
        List<IUnityObjectBase> members = new();
        foreach (IUnityObjectBase candidate in yaml)
        {
            if (IsConvertible(candidate))
            {
                members.Add(candidate);
            }
        }
        return members;
    }
}
