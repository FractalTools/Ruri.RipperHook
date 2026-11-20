using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class EntityConverter(RuriEngineMapping mapping, EngineTypes types, GapReport gaps, string file)
{
    private const int NestingLimit = 64;
    private const long UnknownReference = -1;
    private const long NullReference = -2;

    private sealed record RegistryEntry(string ClassName, string Namespace, ObjectValue Data);

    private readonly Dictionary<long, RegistryEntry> registry = new();
    private int depth;

    public ObjectValue ConvertMapped(ObjectValue source, string engineTypeName, StructShape shape)
    {
        ObjectValue components = new();
        components.Add(engineTypeName, ConvertStruct(source, shape, engineTypeName, null));
        return components;
    }

    public ObjectValue ConvertScripted(ObjectValue source, EngineClass engineClass)
    {
        BuildRegistry(source);
        SplitRow? split = mapping.SplitOf(engineClass.FullName);
        ObjectValue main = source;
        List<ObjectValue> groupSources = new();
        Value? identity = null;
        if (split is not null)
        {
            main = new ObjectValue();
            foreach (SplitGroup group in split.Groups)
            {
                groupSources.Add(new ObjectValue());
            }
            foreach (KeyValuePair<string, Value> field in source.Fields)
            {
                string engineName = mapping.StripPrefix(field.Key);
                if (split.Identity is not null && engineName == split.Identity)
                {
                    identity = field.Value;
                    continue;
                }
                int index = GroupIndexOf(split, engineName);
                if (index >= 0)
                {
                    groupSources[index].Add(field.Key, field.Value);
                }
                else
                {
                    main.Add(field.Key, field.Value);
                }
            }
        }

        List<ObjectValue> expanded = new();
        ObjectValue self = ConvertStruct(main, engineClass.Shape, engineClass.FullName, expanded);

        ObjectValue components = new();
        if (split is { EmitIdentity: true })
        {
            ObjectValue named = new();
            if (identity is ScalarValue { Kind: ScalarKind.Text } text && text.Text.Length > 0)
            {
                named.Add(mapping.IdentityField, text);
            }
            components.Add(mapping.IdentityComponent, named);
        }
        components.Add(engineClass.FullName, self);
        if (split is not null)
        {
            for (int index = 0; index < split.Groups.Length; index++)
            {
                SplitGroup group = split.Groups[index];
                if (!types.TryGet(group.Component, out EngineClass? groupClass))
                {
                    gaps.Add(GapCategories.UnresolvedClass, $"{group.Component}: no engine class of that name", file);
                    continue;
                }
                components.Add(group.Component, ConvertStruct(groupSources[index], groupClass.Shape, group.Component, null));
            }
        }
        foreach (ObjectValue wrapper in expanded)
        {
            KeyValuePair<string, Value> component = wrapper.Fields[0];
            if (components.TryGet(component.Key, out _))
            {
                gaps.Add(GapCategories.DuplicateComponent, component.Key, file);
                continue;
            }
            components.Add(component.Key, component.Value);
        }
        return components;
    }

    private static int GroupIndexOf(SplitRow split, string engineName)
    {
        for (int index = 0; index < split.Groups.Length; index++)
        {
            if (Array.IndexOf(split.Groups[index].Fields, engineName) >= 0)
            {
                return index;
            }
        }
        return -1;
    }

    private void BuildRegistry(ObjectValue source)
    {
        registry.Clear();
        if (!source.TryGet(ManagedReferenceTypes.RegistryFieldName, out Value? references) || references is not ObjectValue registryObject
            || !registryObject.TryGet(ManagedReferenceTypes.ReferenceIdsFieldName, out Value? identifiers) || identifiers is not ArrayValue list)
        {
            return;
        }
        foreach (Value item in list.Items)
        {
            if (item is not ObjectValue entry
                || !entry.TryGet(ManagedReferenceTypes.ReferenceIdFieldName, out Value? rid) || rid is not ScalarValue { IsIntegral: true } ridScalar)
            {
                continue;
            }
            long id = ridScalar.Kind == ScalarKind.Unsigned ? unchecked((long)ridScalar.Unsigned) : ridScalar.Signed;
            ObjectValue? type = entry.TryGet(ManagedReferenceTypes.TypeFieldName, out Value? typeValue) ? typeValue as ObjectValue : null;
            ObjectValue data = entry.TryGet(ManagedReferenceTypes.DataFieldName, out Value? dataValue) && dataValue is ObjectValue dataObject
                ? dataObject
                : new ObjectValue();
            registry[id] = new RegistryEntry(TextOf(type, ManagedReferenceTypes.ClassFieldName), TextOf(type, ManagedReferenceTypes.NamespaceFieldName), data);
        }
    }

    private static string TextOf(ObjectValue? source, string field)
    {
        return source is not null && source.TryGet(field, out Value? value) && value is ScalarValue { Kind: ScalarKind.Text } text
            ? text.Text
            : string.Empty;
    }

    private ObjectValue ConvertStruct(ObjectValue source, StructShape shape, string path, List<ObjectValue>? components)
    {
        Dictionary<string, Value> converted = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Value> field in source.Fields)
        {
            if (mapping.IsBookkeeping(field.Key) || (shape.HasRegistry && field.Key == ManagedReferenceTypes.RegistryFieldName))
            {
                continue;
            }
            string? engineName = mapping.ResolveField(shape, field.Key);
            if (engineName is null || !shape.TryGetField(engineName, out EngineField? target))
            {
                gaps.Add(GapCategories.IgnoredField, $"{shape.TypeName}.{field.Key}", file);
                continue;
            }
            if (components is not null
                && engineName == mapping.ComponentListField
                && target.Shape is ArrayShape { Element: ManagedShape }
                && field.Value is ArrayValue list)
            {
                foreach (Value item in list.Items)
                {
                    if (ConvertManaged(item, $"{path}.{engineName}") is ObjectValue component)
                    {
                        components.Add(component);
                    }
                }
                continue;
            }
            if (Convert(field.Value, target.Shape, $"{path}.{engineName}") is { } value)
            {
                converted[engineName] = value;
            }
        }

        if (mapping.CurrentVersions.TryGetValue(shape.TypeName, out Dictionary<string, long>? versions))
        {
            foreach (KeyValuePair<string, long> version in versions)
            {
                if (shape.TryGetField(version.Key, out EngineField? versionField) && versionField.Shape is PrimitiveShape)
                {
                    converted[version.Key] = ScalarValue.FromInteger(version.Value);
                }
            }
        }

        ObjectValue result = new();
        foreach (EngineField field in shape.Fields)
        {
            if (converted.TryGetValue(field.Name, out Value? value))
            {
                result.Add(field.Name, value);
            }
        }
        return result;
    }

    private Value? Convert(Value source, EngineShape shape, string path)
    {
        switch (shape)
        {
            case PrimitiveShape primitive:
                return ConvertPrimitive(source, primitive.Kind, path);
            case TextShape:
                return source is ScalarValue { Kind: ScalarKind.Text } ? source : Mismatch(path, "text", source);
            case ReferenceShape:
                return source is ReferenceValue ? source : Mismatch(path, "reference", source);
            case ManagedShape:
                return ConvertManaged(source, path);
            case ArrayShape array:
                return ConvertArray(source, array, path);
            case StructShape structure:
                return source switch
                {
                    ObjectValue entity => ConvertNested(entity, structure, path),
                    ReferenceValue => source,
                    _ => Mismatch(path, structure.TypeName, source),
                };
            default:
                return Mismatch(path, shape.GetType().Name, source);
        }
    }

    private ObjectValue? ConvertNested(ObjectValue source, StructShape shape, string path)
    {
        if (depth >= NestingLimit)
        {
            gaps.Add(GapCategories.ShapeMismatch, $"{path}: nesting deeper than {NestingLimit}", file);
            return null;
        }
        depth++;
        try
        {
            return ConvertStruct(source, shape, path, null);
        }
        finally
        {
            depth--;
        }
    }

    private Value? ConvertManaged(Value source, string path)
    {
        if (source is not ObjectValue reference
            || !(reference.TryGet(ManagedReferenceTypes.ReferenceIdFieldName, out Value? idValue) || reference.TryGet(ManagedReferenceTypes.IndexFieldName, out idValue))
            || idValue is not ScalarValue { IsIntegral: true } idScalar)
        {
            Mismatch(path, "managed reference", source);
            return NullValue.Instance;
        }
        long id = idScalar.Kind == ScalarKind.Unsigned ? unchecked((long)idScalar.Unsigned) : idScalar.Signed;
        if (id is NullReference or UnknownReference)
        {
            return NullValue.Instance;
        }
        if (!registry.TryGetValue(id, out RegistryEntry? entry))
        {
            gaps.Add(GapCategories.ShapeMismatch, $"{path}: managed reference is not in the registry", file);
            return null;
        }
        if (!ScriptIdentity.TryResolve(types, entry.Namespace, entry.ClassName, out EngineClass? engineClass, out string reason))
        {
            gaps.Add(GapCategories.UnresolvedClass, reason, file);
            return null;
        }
        ObjectValue? data = ConvertNested(entry.Data, engineClass.Shape, $"{path}<{engineClass.FullName}>");
        if (data is null)
        {
            return null;
        }
        ObjectValue wrapper = new();
        wrapper.Add(engineClass.FullName, data);
        return wrapper;
    }

    private Value? ConvertArray(Value source, ArrayShape shape, string path)
    {
        if (source is ScalarValue { Kind: ScalarKind.Bytes } bytes && shape.Element is PrimitiveShape { Kind: PrimitiveKind.Byte or PrimitiveKind.SByte })
        {
            bool signedElements = shape.Element is PrimitiveShape { Kind: PrimitiveKind.SByte };
            ArrayValue expanded = new();
            foreach (byte item in bytes.Bytes)
            {
                expanded.Items.Add(ScalarValue.FromInteger(signedElements ? (long)(sbyte)item : item));
            }
            return expanded;
        }
        if (source is not ArrayValue array)
        {
            return Mismatch(path, "array", source);
        }
        ArrayValue result = new();
        foreach (Value item in array.Items)
        {
            Value? converted = shape.Element is ManagedShape ? ConvertManaged(item, path + "[]") : Convert(item, shape.Element, path + "[]");
            if (converted is null && shape.Element is ManagedShape)
            {
                continue;
            }
            if (converted is null)
            {
                return null;
            }
            result.Items.Add(converted);
        }
        return result;
    }

    private Value? ConvertPrimitive(Value source, PrimitiveKind kind, string path)
    {
        if (source is not ScalarValue scalar)
        {
            return Mismatch(path, kind.ToString(), source);
        }
        switch (kind)
        {
            case PrimitiveKind.Boolean:
                return scalar.Kind is ScalarKind.Boolean or ScalarKind.Integer or ScalarKind.Unsigned
                    ? ScalarValue.FromBoolean(scalar.AsBoolean)
                    : Mismatch(path, "bool", source);
            case PrimitiveKind.Single:
                return scalar.IsNumber ? ScalarValue.FromSingle((float)scalar.AsDouble) : Mismatch(path, "float", source);
            case PrimitiveKind.Double:
                return scalar.IsNumber ? ScalarValue.FromDouble(scalar.AsDouble) : Mismatch(path, "double", source);
            case PrimitiveKind.Char:
                if (scalar.Kind == ScalarKind.Text && scalar.Text.Length == 1)
                {
                    return ScalarValue.FromInteger(scalar.Text[0]);
                }
                return InRange(scalar, 0, ushort.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, "char", source);
            case PrimitiveKind.SByte:
                return InRange(scalar, sbyte.MinValue, sbyte.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            case PrimitiveKind.Byte:
                return InRange(scalar, 0, byte.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            case PrimitiveKind.Short:
                return InRange(scalar, short.MinValue, short.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            case PrimitiveKind.UShort:
                return InRange(scalar, 0, ushort.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            case PrimitiveKind.Int:
                return InRange(scalar, int.MinValue, int.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            case PrimitiveKind.UInt:
                return InRange(scalar, 0, uint.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            case PrimitiveKind.Long:
                return InRange(scalar, long.MinValue, long.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
            default:
                if (scalar.Kind == ScalarKind.Unsigned)
                {
                    return ScalarValue.FromUnsigned(scalar.Unsigned);
                }
                return InRange(scalar, 0, long.MaxValue) ? ScalarValue.FromInteger(scalar.Signed) : Mismatch(path, kind.ToString(), source);
        }
    }

    private static bool InRange(ScalarValue scalar, long minimum, long maximum)
    {
        return scalar.Kind is ScalarKind.Integer or ScalarKind.Boolean && scalar.Signed >= minimum && scalar.Signed <= maximum;
    }

    private Value? Mismatch(string path, string expected, Value actual)
    {
        gaps.Add(GapCategories.ShapeMismatch, $"{path}: expected {expected}, found {Describe(actual)}", file);
        return null;
    }

    private static string Describe(Value value)
    {
        return value switch
        {
            ObjectValue => "object",
            ArrayValue => "array",
            ReferenceValue => "reference",
            NullValue => "null",
            ScalarValue scalar => scalar.Kind.ToString().ToLowerInvariant(),
            _ => value.GetType().Name,
        };
    }
}
