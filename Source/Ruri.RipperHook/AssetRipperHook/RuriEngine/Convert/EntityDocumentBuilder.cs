using AssetRipper.Assets;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class EntityDocumentBuilder(RuriEngineMapping mapping, EngineTypes types, GapReport gaps)
{
    public ObjectValue? BuildComponents(IUnityObjectBase asset, ReferenceResolver references, string file)
    {
        ClassRow row = mapping.RowOf(asset.ClassName);
        return row.Kind switch
        {
            ClassKind.Scripted => BuildScripted(asset, references, file),
            ClassKind.Mapped => BuildMapped(asset, row, references, file),
            _ => null,
        };
    }

    private ObjectValue? BuildScripted(IUnityObjectBase asset, ReferenceResolver references, string file)
    {
        if (asset is not IMonoBehaviour behaviour)
        {
            return null;
        }
        bool reportedByReader = asset.Collection is UnityYamlAssetCollection;
        SerializableStructure? structure = behaviour.LoadStructure();
        IMonoScript? script = behaviour.ScriptP;
        if (structure is null || script is null)
        {
            if (!reportedByReader)
            {
                gaps.Add(GapCategories.UnresolvedScript, script?.ClassName_R.String ?? "(no script)", file);
            }
            return null;
        }
        if (!ScriptIdentity.TryResolve(types, script.Namespace.String, script.ClassName_R.String, out EngineClass? engineClass, out string reason))
        {
            if (!reportedByReader)
            {
                gaps.Add(GapCategories.UnresolvedClass, reason, file);
            }
            return null;
        }
        ObjectValue source = ValueTreeWalker.Walk(structure, asset.Collection, references, file);
        return new EntityConverter(mapping, types, gaps, file).ConvertScripted(source, engineClass);
    }

    private ObjectValue? BuildMapped(IUnityObjectBase asset, ClassRow row, ReferenceResolver references, string file)
    {
        string engineType = row.EngineType ?? string.Empty;
        if (!types.TryGet(engineType, out EngineClass? engineClass))
        {
            gaps.Add(GapCategories.NoEngineType, $"{asset.ClassName} -> {engineType}", file);
            return null;
        }
        if (asset.Collection is not UnityYamlAssetCollection yaml || !yaml.Bodies.TryGetValue(asset.PathID, out UnityYamlMapping? body))
        {
            gaps.Add(GapCategories.UnreadableFile, $"{asset.ClassName} has no YAML body", file);
            return null;
        }
        if (!MappedDecoders.TryGet(asset.ClassName, out MappedDecoder? decoder))
        {
            gaps.Add(GapCategories.UnmappedClass, $"{asset.ClassName} has no decoder", file);
            return null;
        }
        DecodeContext context = new(body, yaml, asset.ClassName, engineClass.FullName, engineClass.Shape, mapping, gaps, file);
        ObjectValue? source = decoder(context);
        if (source is null)
        {
            return null;
        }
        context.CarryRenamed(source);
        context.ReportLeftovers();
        return new EntityConverter(mapping, types, gaps, file).ConvertMapped(source, engineClass.FullName, engineClass.Shape);
    }
}
