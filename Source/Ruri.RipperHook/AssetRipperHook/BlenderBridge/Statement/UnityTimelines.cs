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
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_320;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.PropertyName;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// A timeline asset as the record a host reads: its tracks in a depth-first walk (a group track is followed by its
/// children), each track's clips with the playable asset each clip plays, each track's markers, and -- when a director
/// plays it -- the director and the object each track is bound to. Every field the game's own scripts serialize is
/// stated under the name the script gives it (the game ships no source, but the scripts name their fields), a pointer
/// as the key of what it points at, so that a host models the fields it uses and can refuse the ones it does not
/// rather than lose them. An exposed reference (a clip's way of naming a scene object, resolved by the director that
/// plays it) is stated as the scene path of what it resolves to.
/// </summary>
public sealed class UnityTimelines
{
    private const string TimelineScript = "TimelineAsset";
    private const string TracksField = "m_Tracks";
    private const string ChildrenField = "m_Children";
    private const string ClipsField = "m_Clips";
    private const string MarkersField = "m_Markers";
    private const string AssetField = "m_Asset";

    private readonly Func<IUnityObjectBase, string> _keyOf;
    private readonly Func<IGameObject, string?> _pathOf;
    private readonly IPlayableDirector? _director;
    private int _tracks;

    private UnityTimelines(Func<IUnityObjectBase, string> keyOf, Func<IGameObject, string?> pathOf, IPlayableDirector? director)
    {
        _keyOf = keyOf;
        _pathOf = pathOf;
        _director = director;
    }

    public static bool IsTimeline(IMonoBehaviour behaviour) => behaviour.ScriptClass() == TimelineScript;

    public static JsonObject Describe(IMonoBehaviour timeline, IPlayableDirector? director, string directorPath,
        Func<IUnityObjectBase, string> keyOf, Func<IGameObject, string?> pathOf) =>
        new UnityTimelines(keyOf, pathOf, director).Record(timeline, directorPath);

    private JsonObject Record(IMonoBehaviour timeline, string directorPath)
    {
        SerializableStructure structure = StructureOf(timeline);
        JsonObject record = new()
        {
            ["sourceKey"] = _keyOf(timeline),
            ["sourceName"] = timeline.Name.String,
        };
        if (_director is not null)
        {
            record["director"] = new JsonObject
            {
                ["path"] = directorPath,
                ["initialState"] = _director.InitialState_C320,
                ["wrapMode"] = _director.WrapMode_C320,
                ["updateMode"] = _director.DirectorUpdateMode_C320,
                ["initialTime"] = Number(_director.InitialTime_C320),
                ["enabled"] = _director.Enabled_C320 != 0,
            };
        }
        record["asset"] = Without(Structure(structure, timeline.Collection), TracksField);

        JsonArray tracks = [];
        if (structure.TryGetField(TracksField, out SerializableValue list))
        {
            foreach (IMonoBehaviour track in Pointed(list, timeline.Collection))
            {
                Track(track, -1, timeline, tracks);
            }
        }
        record["tracks"] = tracks;
        return record;
    }

    private void Track(IMonoBehaviour track, int parent, IMonoBehaviour timeline, JsonArray into)
    {
        int index = _tracks++;
        SerializableStructure structure = StructureOf(track);
        JsonObject entry = new()
        {
            ["index"] = index,
            ["type"] = track.ScriptClass(),
            ["key"] = _keyOf(track),
            ["name"] = track.Name.String,
            ["fields"] = Without(Structure(structure, track.Collection), ClipsField, ChildrenField, MarkersField),
        };
        if (parent >= 0)
        {
            entry["parent"] = parent;
        }
        if (Binding(track) is { } binding)
        {
            entry["binding"] = binding;
        }

        JsonArray clips = [];
        if (structure.TryGetField(ClipsField, out SerializableValue clipList))
        {
            foreach (IUnityAssetBase item in clipList.AsAssetArray)
            {
                if (item is SerializableStructure clip)
                {
                    clips.Add(Clip(clip, track.Collection));
                }
            }
        }
        entry["clips"] = clips;

        JsonArray markers = [];
        if (structure.TryGetField(MarkersField, out SerializableValue markerList) && markerList.CValue is SerializableStructure markerBlock
            && markerBlock.TryGetField("m_Objects", out SerializableValue objects))
        {
            foreach (IMonoBehaviour marker in Pointed(objects, track.Collection))
            {
                markers.Add(Marker(marker));
            }
        }
        entry["markers"] = markers;
        into.Add(entry);

        if (structure.TryGetField(ChildrenField, out SerializableValue children))
        {
            foreach (IMonoBehaviour child in Pointed(children, track.Collection))
            {
                Track(child, index, timeline, into);
            }
        }
    }

    private JsonObject Clip(SerializableStructure clip, AssetCollection collection)
    {
        JsonObject entry = new() { ["fields"] = Without(Structure(clip, collection), AssetField) };
        if (clip.TryGetField(AssetField, out SerializableValue pointer) && pointer.CValue is IPPtr target
            && collection.TryGetAsset(target.FileID, target.PathID) is IMonoBehaviour played)
        {
            entry["assetType"] = played.ScriptClass();
            entry["assetKey"] = _keyOf(played);
            entry["asset"] = Structure(StructureOf(played), played.Collection);
        }
        return entry;
    }

    private JsonObject Marker(IMonoBehaviour marker)
    {
        SerializableStructure structure = StructureOf(marker);
        JsonObject entry = new()
        {
            ["type"] = marker.ScriptClass(),
            ["key"] = _keyOf(marker),
            ["fields"] = Structure(structure, marker.Collection),
        };
        if (structure.TryGetField("m_Asset", out SerializableValue signal) && signal.CValue is IPPtr signalPointer
            && marker.Collection.TryGetAsset(signalPointer.FileID, signalPointer.PathID) is { } signalAsset)
        {
            entry["signal"] = signalAsset is IMonoBehaviour signalBehaviour ? signalBehaviour.Name.String : signalAsset.GetBestName();
        }
        return entry;
    }

    private JsonObject? Binding(IMonoBehaviour track)
    {
        if (_director is null)
        {
            return null;
        }
        foreach (var binding in _director.SceneBindings_C320)
        {
            if (!ReferenceEquals(binding.Key.TryGetAsset(_director.Collection), track))
            {
                continue;
            }
            IUnityObjectBase? bound = binding.Value.TryGetAsset(_director.Collection);
            return bound is null ? null : Bound(bound);
        }
        return null;
    }

    private JsonObject? Bound(IUnityObjectBase bound)
    {
        IGameObject? holder = bound switch
        {
            IGameObject gameObject => gameObject,
            IComponent component => component.GameObject_C2P,
            _ => null,
        };
        if (holder is null)
        {
            return null;
        }
        return new JsonObject
        {
            ["path"] = _pathOf(holder),
            ["class"] = bound is IMonoBehaviour behaviour ? behaviour.ScriptClass() : bound.ClassName,
        };
    }

    private static IEnumerable<IMonoBehaviour> Pointed(SerializableValue list, AssetCollection collection)
    {
        foreach (IUnityAssetBase item in list.AsAssetArray)
        {
            if (item is IPPtr pointer && collection.TryGetAsset(pointer.FileID, pointer.PathID) is IMonoBehaviour behaviour)
            {
                yield return behaviour;
            }
        }
    }

    private static SerializableStructure StructureOf(IMonoBehaviour behaviour)
    {
        SerializableStructure? structure = behaviour.Structure is UnloadedStructure unloaded
            ? unloaded.LoadStructure()
            : behaviour.Structure as SerializableStructure;
        return structure ?? throw new InvalidOperationException(
            $"{behaviour.ScriptClass()} {behaviour.Name.String} has no script layout (its structure is {behaviour.Structure?.GetType().Name ?? "null"}).");
    }

    private static JsonObject Without(JsonObject source, params string[] names)
    {
        foreach (string name in names)
        {
            source.Remove(name);
        }
        return source;
    }

    private JsonObject Structure(SerializableStructure structure, AssetCollection collection)
    {
        JsonObject result = new();
        IReadOnlyList<SerializableType.Field> fields = structure.Type.Fields;
        for (int index = 0; index < fields.Count; index++)
        {
            JsonNode? value = fields[index].ArrayDepth switch
            {
                0 => Scalar(fields[index].Type, structure.Fields[index], collection),
                1 => Elements(fields[index].Type, structure.Fields[index], collection),
                _ => throw new NotSupportedException($"{structure.Type.FullName}.{fields[index].Name} nests arrays."),
            };
            if (value is not null)
            {
                result[fields[index].Name] = value;
            }
        }
        if (_director is not null && structure.TryGetField("exposedName", out SerializableValue exposed)
            && structure.ContainsField("defaultValue"))
        {
            JsonObject? resolved = Exposed(exposed, collection);
            if (resolved is not null)
            {
                result["exposed"] = resolved;
            }
        }
        return result;
    }

    private static bool SameName(IPropertyName left, IPropertyName right)
    {
        string leftText = left.Id_Utf8String.String;
        string rightText = right.Id_Utf8String.String;
        return leftText.Length > 0 || rightText.Length > 0 ? leftText == rightText : left.Id_Int32 == right.Id_Int32;
    }

    private JsonObject? Exposed(SerializableValue exposedName, AssetCollection collection)
    {
        if (_director is null || exposedName.CValue is not IPropertyName name)
        {
            return null;
        }
        var table = _director.ExposedReferences_C320.References_Release;
        for (int index = 0; index < table.Count; index++)
        {
            if (SameName(table.GetKey(index), name))
            {
                IUnityObjectBase? target = table.GetValue(index).TryGetAsset(_director.Collection);
                return target is null ? null : Bound(target);
            }
        }
        return null;
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
                return collection.TryGetAsset(pointer.FileID, pointer.PathID) is { } target ? _keyOf(target) : null;
            case SerializableStructure structure:
                return Structure(structure, collection);
            case IUnityAssetBase generated:
                return Generated(generated, collection, 0);
            default:
                return null;
        }
    }

    private static readonly HashSet<string> NotData = new(StringComparer.Ordinal)
    {
        "FlowMappedInYaml", "SerializedVersion", "ClassID", "ClassName", "Collection", "PathID", "AssetInfo", "MainAsset",
    };

    private JsonNode? Generated(IUnityAssetBase asset, AssetCollection collection, int depth)
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
            case IPPtr pointer: return collection.TryGetAsset(pointer.FileID, pointer.PathID) is { } target ? _keyOf(target) : null;
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
                        IPPtr pointer => collection.TryGetAsset(pointer.FileID, pointer.PathID) is { } target ? _keyOf(target) : null,
                        SerializableStructure structure => Structure(structure, collection),
                        IUnityAssetBase generated => Generated(generated, collection, 0),
                        _ => null,
                    };
                    if (element is not null)
                    {
                        array.Add(element);
                    }
                }
                break;
        }
        return array.Count == 0 ? null : array;
    }

    private static JsonNode Wide(long value) =>
        Math.Abs(value) < (1L << 53) ? value : value.ToString(CultureInfo.InvariantCulture);

    private static JsonNode Number(double value) => double.IsFinite(value)
        ? value
        : double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";
}
