using System.Globalization;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Extensions;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// A component of a node, or a scriptable object, as the record a host reads: which script it runs (assembly, namespace,
/// class), where it sits, whether it is enabled, and every field its script serializes, each under the name the script gives
/// it and in the type the script declares. A component the game's scripts do not define (a rect transform, a canvas) is stated
/// the same way from its own serialized members.
///
/// A pointer states what it points at in one of two forms. An object of the statement's own hierarchies (a node, or a
/// component of one) is a <c>node</c> reference: the key of the object, the key of the root object it hangs under, its source
/// path with the number of nodes of that path before it, its class and the number of components of that class before it on
/// its node. Anything else is an <c>asset</c> reference: the key, the class and the name of the object. A null pointer is no
/// member, and a null place in a list is a <c>none</c> reference, so that every other place keeps its index. A pointer to an
/// object no loaded archive holds is null as it is when the game runs, and is named in <see cref="Dangling"/>.
/// </summary>
public sealed class UnityScripts : UnityScriptRecords
{
    /// <summary>Where a node of the statement sits: its source path, how many nodes of that path come before it in the
    /// statement, and the key of the root object whose hierarchy it is part of.</summary>
    public sealed record Placement(string Path, int PathOrdinal, string RootKey);

    private const string MonoBehaviourClass = "MonoBehaviour";

    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    private readonly Func<IUnityObjectBase, string> _keyOf;
    private readonly Func<IGameObject, Placement?> _placementOf;
    private readonly List<string> _dangling = [];
    private readonly List<string> _unreadable = [];
    private readonly bool _allowUnreadable;
    private readonly ScriptMemberFilter _members;
    private string _owner = string.Empty;

    public UnityScripts(Func<IUnityObjectBase, string> keyOf, Func<IGameObject, Placement?> placementOf, bool allowUnreadable,
        ScriptMemberFilter members)
    {
        _keyOf = keyOf;
        _placementOf = placementOf;
        _allowUnreadable = allowUnreadable;
        _members = members;
    }

    /// <summary>The pointers that pointed at nothing a loaded archive holds, each with the field it was found in.</summary>
    public IReadOnlyList<string> Dangling => _dangling;

    /// <summary>The scripts stated as unreadable, each with why.</summary>
    public IReadOnlyList<string> Unreadable => _unreadable;

    public static string ClassNameOf(IUnityObjectBase target) => target switch
    {
        IMonoBehaviour behaviour => behaviour.ScriptClass() is { Length: > 0 } name ? name : MonoBehaviourClass,
        _ => target.ClassName,
    };

    internal static string NameOf(IUnityObjectBase target) => target switch
    {
        IMonoBehaviour behaviour when behaviour.Name.String.Length > 0 => behaviour.Name.String,
        IMonoBehaviour behaviour => behaviour.GameObjectP?.Name.String ?? string.Empty,
        IComponent component => component.GameObject_C2P?.Name.String ?? string.Empty,
        _ => target.GetBestName(),
    };

    private static string NamespaceOf(IMonoBehaviour behaviour) =>
        behaviour.ScriptP is { } script ? script.Namespace.String : string.Empty;

    private static string AssemblyOf(IMonoBehaviour behaviour) =>
        behaviour.ScriptP is { } script ? script.GetAssemblyNameFixed() : string.Empty;

    /// <summary>A component placed on a node.</summary>
    public JsonObject Component(IComponent component, IGameObject holder, Placement placement)
    {
        string className = ClassNameOf(component);
        _owner = $"{className} on {placement.Path}";
        JsonObject record = new()
        {
            [ScriptRecordMembers.Key] = _keyOf(component),
            [ScriptRecordMembers.Root] = placement.RootKey,
            [ScriptRecordMembers.Path] = placement.Path,
            [ScriptRecordMembers.PathOrdinal] = placement.PathOrdinal,
            [ScriptRecordMembers.Class] = className,
        };
        if (component is IMonoBehaviour behaviour)
        {
            string assembly = AssemblyOf(behaviour);
            record[ScriptRecordMembers.Namespace] = NamespaceOf(behaviour);
            record[ScriptRecordMembers.Assembly] = assembly;
            record[ScriptRecordMembers.ClassOrdinal] = ClassOrdinal(component, holder);
            record[ScriptRecordMembers.Enabled] = behaviour.Enabled != 0;
            if (behaviour.Name.String.Length > 0)
            {
                record[ScriptRecordMembers.Name] = behaviour.Name.String;
            }
            if (!TryStructure(behaviour, out JsonObject? statedFields))
            {
                record[ScriptRecordMembers.Unreadable] = _unreadable[^1];
                return record;
            }
            record[ScriptRecordMembers.Fields] = statedFields;
            UnityScriptAdditions.Add(record, assembly, className, behaviour.Name.String);
            return record;
        }
        record[ScriptRecordMembers.Namespace] = string.Empty;
        record[ScriptRecordMembers.Assembly] = string.Empty;
        record[ScriptRecordMembers.ClassOrdinal] = ClassOrdinal(component, holder);
        JsonObject fields = Walk(component, component.Collection, UnityScriptAdditions.Dropped(className), _members.Of(className));
        if (fields.Remove(ScriptRecordMembers.EnabledField, out JsonNode? enabled) && enabled is not null)
        {
            record[ScriptRecordMembers.Enabled] = enabled.GetValue<int>() != 0;
        }
        record[ScriptRecordMembers.Fields] = fields;
        return record;
    }

    /// <summary>A scriptable object: a script object that sits on no node.</summary>
    public JsonObject Asset(IMonoBehaviour asset, string container)
    {
        string className = ClassNameOf(asset);
        string assembly = AssemblyOf(asset);
        _owner = $"{className} {asset.Name.String}";
        JsonObject record = new()
        {
            [ScriptRecordMembers.Key] = _keyOf(asset),
            [ScriptRecordMembers.Class] = className,
            [ScriptRecordMembers.Namespace] = NamespaceOf(asset),
            [ScriptRecordMembers.Assembly] = assembly,
            [ScriptRecordMembers.Name] = asset.Name.String,
        };
        if (container.Length > 0)
        {
            record[ScriptRecordMembers.Container] = container;
        }
        if (!TryStructure(asset, out JsonObject? fields))
        {
            record[ScriptRecordMembers.Unreadable] = _unreadable[^1];
            return record;
        }
        record[ScriptRecordMembers.Fields] = fields;
        UnityScriptAdditions.Add(record, assembly, className, asset.Name.String);
        return record;
    }

    /// <summary>An asset of the engine's own classes (an audio mixer, a group of one): no script defines it, so it is stated from its
    /// own serialized members like a component of the engine, under the members the request names for its class.</summary>
    public JsonObject NativeAsset(IUnityObjectBase asset, string container)
    {
        string className = asset.ClassName;
        string name = NameOf(asset);
        _owner = $"{className} {name}";
        JsonObject record = new()
        {
            [ScriptRecordMembers.Key] = _keyOf(asset),
            [ScriptRecordMembers.Class] = className,
            [ScriptRecordMembers.Namespace] = string.Empty,
            [ScriptRecordMembers.Assembly] = string.Empty,
            [ScriptRecordMembers.Name] = name,
        };
        if (container.Length > 0)
        {
            record[ScriptRecordMembers.Container] = container;
        }
        record[ScriptRecordMembers.Fields] = Walk(asset, asset.Collection, UnityScriptAdditions.Dropped(className), _members.Of(className));
        return record;
    }

    private bool TryStructure(IMonoBehaviour behaviour, out JsonObject? fields)
    {
        try
        {
            fields = Structure(StructureOf(behaviour), behaviour.Collection);
            return true;
        }
        catch (InvalidOperationException unreadable) when (_allowUnreadable && StructureMissing(behaviour))
        {
            _unreadable.Add($"{_owner}: {unreadable.Message}");
            fields = null;
            return false;
        }
    }

    private static bool StructureMissing(IMonoBehaviour behaviour) => behaviour.Structure is null or UnloadedStructure;

    private JsonObject Walk(IUnityAssetBase value, AssetCollection collection, IReadOnlySet<string> skipped, ClassMemberSet? members) =>
        UnityJsonWalker.Walk(value, (fileId, pathId) => Resolve(fileId, pathId, collection), NullElement, Number, skipped, members) as JsonObject
        ?? throw Refused("is not an object");

    protected override JsonNode? EngineStruct(IUnityAssetBase value, AssetCollection collection) =>
        UnityJsonWalker.Walk(value, (fileId, pathId) => Resolve(fileId, pathId, collection), NullElement, Number, NoNames, null);

    protected override JsonNode? NullElement() =>
        new JsonObject { [ScriptRecordMembers.Kind] = ScriptRecordMembers.NoReference };

    /// <summary>A real number states itself as one: a whole value keeps its decimal point, so that a field of the script that
    /// is a float reads as a float in every record whatever it holds, and its single-precision value is its exact double.</summary>
    protected override JsonNode Number(double value)
    {
        if (!double.IsFinite(value))
        {
            return base.Number(value);
        }
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return JsonNode.Parse(text.Contains('.') || text.Contains('E') ? text : text + ".0")!;
    }

    protected override JsonNode? Pointer(IPPtr pointer, AssetCollection collection) => Resolve(pointer.FileID, pointer.PathID, collection);

    private JsonNode? Resolve(int fileId, long pathId, AssetCollection collection)
    {
        if (pathId == 0)
        {
            return null;
        }
        IUnityObjectBase? target = collection.TryGetAsset(fileId, pathId);
        if (target is null)
        {
            _dangling.Add($"{_owner}: {Where}");
            return null;
        }
        IGameObject? holder = target switch
        {
            IGameObject gameObject => gameObject,
            IComponent component => component.GameObject_C2P,
            _ => null,
        };
        if (holder is not null && _placementOf(holder) is { } placement)
        {
            return new JsonObject
            {
                [ScriptRecordMembers.Kind] = ScriptRecordMembers.NodeReference,
                [ScriptRecordMembers.Key] = _keyOf(target),
                [ScriptRecordMembers.Root] = placement.RootKey,
                [ScriptRecordMembers.Path] = placement.Path,
                [ScriptRecordMembers.PathOrdinal] = placement.PathOrdinal,
                [ScriptRecordMembers.Class] = ClassNameOf(target),
                [ScriptRecordMembers.ClassOrdinal] = target is IComponent component ? ClassOrdinal(component, holder) : 0,
            };
        }
        return new JsonObject
        {
            [ScriptRecordMembers.Kind] = ScriptRecordMembers.AssetReference,
            [ScriptRecordMembers.Key] = _keyOf(target),
            [ScriptRecordMembers.Class] = ClassNameOf(target),
            [ScriptRecordMembers.Name] = NameOf(target),
        };
    }

    private int ClassOrdinal(IComponent component, IGameObject holder)
    {
        string className = ClassNameOf(component);
        int ordinal = 0;
        foreach (IComponent? sibling in holder.GetComponentAccessList())
        {
            if (sibling is null)
            {
                continue;
            }
            if (ReferenceEquals(sibling, component))
            {
                return ordinal;
            }
            if (ClassNameOf(sibling) == className)
            {
                ordinal++;
            }
        }
        throw Refused($"names a {className} that '{holder.Name.String}' does not carry");
    }

    private InvalidOperationException Refused(string reason) => new($"{_owner}: {Where} {reason}.");
}
