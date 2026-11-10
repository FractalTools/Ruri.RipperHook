using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// The serialized fields of a script object as a JSON record, each stated under the name the game's own script gives it
/// (the game ships no source, but its scripts name their fields) and in the type the script declares: a number as a number,
/// a flag as a flag, an enum as its value, a nested structure as an object, an array as an array, an engine structure by its
/// content. What a pointer to another object becomes is the one thing the records of different scripts do not agree on, so it
/// is the subclass's to state (<see cref="Pointer"/>).
/// </summary>
public abstract class UnityScriptRecords
{
    private static readonly HashSet<string> NotData = new(StringComparer.Ordinal)
    {
        "FlowMappedInYaml", "SerializedVersion", "ClassID", "ClassName", "Collection", "PathID", "AssetInfo", "MainAsset",
    };

    private readonly List<string> _trail = [];

    /// <summary>The field being stated, from the record's top down, in the words of the scripts: what a refusal names.</summary>
    protected string Where => string.Join('.', _trail);

    /// <summary>What a pointer is stated as, null for none: the one decision a record's reader is built around.</summary>
    protected abstract JsonNode? Pointer(IPPtr pointer, AssetCollection collection);

    /// <summary>What a structure states beyond its own fields, added to the object stated for it.</summary>
    protected virtual void Extend(SerializableStructure structure, AssetCollection collection, JsonObject result)
    {
    }

    /// <summary>What an engine structure (a vector, a curve, a mask) is stated as.</summary>
    protected virtual JsonNode? EngineStruct(IUnityAssetBase value, AssetCollection collection) => Generated(value, collection, 0);

    /// <summary>What a null pointer in a list is stated as, null for leaving the place out: a list whose places are
    /// meaningful keeps the place, or every element after it moves up by one.</summary>
    protected virtual JsonNode? NullElement() => null;

    protected static SerializableStructure StructureOf(IMonoBehaviour behaviour)
    {
        SerializableStructure? structure = behaviour.Structure is UnloadedStructure unloaded
            ? unloaded.LoadStructure()
            : behaviour.Structure as SerializableStructure;
        return structure ?? throw new InvalidOperationException(
            $"{behaviour.ScriptClass()} {behaviour.Name.String} has no script layout (its structure is {behaviour.Structure?.GetType().Name ?? "null"}).");
    }

    protected static JsonObject Without(JsonObject source, params string[] names)
    {
        foreach (string name in names)
        {
            source.Remove(name);
        }
        return source;
    }

    protected JsonObject Structure(SerializableStructure structure, AssetCollection collection)
    {
        JsonObject result = new();
        IReadOnlyList<SerializableType.Field> fields = structure.Type.Fields;
        for (int index = 0; index < fields.Count; index++)
        {
            _trail.Add(fields[index].Name);
            JsonNode? value = fields[index].ArrayDepth switch
            {
                0 => Scalar(fields[index].Type, structure.Fields[index], collection),
                1 => Elements(fields[index].Type, structure.Fields[index], collection),
                _ => throw new NotSupportedException($"{structure.Type.FullName}.{fields[index].Name} nests arrays."),
            };
            _trail.RemoveAt(_trail.Count - 1);
            if (value is not null)
            {
                result[fields[index].Name] = value;
            }
        }
        Extend(structure, collection, result);
        return result;
    }

    private JsonNode? Scalar(SerializableType type, SerializableValue value, AssetCollection collection) => type.Type switch
    {
        PrimitiveType.Bool => value.AsBoolean,
        PrimitiveType.Char => (int)value.AsChar,
        PrimitiveType.SByte => (int)value.AsSByte,
        PrimitiveType.Byte => (int)value.AsByte,
        PrimitiveType.Short => (int)value.AsInt16,
        PrimitiveType.UShort => (int)value.AsUInt16,
        PrimitiveType.Int => value.AsInt32,
        PrimitiveType.UInt => (long)value.AsUInt32,
        PrimitiveType.Long => Wide(value.AsInt64),
        PrimitiveType.ULong => value.AsUInt64 <= long.MaxValue ? Wide((long)value.AsUInt64) : value.AsUInt64.ToString(CultureInfo.InvariantCulture),
        PrimitiveType.Single => Number(value.AsSingle),
        PrimitiveType.Double => Number(value.AsDouble),
        PrimitiveType.String => value.AsString,
        _ => Complex(value, collection),
    };

    private JsonNode? Complex(SerializableValue value, AssetCollection collection)
    {
        switch (value.CValue)
        {
            case IPPtr pointer:
                return Pointer(pointer, collection);
            case SerializableStructure structure:
                return Structure(structure, collection);
            case IUnityAssetBase generated:
                return EngineStruct(generated, collection);
            default:
                return null;
        }
    }

    protected JsonNode? Generated(IUnityAssetBase asset, AssetCollection collection, int depth)
    {
        if (depth > 6)
        {
            return null;
        }
        JsonObject result = new();
        foreach (PropertyInfo property in asset.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (NotData.Contains(property.Name) || property.GetIndexParameters().Length != 0
                || property.Name.StartsWith("Has_", StringComparison.Ordinal)
                || typeof(IUnityObjectBase).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }
            JsonNode? node = Reflected(property.GetValue(asset), collection, depth);
            if (node is not null)
            {
                result[property.Name] = node;
            }
        }
        return result;
    }

    private JsonNode? Reflected(object? value, AssetCollection collection, int depth)
    {
        switch (value)
        {
            case null: return null;
            case bool flag: return flag;
            case byte or sbyte or short or ushort or int: return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            case uint unsigned: return (long)unsigned;
            case long wide: return Wide(wide);
            case ulong unsignedWide: return unsignedWide <= long.MaxValue ? Wide((long)unsignedWide) : unsignedWide.ToString(CultureInfo.InvariantCulture);
            case float single: return Number(single);
            case double precise: return Number(precise);
            case string text: return text;
            case Utf8String utf8: return utf8.String;
            case Enum member: return Convert.ToInt32(member, CultureInfo.InvariantCulture);
            case IPPtr pointer: return Pointer(pointer, collection);
            case IUnityAssetBase nested: return Generated(nested, collection, depth + 1);
            case IEnumerable list:
                JsonArray array = [];
                foreach (object? item in list)
                {
                    JsonNode? element = Reflected(item, collection, depth);
                    if (element is not null)
                    {
                        array.Add(element);
                    }
                }
                return array.Count == 0 ? null : array;
            default: return null;
        }
    }

    private JsonNode? Elements(SerializableType type, SerializableValue value, AssetCollection collection)
    {
        JsonArray array = [];
        switch (type.Type)
        {
            case PrimitiveType.Bool: foreach (bool item in value.AsBooleanArray) array.Add(item); break;
            case PrimitiveType.Byte: foreach (byte item in value.AsByteArray) array.Add((int)item); break;
            case PrimitiveType.Short: foreach (short item in value.AsInt16Array) array.Add((int)item); break;
            case PrimitiveType.UShort: foreach (ushort item in value.AsUInt16Array) array.Add((int)item); break;
            case PrimitiveType.Int: foreach (int item in value.AsInt32Array) array.Add(item); break;
            case PrimitiveType.UInt: foreach (uint item in value.AsUInt32Array) array.Add((long)item); break;
            case PrimitiveType.Long: foreach (long item in value.AsInt64Array) array.Add(Wide(item)); break;
            case PrimitiveType.Single: foreach (float item in value.AsSingleArray) array.Add(Number(item)); break;
            case PrimitiveType.Double: foreach (double item in value.AsDoubleArray) array.Add(Number(item)); break;
            case PrimitiveType.String: foreach (string item in value.AsStringArray) array.Add(item); break;
            default:
                foreach (IUnityAssetBase item in value.AsAssetArray)
                {
                    JsonNode? element = item switch
                    {
                        IPPtr pointer => Pointer(pointer, collection),
                        SerializableStructure structure => Structure(structure, collection),
                        IUnityAssetBase generated => EngineStruct(generated, collection),
                        _ => null,
                    };
                    if (element is not null)
                    {
                        array.Add(element);
                    }
                    else if (item is IPPtr && NullElement() is { } hole)
                    {
                        array.Add(hole);
                    }
                }
                break;
        }
        return array.Count == 0 ? null : array;
    }

    protected static JsonNode Wide(long value) =>
        Math.Abs(value) < (1L << 53) ? value : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A real number. One that is not finite is its name in text, which JSON has no number for.</summary>
    protected virtual JsonNode Number(double value) => double.IsFinite(value)
        ? value
        : double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";
}
