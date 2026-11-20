namespace Ruri.RipperHook.RuriEngine;

internal abstract class Value
{
}

internal sealed class NullValue : Value
{
    public static NullValue Instance { get; } = new();

    private NullValue()
    {
    }
}

internal sealed class ObjectValue : Value
{
    private readonly List<KeyValuePair<string, Value>> fields = new();

    public IReadOnlyList<KeyValuePair<string, Value>> Fields => fields;

    public int Count => fields.Count;

    public void Add(string name, Value value) => fields.Add(new KeyValuePair<string, Value>(name, value));

    public bool TryGet(string name, [NotNullWhen(true)] out Value? value)
    {
        foreach (KeyValuePair<string, Value> field in fields)
        {
            if (field.Key == name)
            {
                value = field.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}

internal sealed class ArrayValue : Value
{
    public List<Value> Items { get; } = new();
}

internal enum ScalarKind
{
    Boolean,
    Integer,
    Unsigned,
    Single,
    Double,
    Text,
    Bytes,
}

internal sealed class ScalarValue : Value
{
    private ScalarValue(ScalarKind kind, long signed, ulong unsigned, double floating, string text, byte[] bytes)
    {
        Kind = kind;
        Signed = signed;
        Unsigned = unsigned;
        Floating = floating;
        Text = text;
        Bytes = bytes;
    }

    public ScalarKind Kind { get; }

    public long Signed { get; }

    public ulong Unsigned { get; }

    public double Floating { get; }

    public string Text { get; }

    public byte[] Bytes { get; }

    public bool IsNumber => Kind is ScalarKind.Integer or ScalarKind.Unsigned or ScalarKind.Single or ScalarKind.Double;

    public bool IsIntegral => Kind is ScalarKind.Integer or ScalarKind.Unsigned;

    public static ScalarValue FromBoolean(bool value) => new(ScalarKind.Boolean, value ? 1 : 0, 0, 0, string.Empty, []);

    public static ScalarValue FromInteger(long value) => new(ScalarKind.Integer, value, 0, 0, string.Empty, []);

    public static ScalarValue FromUnsigned(ulong value) => value <= long.MaxValue
        ? FromInteger((long)value)
        : new ScalarValue(ScalarKind.Unsigned, 0, value, 0, string.Empty, []);

    public static ScalarValue FromSingle(float value) => new(ScalarKind.Single, 0, 0, value, string.Empty, []);

    public static ScalarValue FromDouble(double value) => new(ScalarKind.Double, 0, 0, value, string.Empty, []);

    public static ScalarValue FromText(string value) => new(ScalarKind.Text, 0, 0, 0, value, []);

    public static ScalarValue FromBytes(byte[] value) => new(ScalarKind.Bytes, 0, 0, 0, string.Empty, value);

    public bool AsBoolean => Kind == ScalarKind.Boolean ? Signed != 0 : Signed != 0 || Unsigned != 0;

    public double AsDouble => Kind switch
    {
        ScalarKind.Single or ScalarKind.Double => Floating,
        ScalarKind.Unsigned => Unsigned,
        _ => Signed,
    };
}

internal sealed class ReferenceValue : Value
{
    public ReferenceValue(long fileId, string? guid, int type)
    {
        FileId = fileId;
        Guid = guid;
        Type = type;
    }

    public long FileId { get; }

    public string? Guid { get; }

    public int Type { get; }

    public bool IsNull => FileId == 0;

    public static ReferenceValue Null { get; } = new(0, null, 0);
}
