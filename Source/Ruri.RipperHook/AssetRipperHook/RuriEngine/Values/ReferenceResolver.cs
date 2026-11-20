using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class ReferenceResolver(IExportContainer container, RuriEngineMapping mapping, GapReport gaps)
{
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
            if (mapping.RetargetPackageReference(external.Guid, pathId, external.Type, gaps, where) is { } retargeted)
            {
                return retargeted;
            }
            if (!mapping.IsBuiltinGuid(external.Guid))
            {
                gaps.Add(GapCategories.DanglingReference, external.Guid, $"{where} -> {external.Guid}:{pathId}");
            }
            return new ReferenceValue(pathId, external.Guid, external.Type);
        }
        if (fileIndex >= 0 && fileIndex < origin.Dependencies.Count && origin.Dependencies[fileIndex] is UnityYamlAssetCollection known)
        {
            return new ReferenceValue(pathId, known.Entry.Guid, known.ReferenceType);
        }
        MetaPtr missing = MetaPtr.CreateMissingReference(ClassIdOf(targetType), container.ToExportType(targetType));
        gaps.Add(GapCategories.DanglingReference, targetType.Name, $"{where} -> missing {targetType.Name} ({fileIndex}:{pathId})");
        return new ReferenceValue(missing.FileID, missing.GUID.ToString(), (int)missing.AssetType);
    }

    private ReferenceValue ResolveAsset(IUnityObjectBase target, long pathId, string where)
    {
        if (target.Collection is UnityYamlAssetCollection source
            && mapping.RetargetPackageReference(source.Entry.Guid, pathId, source.ReferenceType, gaps, where) is { } retargeted)
        {
            return retargeted;
        }
        MetaPtr pointer = container.CreateExportPointer(target);
        if (pointer.GUID.IsZero)
        {
            return new ReferenceValue(pointer.FileID, null, 0);
        }
        if (pointer.GUID == UnityGuid.MissingReference)
        {
            if (target.Collection is UnityYamlAssetCollection yaml)
            {
                gaps.Add(GapCategories.UnconvertedReference, target.ClassName, $"{where} -> {yaml.Entry.RelativePath}");
                return new ReferenceValue(pathId, yaml.Entry.Guid, yaml.ReferenceType);
            }
            gaps.Add(GapCategories.DanglingReference, target.ClassName, $"{where} -> unexported {target.ClassName}");
        }
        return new ReferenceValue(pointer.FileID, pointer.GUID.ToString(), (int)pointer.AssetType);
    }

    private static int ClassIdOf(Type type)
    {
        return (int)ClassIDTypeMap.Dictionary.GetValueOrDefault(type, ClassIDType.Object);
    }
}
