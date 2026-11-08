using System.Globalization;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Subclasses.Vector2f;
using AssetRipper.SourceGenerated.Subclasses.Vector3f;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// A component of the camera package's own scripts (the assembly <see cref="Assembly"/>) as the record a host reads: whether
/// it is enabled, then every field its script serializes, under the name the script gives it. The post-processing component,
/// which names a profile asset of the render pipeline and nothing of the camera, is left out.
///
/// A pointer is stated as what a layer of nodes can hold. A pointer to an object of the scene (an object, or any component
/// of one) is the source path of that object, the path the scene layer states the node by, so that a pointer to a node the
/// layer does not hold is not a path that reads as nothing: it refuses the record. A pointer to a scriptable object of the
/// package (a noise profile, a table of blends) is the asset itself, embedded with its name, so that one record carries
/// everything the component reads. A pointer to anything else refuses the record too. A vector is an object of its
/// components, x, y and z in lower case.
/// </summary>
public sealed class UnityCinemachine : UnityScriptRecords
{
    public const string Assembly = "Cinemachine";

    private const string PostProcessing = "CinemachinePostProcessing";
    private const string EnabledMember = "m_Enabled";
    private const string NameMember = "m_Name";

    private readonly Func<IGameObject, string?> _pathOf;
    private readonly HashSet<IUnityObjectBase> _embedding = new(ReferenceEqualityComparer.Instance);
    private string _component = string.Empty;

    private UnityCinemachine(Func<IGameObject, string?> pathOf)
    {
        _pathOf = pathOf;
    }

    public static bool Owns(IMonoBehaviour behaviour) => InAssembly(behaviour) && behaviour.ScriptClass() != PostProcessing;

    private static bool InAssembly(IMonoBehaviour behaviour) =>
        behaviour.ScriptP is { } script && script.GetAssemblyNameFixed() == Assembly;

    public static JsonObject Describe(IMonoBehaviour component, Func<IGameObject, string?> pathOf) =>
        new UnityCinemachine(pathOf).Record(component);

    private JsonObject Record(IMonoBehaviour component)
    {
        _component = $"{component.ScriptClass()} on {component.GameObjectP?.Name.String}";
        JsonObject record = Structure(StructureOf(component), component.Collection);
        record.Insert(0, EnabledMember, component.Enabled != 0);
        return record;
    }

    protected override JsonNode? Pointer(IPPtr pointer, AssetCollection collection)
    {
        if (pointer.PathID == 0)
        {
            return null;
        }
        IUnityObjectBase target = collection.TryGetAsset(pointer.FileID, pointer.PathID)
            ?? throw Refused("points at an object none of the loaded archives holds");
        IGameObject? holder = target switch
        {
            IGameObject gameObject => gameObject,
            IComponent component => component.GameObject_C2P,
            _ => null,
        };
        if (holder is not null)
        {
            return _pathOf(holder) ?? throw Refused($"points at '{holder.Name.String}', which is no node of the layer");
        }
        if (target is IMonoBehaviour asset && InAssembly(asset))
        {
            return Embedded(asset);
        }
        throw Refused(target is IMonoBehaviour outside
            ? $"points at the {outside.ScriptClass()} '{outside.Name.String}', a scriptable object outside the package, which a layer of nodes cannot state"
            : $"points at a {target.ClassName}, which a layer of nodes cannot state");
    }

    /// <summary>A real number states itself as one: a whole value keeps its decimal point, so that a field of the script that is a
    /// float reads as a float in every record whatever it holds, and its single-precision value is its exact double.</summary>
    protected override JsonNode Number(double value)
    {
        if (!double.IsFinite(value))
        {
            return base.Number(value);
        }
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return JsonNode.Parse(text.Contains('.') || text.Contains('E') ? text : text + ".0")!;
    }

    protected override JsonNode? EngineStruct(IUnityAssetBase value, AssetCollection collection) => value switch
    {
        IVector2f vector => new JsonObject { ["x"] = Number(vector.X), ["y"] = Number(vector.Y) },
        IVector3f vector => new JsonObject { ["x"] = Number(vector.X), ["y"] = Number(vector.Y), ["z"] = Number(vector.Z) },
        _ => base.EngineStruct(value, collection),
    };

    private JsonObject Embedded(IMonoBehaviour asset)
    {
        if (!_embedding.Add(asset))
        {
            throw Refused($"reaches '{asset.Name.String}' through itself");
        }
        JsonObject record = Structure(StructureOf(asset), asset.Collection);
        record.Insert(0, NameMember, asset.Name.String);
        _embedding.Remove(asset);
        return record;
    }

    private InvalidOperationException Refused(string reason) =>
        new($"{_component}: {Where} {reason}.");
}
