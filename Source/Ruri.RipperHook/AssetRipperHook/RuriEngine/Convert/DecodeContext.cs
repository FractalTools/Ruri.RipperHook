namespace Ruri.RipperHook.RuriEngine;

internal sealed class DecodeContext(
    UnityYamlMapping body,
    UnityYamlAssetCollection collection,
    string className,
    string engineTypeName,
    StructShape shape,
    RuriEngineMapping mapping,
    GapReport gaps,
    string file)
{
    private readonly HashSet<string> taken = new(StringComparer.Ordinal);

    public UnityYamlAssetCollection Collection => collection;

    public string File => file;

    public UnityYamlNode? Take(string key)
    {
        taken.Add(key);
        return YamlNodes.Optional(body, key);
    }

    public void Unsupported(string what)
    {
        gaps.Add(GapCategories.UnsupportedLayout, $"{className}: {what}", file);
    }

    public void Ignored(string name)
    {
        gaps.Add(GapCategories.IgnoredField, $"{engineTypeName}.{name}", file);
    }

    public Value Pointer(UnityYamlNode? node, string path)
    {
        if (node is null or UnityYamlScalar { IsEmpty: true })
        {
            return ReferenceValue.Null;
        }
        UnityYamlMapping reference = YamlNodes.Mapping(node, path);
        if (reference.TryGetText("fileID") is not { } fileText || !YamlScalars.TryParseInteger(fileText, out long fileId))
        {
            throw new FormatException($"{path}: reference without fileID");
        }
        if (fileId == 0)
        {
            return ReferenceValue.Null;
        }
        string? guid = reference.TryGetText("guid");
        if (string.IsNullOrEmpty(guid) || YamlScalars.IsZeroGuid(guid))
        {
            return ReferenceValue.To(collection.Entry.Guid, fileId, collection.Entry.IsMain(fileId));
        }
        if (mapping.RetargetPackageReference(guid, fileId, gaps, $"{file}:{path}") is { } retargeted)
        {
            return retargeted;
        }
        if (collection.Project is { } project && project.TryGetCollection(guid, out UnityYamlAssetCollection? target))
        {
            return ReferenceValue.To(target.Entry.Guid, fileId, target.Entry.IsMain(fileId));
        }
        if (!mapping.IsBuiltinGuid(guid))
        {
            gaps.Add(GapCategories.DanglingReference, guid, $"{file}:{path} -> {guid}:{fileId}");
        }
        return ReferenceValue.To(guid, fileId, ObjectGuid.IsMainByStride(fileId));
    }

    public void CarryRenamed(ObjectValue output)
    {
        foreach (KeyValuePair<string, UnityYamlNode> entry in body.Entries)
        {
            if (!mapping.TryRename(engineTypeName, entry.Key, out string? engineName) || !shape.TryGetField(engineName, out EngineField? field))
            {
                continue;
            }
            taken.Add(entry.Key);
            if (ScalarOf(entry.Value, field.Shape) is { } value)
            {
                output.Add(engineName, value);
            }
            else
            {
                gaps.Add(GapCategories.ShapeMismatch, $"{engineTypeName}.{engineName}: cannot read {entry.Key}", file);
            }
        }
    }

    public void ReportLeftovers()
    {
        foreach (KeyValuePair<string, UnityYamlNode> entry in body.Entries)
        {
            if (taken.Contains(entry.Key) || mapping.IsBookkeeping(entry.Key) || mapping.IsIgnoredField(className, entry.Key))
            {
                continue;
            }
            gaps.Add(GapCategories.IgnoredField, $"{engineTypeName}.{entry.Key}", file);
        }
    }

    private static Value? ScalarOf(UnityYamlNode node, EngineShape target)
    {
        if (node is not UnityYamlScalar scalar)
        {
            return null;
        }
        switch (target)
        {
            case TextShape:
                return ScalarValue.FromText(scalar.Text);
            case PrimitiveShape { Kind: PrimitiveKind.Boolean }:
                return YamlScalars.TryParseBoolean(scalar.Text, out bool flag) ? ScalarValue.FromBoolean(flag) : null;
            case PrimitiveShape { Kind: PrimitiveKind.Single }:
                return YamlScalars.TryParseSingle(scalar.Text, out float single) ? ScalarValue.FromSingle(single) : null;
            case PrimitiveShape { Kind: PrimitiveKind.Double }:
                return YamlScalars.TryParseDouble(scalar.Text, out double floating) ? ScalarValue.FromDouble(floating) : null;
            case PrimitiveShape:
                return YamlScalars.TryParseInteger(scalar.Text, out long integer) ? ScalarValue.FromInteger(integer) : null;
            default:
                return null;
        }
    }
}
