using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using AssetRipper.Assets;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Object;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class YamlStructureFiller(
    RuriEngineMapping mapping,
    ProjectIndex project,
    ITypeResolver resolver,
    GapReport gaps,
    UnityVersion version,
    UnityYamlAssetCollection collection)
{
    private const long NullManagedReference = -2;

    private delegate bool TryParseNode<T>(UnityYamlNode node, out T value);

    private delegate void StoreArray<T>(ref SerializableValue target, T[] array);

    private readonly Dictionary<SerializableType, Dictionary<string, int>> indexes = new();

    public void FillStructure(SerializableStructure target, UnityYamlMapping source, string path)
    {
        SerializableType type = target.Type;
        Dictionary<string, int> index = FieldIndex(type);
        bool[] filled = new bool[target.Fields.Length];
        foreach (KeyValuePair<string, UnityYamlNode> entry in source.Entries)
        {
            if (mapping.IsBookkeeping(entry.Key))
            {
                continue;
            }
            string? fieldName = mapping.ResolveField(type.Name, entry.Key, index.ContainsKey);
            if (fieldName is null)
            {
                gaps.Add(GapCategories.IgnoredField, $"{type.Name}.{entry.Key}", collection.Entry.RelativePath);
                continue;
            }
            int fieldIndex = index[fieldName];
            if (filled[fieldIndex])
            {
                continue;
            }
            SerializableType.Field etalon = type.Fields[fieldIndex];
            filled[fieldIndex] = etalon.Type == ManagedReferenceTypes.Registry
                ? FillRegistry(target, fieldIndex, entry.Value, path)
                : FillField(ref target.Fields[fieldIndex], etalon, entry.Value, target.Depth, $"{path}.{fieldName}");
        }
        for (int fieldIndex = 0; fieldIndex < filled.Length; fieldIndex++)
        {
            if (!filled[fieldIndex])
            {
                SparseFields.MarkAbsent(target, type.Fields[fieldIndex].Name);
            }
        }
    }

    private Dictionary<string, int> FieldIndex(SerializableType type)
    {
        if (!indexes.TryGetValue(type, out Dictionary<string, int>? index))
        {
            index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int fieldIndex = 0; fieldIndex < type.Fields.Count; fieldIndex++)
            {
                index.TryAdd(type.Fields[fieldIndex].Name, fieldIndex);
            }
            indexes[type] = index;
        }
        return index;
    }

    private bool FillField(ref SerializableValue value, SerializableType.Field etalon, UnityYamlNode node, int depth, string path)
    {
        return etalon.ArrayDepth switch
        {
            0 => FillSingle(ref value, etalon.Type, node, depth, path),
            1 => FillArray(ref value, etalon.Type, node, depth, path),
            _ => Unsupported(path, $"array depth {etalon.ArrayDepth}"),
        };
    }

    private bool FillSingle(ref SerializableValue value, SerializableType type, UnityYamlNode node, int depth, string path)
    {
        switch (type.Type)
        {
            case PrimitiveType.Bool:
                if (TryBoolean(node, out bool boolean))
                {
                    value.AsBoolean = boolean;
                    return true;
                }
                return Mismatch(path, "bool");
            case PrimitiveType.Char:
                if (TryInteger(node, out ushort character))
                {
                    value.AsChar = (char)character;
                    return true;
                }
                return Mismatch(path, "char");
            case PrimitiveType.SByte:
                if (TryInteger(node, out sbyte sbyteValue))
                {
                    value.AsSByte = sbyteValue;
                    return true;
                }
                return Mismatch(path, "sbyte");
            case PrimitiveType.Byte:
                if (TryInteger(node, out byte byteValue))
                {
                    value.AsByte = byteValue;
                    return true;
                }
                return Mismatch(path, "byte");
            case PrimitiveType.Short:
                if (TryInteger(node, out short shortValue))
                {
                    value.AsInt16 = shortValue;
                    return true;
                }
                return Mismatch(path, "short");
            case PrimitiveType.UShort:
                if (TryInteger(node, out ushort ushortValue))
                {
                    value.AsUInt16 = ushortValue;
                    return true;
                }
                return Mismatch(path, "ushort");
            case PrimitiveType.Int:
                if (TryInteger(node, out int intValue))
                {
                    value.AsInt32 = intValue;
                    return true;
                }
                return Mismatch(path, "int");
            case PrimitiveType.UInt:
                if (TryInteger(node, out uint uintValue))
                {
                    value.AsUInt32 = uintValue;
                    return true;
                }
                return Mismatch(path, "uint");
            case PrimitiveType.Long:
                if (TryInteger(node, out long longValue))
                {
                    value.AsInt64 = longValue;
                    return true;
                }
                return Mismatch(path, "long");
            case PrimitiveType.ULong:
                if (TryInteger(node, out ulong ulongValue))
                {
                    value.AsUInt64 = ulongValue;
                    return true;
                }
                return Mismatch(path, "ulong");
            case PrimitiveType.Single:
                if (TrySingle(node, out float singleValue))
                {
                    value.AsSingle = singleValue;
                    return true;
                }
                return Mismatch(path, "float");
            case PrimitiveType.Double:
                if (TryDouble(node, out double doubleValue))
                {
                    value.AsDouble = doubleValue;
                    return true;
                }
                return Mismatch(path, "double");
            case PrimitiveType.String:
                if (node is UnityYamlScalar text)
                {
                    value.AsString = text.Text;
                    return true;
                }
                return Mismatch(path, "text");
            case PrimitiveType.Complex:
                if (value.AsAsset is SerializableStructure existing && existing.Type == type && node is UnityYamlMapping existingMapping)
                {
                    FillStructure(existing, existingMapping, path);
                    return true;
                }
                if (CreateComplex(type, node, depth, path) is { } created)
                {
                    value.AsAsset = created;
                    return true;
                }
                return false;
            default:
                return Unsupported(path, type.Type.ToString());
        }
    }

    private bool FillArray(ref SerializableValue value, SerializableType type, UnityYamlNode node, int depth, string path)
    {
        switch (type.Type)
        {
            case PrimitiveType.Complex:
                return FillComplexArray(ref value, type, node, depth, path);
            case PrimitiveType.String:
                if (TryElements(node, TryText, out string[]? texts))
                {
                    value.AsStringArray = texts;
                    return true;
                }
                return Mismatch(path, "text array");
            case PrimitiveType.Single:
                if (TryElements(node, TrySingle, out float[]? singles))
                {
                    value.AsSingleArray = singles;
                    return true;
                }
                return Mismatch(path, "float array");
            case PrimitiveType.Double:
                if (TryElements(node, TryDouble, out double[]? doubles))
                {
                    value.AsDoubleArray = doubles;
                    return true;
                }
                return Mismatch(path, "double array");
            case PrimitiveType.Pair:
            case PrimitiveType.MapPair:
            case PrimitiveType.Void:
                return Unsupported(path, type.Type.ToString());
            default:
                return FillIntegralArray(ref value, type.Type, node, path);
        }
    }

    private bool FillIntegralArray(ref SerializableValue value, PrimitiveType kind, UnityYamlNode node, string path)
    {
        if (node is UnityYamlScalar compact && !compact.IsEmpty)
        {
            if (!YamlScalars.TryDecodeCompact(compact.Text, kind.GetSize(), out byte[] bytes))
            {
                return Mismatch(path, $"compact {kind} array");
            }
            switch (kind)
            {
                case PrimitiveType.Bool:
                    value.AsBooleanArray = Array.ConvertAll(bytes, static b => b != 0);
                    break;
                case PrimitiveType.Char:
                    value.AsCharArray = Array.ConvertAll(YamlScalars.ToArray<ushort>(bytes), static c => (char)c);
                    break;
                case PrimitiveType.SByte:
                    value.AsSByteArray = Array.ConvertAll(bytes, static b => unchecked((sbyte)b));
                    break;
                case PrimitiveType.Byte:
                    value.AsByteArray = bytes;
                    break;
                case PrimitiveType.Short:
                    value.AsInt16Array = YamlScalars.ToArray<short>(bytes);
                    break;
                case PrimitiveType.UShort:
                    value.AsUInt16Array = YamlScalars.ToArray<ushort>(bytes);
                    break;
                case PrimitiveType.Int:
                    value.AsInt32Array = YamlScalars.ToArray<int>(bytes);
                    break;
                case PrimitiveType.UInt:
                    value.AsUInt32Array = YamlScalars.ToArray<uint>(bytes);
                    break;
                case PrimitiveType.Long:
                    value.AsInt64Array = YamlScalars.ToArray<long>(bytes);
                    break;
                default:
                    value.AsUInt64Array = YamlScalars.ToArray<ulong>(bytes);
                    break;
            }
            return true;
        }

        bool filled = kind switch
        {
            PrimitiveType.Bool => Assign(node, TryBoolean, static (ref SerializableValue target, bool[] array) => target.AsBooleanArray = array, ref value),
            PrimitiveType.Char => Assign(node, TryCharacter, static (ref SerializableValue target, char[] array) => target.AsCharArray = array, ref value),
            PrimitiveType.SByte => Assign(node, TryInteger, static (ref SerializableValue target, sbyte[] array) => target.AsSByteArray = array, ref value),
            PrimitiveType.Byte => Assign(node, TryInteger, static (ref SerializableValue target, byte[] array) => target.AsByteArray = array, ref value),
            PrimitiveType.Short => Assign(node, TryInteger, static (ref SerializableValue target, short[] array) => target.AsInt16Array = array, ref value),
            PrimitiveType.UShort => Assign(node, TryInteger, static (ref SerializableValue target, ushort[] array) => target.AsUInt16Array = array, ref value),
            PrimitiveType.Int => Assign(node, TryInteger, static (ref SerializableValue target, int[] array) => target.AsInt32Array = array, ref value),
            PrimitiveType.UInt => Assign(node, TryInteger, static (ref SerializableValue target, uint[] array) => target.AsUInt32Array = array, ref value),
            PrimitiveType.Long => Assign(node, TryInteger, static (ref SerializableValue target, long[] array) => target.AsInt64Array = array, ref value),
            _ => Assign(node, TryInteger, static (ref SerializableValue target, ulong[] array) => target.AsUInt64Array = array, ref value),
        };
        return filled || Mismatch(path, $"{kind} array");
    }

    private static bool Assign<T>(UnityYamlNode node, TryParseNode<T> parse, StoreArray<T> store, ref SerializableValue value)
    {
        if (!TryElements(node, parse, out T[]? array))
        {
            return false;
        }
        store(ref value, array);
        return true;
    }

    private bool FillComplexArray(ref SerializableValue value, SerializableType type, UnityYamlNode node, int depth, string path)
    {
        if (node is UnityYamlScalar { IsEmpty: true })
        {
            value.AsAssetArray = [];
            return true;
        }
        if (node is not UnityYamlSequence sequence)
        {
            return Mismatch(path, "sequence");
        }
        IUnityAssetBase[] items = new IUnityAssetBase[sequence.Items.Count];
        for (int index = 0; index < items.Length; index++)
        {
            IUnityAssetBase? item = CreateComplex(type, sequence.Items[index], depth, path + "[]");
            if (item is null)
            {
                return false;
            }
            items[index] = item;
        }
        value.AsAssetArray = items;
        return true;
    }

    private IUnityAssetBase? CreateComplex(SerializableType type, UnityYamlNode node, int depth, string path)
    {
        if (type.IsEnginePointer())
        {
            return Pointer(node, path);
        }
        if (type == ManagedReferenceTypes.ManagedReference || type == ManagedReferenceTypes.IndexedManagedReference)
        {
            return ManagedReference(type, node, depth, path);
        }
        if (node is not UnityYamlMapping mappingNode)
        {
            Mismatch(path, type.Name);
            return null;
        }
        if (type.CreateInstance(depth + 1, version) is not SerializableStructure structure)
        {
            Unsupported(path, type.Name);
            return null;
        }
        FillStructure(structure, mappingNode, path);
        return structure;
    }

    private IUnityAssetBase? ManagedReference(SerializableType type, UnityYamlNode node, int depth, string path)
    {
        long identifier = NullManagedReference;
        if (node is UnityYamlMapping reference)
        {
            string? text = reference.TryGetText(ManagedReferenceTypes.ReferenceIdFieldName) ?? reference.TryGetText(ManagedReferenceTypes.IndexFieldName);
            if (text is null || !TryIdentifier(text, out identifier))
            {
                Mismatch(path, "managed reference identifier");
                return null;
            }
        }
        else if (node is not UnityYamlScalar { IsEmpty: true })
        {
            Mismatch(path, "managed reference");
            return null;
        }
        if (type.CreateInstance(depth + 1, version) is not SerializableStructure structure)
        {
            return null;
        }
        SerializableType.Field field = type.Fields[0];
        if (field.Type.Type == PrimitiveType.Long)
        {
            structure[field.Name].AsInt64 = identifier;
        }
        else
        {
            structure[field.Name].AsInt32 = (int)identifier;
        }
        return structure;
    }

    private bool FillRegistry(SerializableStructure owner, int fieldIndex, UnityYamlNode node, string path)
    {
        if (node is not UnityYamlMapping registryMapping)
        {
            return Mismatch(path + ".references", "registry mapping");
        }
        if (ManagedReferenceTypes.Registry.CreateInstance(owner.Depth + 1, version) is not SerializableStructure registry)
        {
            return false;
        }
        if (registryMapping.TryGetText(ManagedReferenceTypes.VersionFieldName) is { } format && YamlScalars.TryParseInteger(format, out long formatValue))
        {
            registry[ManagedReferenceTypes.VersionFieldName].AsInt32 = (int)formatValue;
        }
        List<IUnityAssetBase> referenced = new();
        if (registryMapping.TryGet(ManagedReferenceTypes.ReferenceIdsFieldName, out UnityYamlNode? identifiers) && identifiers is UnityYamlSequence list)
        {
            foreach (UnityYamlNode item in list.Items)
            {
                if (ReferencedObject(item, owner.Depth + 2, path) is { } entry)
                {
                    referenced.Add(entry);
                }
            }
        }
        registry[ManagedReferenceTypes.ReferenceIdsFieldName].AsAssetArray = referenced.ToArray();
        owner.Fields[fieldIndex].AsAsset = registry;
        return true;
    }

    private IUnityAssetBase? ReferencedObject(UnityYamlNode node, int depth, string path)
    {
        if (node is not UnityYamlMapping item
            || item.TryGetText(ManagedReferenceTypes.ReferenceIdFieldName) is not { } ridText
            || !TryIdentifier(ridText, out long identifier)
            || !item.TryGet(ManagedReferenceTypes.TypeFieldName, out UnityYamlNode? typeNode)
            || typeNode is not UnityYamlMapping typeMapping)
        {
            Mismatch(path + ".references", "referenced object");
            return null;
        }
        string className = typeMapping.TryGetText(ManagedReferenceTypes.ClassFieldName) ?? string.Empty;
        string @namespace = typeMapping.TryGetText(ManagedReferenceTypes.NamespaceFieldName) ?? string.Empty;
        string assembly = typeMapping.TryGetText(ManagedReferenceTypes.AssemblyFieldName) ?? string.Empty;

        if (ManagedReferenceTypes.ReferencedObject.CreateInstance(depth, version) is not SerializableStructure referencedObject
            || ManagedReferenceTypes.ReferencedManagedType.CreateInstance(depth + 1, version) is not SerializableStructure managedType)
        {
            return null;
        }
        referencedObject[ManagedReferenceTypes.ReferenceIdFieldName].AsInt64 = identifier;
        managedType[ManagedReferenceTypes.ClassFieldName].AsString = className;
        managedType[ManagedReferenceTypes.NamespaceFieldName].AsString = @namespace;
        managedType[ManagedReferenceTypes.AssemblyFieldName].AsString = assembly;
        referencedObject[ManagedReferenceTypes.TypeFieldName].AsAsset = managedType;

        if (className.Length > 0 && assembly.Length > 0)
        {
            ScriptIdentifier script = new(assembly, @namespace, className);
            if (resolver.TryGetSerializableType(script, version, out SerializableType? dataType, out string? reason))
            {
                if (dataType.CreateInstance(depth + 1, version) is SerializableStructure data)
                {
                    if (item.TryGet(ManagedReferenceTypes.DataFieldName, out UnityYamlNode? dataNode) && dataNode is UnityYamlMapping dataMapping)
                    {
                        FillStructure(data, dataMapping, dataType.Name);
                    }
                    referencedObject[ManagedReferenceTypes.DataFieldName].AsAsset = data;
                }
            }
            else
            {
                gaps.Add(GapCategories.UnresolvedClass, reason, collection.Entry.RelativePath);
            }
        }
        return referencedObject;
    }

    private IUnityAssetBase Pointer(UnityYamlNode node, string path)
    {
        long fileId = 0;
        string? guid = null;
        long type = 0;
        if (node is UnityYamlMapping reference)
        {
            if (reference.TryGetText("fileID") is { } fileText && !YamlScalars.TryParseInteger(fileText, out fileId))
            {
                Mismatch(path, "reference fileID");
            }
            guid = reference.TryGetText("guid");
            if (reference.TryGetText("type") is { } typeText)
            {
                YamlScalars.TryParseInteger(typeText, out type);
            }
        }
        else if (node is not UnityYamlScalar { IsEmpty: true })
        {
            Mismatch(path, "reference");
        }

        if (fileId == 0)
        {
            return CreatePointer(0, 0);
        }
        if (string.IsNullOrEmpty(guid) || IsZeroGuid(guid))
        {
            return CreatePointer(0, fileId);
        }
        return CreatePointer(project.SlotFor(collection, guid, (int)type), fileId);
    }

    private IUnityAssetBase CreatePointer(int fileIndex, long pathId)
    {
        IPPtr_Object pointer = PPtr_Object.Create(version);
        Span<byte> buffer = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, fileIndex);
        int length;
        if (pointer.Has_PathID__Int64())
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer[4..], pathId);
            length = 12;
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], (int)pathId);
            length = 8;
        }
        EndianSpanReader reader = new(buffer[..length], EndianType.LittleEndian);
        pointer.ReadRelease(ref reader);
        return pointer;
    }

    private static bool IsZeroGuid(string guid)
    {
        foreach (char c in guid)
        {
            if (c != '0')
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryIdentifier(string text, out long identifier)
    {
        if (YamlScalars.TryParseInteger(text, out identifier))
        {
            return true;
        }
        if (YamlScalars.TryParseUnsigned(text, out ulong unsigned))
        {
            identifier = unchecked((long)unsigned);
            return true;
        }
        return false;
    }

    private static bool TryElements<T>(UnityYamlNode node, TryParseNode<T> parse, [NotNullWhen(true)] out T[]? array)
    {
        array = null;
        if (node is UnityYamlScalar { IsEmpty: true })
        {
            array = [];
            return true;
        }
        if (node is not UnityYamlSequence sequence)
        {
            return false;
        }
        T[] result = new T[sequence.Items.Count];
        for (int index = 0; index < result.Length; index++)
        {
            if (!parse(sequence.Items[index], out T element))
            {
                return false;
            }
            result[index] = element;
        }
        array = result;
        return true;
    }

    private static bool TryInteger<T>(UnityYamlNode node, out T value) where T : struct, IBinaryInteger<T>
    {
        if (node is UnityYamlScalar scalar)
        {
            return T.TryParse(scalar.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
        value = default;
        return false;
    }

    private static bool TryBoolean(UnityYamlNode node, out bool value)
    {
        if (node is UnityYamlScalar scalar)
        {
            return YamlScalars.TryParseBoolean(scalar.Text, out value);
        }
        value = false;
        return false;
    }

    private static bool TryCharacter(UnityYamlNode node, out char value)
    {
        if (TryInteger(node, out ushort code))
        {
            value = (char)code;
            return true;
        }
        value = '\0';
        return false;
    }

    private static bool TrySingle(UnityYamlNode node, out float value)
    {
        if (node is UnityYamlScalar scalar)
        {
            return YamlScalars.TryParseSingle(scalar.Text, out value);
        }
        value = 0;
        return false;
    }

    private static bool TryDouble(UnityYamlNode node, out double value)
    {
        if (node is UnityYamlScalar scalar)
        {
            return YamlScalars.TryParseDouble(scalar.Text, out value);
        }
        value = 0;
        return false;
    }

    private static bool TryText(UnityYamlNode node, out string value)
    {
        if (node is UnityYamlScalar scalar)
        {
            value = scalar.Text;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private bool Mismatch(string path, string expected)
    {
        gaps.Add(GapCategories.ShapeMismatch, $"{path}: expected {expected}", collection.Entry.RelativePath);
        return false;
    }

    private bool Unsupported(string path, string what)
    {
        gaps.Add(GapCategories.UnsupportedLayout, $"{path}: {what}", collection.Entry.RelativePath);
        return false;
    }
}
