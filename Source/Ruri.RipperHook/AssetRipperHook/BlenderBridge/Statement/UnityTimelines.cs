using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly.Serializable;
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
public sealed class UnityTimelines : UnityScriptRecords
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

    protected override JsonNode? Pointer(IPPtr pointer, AssetCollection collection) =>
        collection.TryGetAsset(pointer.FileID, pointer.PathID) is { } target ? _keyOf(target) : null;

    protected override void Extend(SerializableStructure structure, AssetCollection collection, JsonObject result)
    {
        if (_director is not null && structure.TryGetField("exposedName", out SerializableValue exposed)
            && structure.ContainsField("defaultValue"))
        {
            JsonObject? resolved = Exposed(exposed, collection);
            if (resolved is not null)
            {
                result["exposed"] = resolved;
            }
        }
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
}
