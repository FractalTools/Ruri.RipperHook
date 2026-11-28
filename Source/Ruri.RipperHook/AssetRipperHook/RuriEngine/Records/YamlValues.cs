using System.Text.RegularExpressions;
using AssetRipper.Assets;

namespace Ruri.RipperHook.RuriEngine;

internal sealed partial class YamlValues(UnityYamlAssetCollection collection, RuriEngineMapping mapping, RecordTable records, GapReport gaps, string file)
{
    private const string FileIdKey = "fileID";
    private const string GuidKey = "guid";
    private const string TypeKey = "type";
    private const int SelfFileIndex = 0;

    public ObjectValue Read(UnityYamlMapping body)
    {
        ObjectValue result = new();
        foreach (KeyValuePair<string, UnityYamlNode> entry in body.Entries)
        {
            if (!mapping.IsBookkeeping(entry.Key))
            {
                result.Add(entry.Key, Read(entry.Value, entry.Key));
            }
        }
        return result;
    }

    private Value Read(UnityYamlNode node, string path)
    {
        switch (node)
        {
            case UnityYamlMapping map when IsPointer(map):
                return Pointer(map, path);
            case UnityYamlMapping map:
                ObjectValue entity = new();
                foreach (KeyValuePair<string, UnityYamlNode> entry in map.Entries)
                {
                    entity.Add(entry.Key, Read(entry.Value, $"{path}.{entry.Key}"));
                }
                return entity;
            case UnityYamlSequence sequence:
                ArrayValue array = new();
                foreach (UnityYamlNode item in sequence.Items)
                {
                    array.Items.Add(Read(item, path + "[]"));
                }
                return array;
            case UnityYamlScalar scalar:
                return Scalar(scalar);
            default:
                throw new NotSupportedException($"{path}: {node.GetType().Name} has no record value form.");
        }
    }

    private static bool IsPointer(UnityYamlMapping map)
    {
        return map.TryGet(FileIdKey, out _) && map.Entries.All(static entry => entry.Key is FileIdKey or GuidKey or TypeKey);
    }

    private static Value Scalar(UnityYamlScalar scalar)
    {
        string text = scalar.Text;
        if (scalar.Quoted)
        {
            return ScalarValue.FromText(text);
        }
        if (IntegerPattern().IsMatch(text))
        {
            if (YamlScalars.TryParseInteger(text, out long signed))
            {
                return ScalarValue.FromInteger(signed).WithSource(text);
            }
            if (YamlScalars.TryParseUnsigned(text, out ulong unsigned))
            {
                return ScalarValue.FromUnsigned(unsigned).WithSource(text);
            }
        }
        else if (FloatPattern().IsMatch(text) && YamlScalars.TryParseDouble(text, out double floating))
        {
            return ScalarValue.FromDouble(floating).WithSource(text);
        }
        return ScalarValue.FromText(text);
    }

    private Value Pointer(UnityYamlMapping map, string path)
    {
        if (map.TryGetText(FileIdKey) is not { } fileText || !YamlScalars.TryParseInteger(fileText, out long fileId))
        {
            throw new FormatException($"{path}: reference without fileID");
        }
        if (fileId == 0)
        {
            return ReferenceValue.Null;
        }
        string? guid = map.TryGetText(GuidKey);
        if (string.IsNullOrEmpty(guid) || YamlScalars.IsZeroGuid(guid))
        {
            return Typed(ReferenceValue.To(collection.Entry.Guid, fileId, collection.Entry.IsMain(fileId)), collection, fileId);
        }
        if (mapping.RetargetPackageReference(guid, fileId, gaps, $"{file}:{path}") is { } retargeted)
        {
            return retargeted;
        }
        if (collection.Project is { } project && project.TryGetCollection(guid, out UnityYamlAssetCollection? target))
        {
            return Typed(ReferenceValue.To(target.Entry.Guid, fileId, target.Entry.IsMain(fileId)), target, fileId);
        }
        if (!mapping.IsBuiltinGuid(guid))
        {
            gaps.Add(GapCategories.DanglingReference, guid, $"{file}:{path} -> {guid}:{fileId}");
        }
        return ReferenceValue.To(guid, fileId, ObjectGuid.IsMainByStride(fileId));
    }

    private ReferenceValue Typed(ReferenceValue reference, UnityYamlAssetCollection owner, long fileId)
    {
        return owner.TryGetAsset(SelfFileIndex, fileId, out IUnityObjectBase? asset)
            ? reference.WithComponentType(records.ComponentTypeOf(asset))
            : reference;
    }

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^-?(\d+\.\d+([eE][+-]?\d+)?|\d+[eE][+-]?\d+)$")]
    private static partial Regex FloatPattern();
}
