using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// An object of the engine's own generated classes (a vector, a colour, a curve, a mask, a component) as the JSON a record
/// holds, each member under the name the player's own data gives it -- which is what the release walk of the object states,
/// and what neither the property names of the generated class (their PascalCase) nor the standard walk (its own spelling, and the
/// members an editor adds) are. A pointer is what the caller makes of it, a list with no
/// element is no member, and a number is what the caller states a real number as.
/// </summary>
internal sealed class UnityJsonWalker : AssetWalker
{
    private sealed class Frame
    {
        public JsonObject? Object { get; init; }

        public JsonArray? Array { get; init; }

        public string? Field { get; set; }
    }

    private readonly Func<int, long, JsonNode?> _pointer;
    private readonly Func<JsonNode?> _hole;
    private readonly Func<double, JsonNode> _number;
    private readonly IReadOnlySet<string> _skipped;
    private readonly ClassMemberSet? _members;
    private readonly List<Frame> _frames = [];
    private JsonNode? _result;

    public UnityJsonWalker(Func<int, long, JsonNode?> pointer, Func<JsonNode?> hole, Func<double, JsonNode> number,
        IReadOnlySet<string> skipped, ClassMemberSet? members)
    {
        _pointer = pointer;
        _hole = hole;
        _number = number;
        _skipped = skipped;
        _members = members;
    }

    public JsonNode? Result => _result;

    public static JsonNode? Walk(IUnityAssetBase value, Func<int, long, JsonNode?> pointer, Func<JsonNode?> hole,
        Func<double, JsonNode> number, IReadOnlySet<string> skipped, ClassMemberSet? members)
    {
        UnityJsonWalker walker = new(pointer, hole, number, skipped, members);
        value.WalkRelease(walker);
        return walker.Result;
    }

    private string PathOf(string name)
    {
        StringBuilder path = new();
        for (int index = 0; index < _frames.Count - 1; index++)
        {
            if (_frames[index].Object is not null && _frames[index].Field is { } field)
            {
                path.Append(field).Append('.');
            }
        }
        return path.Append(name).ToString();
    }

    private void Attach(JsonNode? value)
    {
        if (_frames.Count == 0)
        {
            _result = value;
            return;
        }
        Frame top = _frames[^1];
        if (top.Object is not null)
        {
            if (value is not null && top.Field is not null)
            {
                Assign(top.Object, top.Field, value);
            }
        }
        else if (value is not null)
        {
            top.Array!.Add(value);
        }
    }

    /// <summary>A member the player's data names as an element of a fixed-size array (<c>bytes[3]</c>) is an element of the
    /// array member of the base name, in index order, so that a record carries it as the list it is.</summary>
    private static void Assign(JsonObject target, string field, JsonNode value)
    {
        int open = field.Length > 3 && field[^1] == ']' ? field.LastIndexOf('[') : -1;
        if (open <= 0 || !int.TryParse(field.AsSpan(open + 1, field.Length - open - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            target[field] = value;
            return;
        }
        string name = field[..open];
        JsonArray elements;
        if (target[name] is null)
        {
            elements = [];
            target[name] = elements;
        }
        else
        {
            elements = target[name] as JsonArray
                ?? throw new NotSupportedException($"member {field} is an element of {name}, which is not an array.");
        }
        if (index != elements.Count)
        {
            throw new NotSupportedException($"member {field} is not the next element of {name}, which has {elements.Count}.");
        }
        elements.Add(value);
    }

    public override bool EnterAsset(IUnityAssetBase asset)
    {
        _frames.Add(new Frame { Object = new JsonObject() });
        return true;
    }

    public override void ExitAsset(IUnityAssetBase asset)
    {
        Frame frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        Attach(frame.Object);
    }

    public override bool EnterField(IUnityAssetBase asset, string name)
    {
        if (_frames.Count == 1 && _skipped.Contains(name))
        {
            return false;
        }
        if (_members is not null && !_members.Allows(PathOf(name)))
        {
            return false;
        }
        _frames[^1].Field = name;
        return true;
    }

    public override void ExitField(IUnityAssetBase asset, string name)
    {
        _frames[^1].Field = null;
    }

    public override bool EnterList<T>(IReadOnlyList<T> list)
    {
        if (list.Count == 0)
        {
            return false;
        }
        _frames.Add(new Frame { Array = new JsonArray() });
        return true;
    }

    public override void ExitList<T>(IReadOnlyList<T> list)
    {
        Frame frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        Attach(frame.Array);
    }

    public override bool EnterDictionary<TKey, TValue>(IReadOnlyCollection<KeyValuePair<TKey, TValue>> dictionary) =>
        throw new NotSupportedException($"a dictionary of {typeof(TKey).Name} to {typeof(TValue).Name} has no record form.");

    public override void VisitPPtr<TAsset>(PPtr<TAsset> pptr)
    {
        JsonNode? stated = pptr.IsNull ? null : _pointer(pptr.FileID, pptr.PathID);
        if (stated is null)
        {
            AttachHole();
            return;
        }
        Attach(stated);
    }

    private void AttachHole()
    {
        if (_frames.Count > 0 && _frames[^1].Array is not null && _hole() is { } hole)
        {
            Attach(hole);
        }
    }

    public override void VisitPrimitive<T>(T value)
    {
        Attach(Primitive(value));
    }

    private JsonNode? Primitive(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        byte or sbyte or short or ushort or int => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        char character => (int)character,
        uint unsigned => (long)unsigned,
        long wide => Wide(wide),
        ulong unsignedWide => unsignedWide <= long.MaxValue ? Wide((long)unsignedWide) : unsignedWide.ToString(CultureInfo.InvariantCulture),
        float single => _number(single),
        double precise => _number(precise),
        string text => text,
        Utf8String utf8 => utf8.String,
        byte[] bytes => bytes.Length == 0 ? null : new JsonArray(bytes.Select(entry => (JsonNode?)JsonValue.Create((int)entry)).ToArray()),
        Enum member => Convert.ToInt32(member, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"a {value.GetType().Name} has no record form."),
    };

    private static JsonNode Wide(long value) =>
        Math.Abs(value) < (1L << 53) ? value : value.ToString(CultureInfo.InvariantCulture);
}
