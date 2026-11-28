using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class ReferenceResolver(IExportContainer container, RuriEngineMapping mapping, RecordTable records, GapReport gaps, Guid fileGuid, ProjectEntry? entry)
{
    public ReferenceValue Local(long fileId)
    {
        return ReferenceValue.To(fileGuid, fileId, entry?.IsMain(fileId) ?? ObjectGuid.IsMainByStride(fileId));
    }

    public Value Resolve(AssetCollection origin, int fileIndex, long pathId, Type targetType, string where)
    {
        if (pathId == 0)
        {
            return ReferenceValue.Null;
        }
        if (origin.TryGetAsset(fileIndex, pathId, out IUnityObjectBase? target))
        {
            return ResolveAsset(target, pathId, where);
        }
        if (origin is UnityYamlAssetCollection yaml && yaml.TryGetExternal(fileIndex, out ExternalReference external))
        {
            if (mapping.RetargetPackageReference(external.Guid, pathId, gaps, where) is { } retargeted)
            {
                return retargeted;
            }
            if (!mapping.IsBuiltinGuid(external.Guid))
            {
                gaps.Add(GapCategories.DanglingReference, external.Guid, $"{where} -> {external.Guid}:{pathId}");
            }
            bool isMain = yaml.Project is { } project && project.TryGetCollection(external.Guid, out UnityYamlAssetCollection? found)
                ? found.Entry.IsMain(pathId)
                : ObjectGuid.IsMainByStride(pathId);
            return ReferenceValue.To(external.Guid, pathId, isMain);
        }
        if (fileIndex >= 0 && fileIndex < origin.Dependencies.Count && origin.Dependencies[fileIndex] is UnityYamlAssetCollection known)
        {
            return ReferenceValue.To(known.Entry.Guid, pathId, known.Entry.IsMain(pathId));
        }
        MetaPtr missing = MetaPtr.CreateMissingReference(ClassIdOf(targetType), container.ToExportType(targetType));
        gaps.Add(GapCategories.DanglingReference, targetType.Name, $"{where} -> missing {targetType.Name} ({fileIndex}:{pathId})");
        return ReferenceValue.To(missing.GUID.ToString(), missing.FileID, ObjectGuid.IsMainByStride(missing.FileID));
    }

    private ReferenceValue ResolveAsset(IUnityObjectBase target, long pathId, string where)
    {
        ReferenceValue reference = ResolveTarget(target, pathId, where);
        return reference.IsNull ? reference : reference.WithComponentType(records.ComponentTypeOf(target));
    }

    private ReferenceValue ResolveTarget(IUnityObjectBase target, long pathId, string where)
    {
        if (target.Collection is UnityYamlAssetCollection source
            && mapping.RetargetPackageReference(source.Entry.Guid, pathId, gaps, where) is { } retargeted)
        {
            return retargeted;
        }
        MetaPtr pointer = container.CreateExportPointer(target);
        if (pointer.GUID.IsZero)
        {
            return Local(pointer.FileID);
        }
        if (pointer.GUID == UnityGuid.MissingReference)
        {
            if (target.Collection is UnityYamlAssetCollection yaml)
            {
                gaps.Add(GapCategories.UnconvertedReference, target.ClassName, $"{where} -> {yaml.Entry.RelativePath}");
                return ReferenceValue.To(yaml.Entry.Guid, pathId, yaml.Entry.IsMain(pathId));
            }
            gaps.Add(GapCategories.DanglingReference, target.ClassName, $"{where} -> unexported {target.ClassName}");
        }
        bool isMain = target.Collection is UnityYamlAssetCollection owner ? owner.Entry.IsMain(pointer.FileID) : ObjectGuid.IsMainByStride(pointer.FileID);
        return ReferenceValue.To(pointer.GUID.ToString(), pointer.FileID, isMain);
    }

    private static int ClassIdOf(Type type)
    {
        return (int)ClassIDTypeMap.Dictionary.GetValueOrDefault(type, ClassIDType.Object);
    }
}
