using System;
using System.Collections.Generic;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// A value read straight out of the type tree with no AssetRipper field behind it.
///
/// Custom engines routinely add fields the stock AssetRipper classes have no home for -- EndField's
/// <c>m_CollisionMeshBaked</c>, <c>m_AclCompressedBuffer</c>, <c>m_TOSData</c>. The old pipeline
/// materialized them by generating a whole parallel class hierarchy; here they are captured into
/// this structural value on demand (only for nodes a hook actually declared interest in) and handed
/// to gates and post-read hooks.
/// </summary>
public sealed class TypeTreeValue
{
    private readonly object? scalar;
    private readonly List<TypeTreeValue>? elements;
    private readonly Dictionary<string, TypeTreeValue>? fields;

    public TypeTreeNode Node { get; }

    private TypeTreeValue(TypeTreeNode node, object? scalar, List<TypeTreeValue>? elements, Dictionary<string, TypeTreeValue>? fields)
    {
        Node = node;
        this.scalar = scalar;
        this.elements = elements;
        this.fields = fields;
    }

    internal static TypeTreeValue Scalar(TypeTreeNode node, object? value) => new(node, value, null, null);

    internal static TypeTreeValue Sequence(TypeTreeNode node, List<TypeTreeValue> items) => new(node, null, items, null);

    internal static TypeTreeValue Structure(TypeTreeNode node, Dictionary<string, TypeTreeValue> members) => new(node, null, null, members);

    public bool IsScalar => elements is null && fields is null;

    public IReadOnlyList<TypeTreeValue> Elements => elements ?? (IReadOnlyList<TypeTreeValue>)Array.Empty<TypeTreeValue>();

    public IReadOnlyDictionary<string, TypeTreeValue> Fields => fields ?? EmptyFields;

    private static readonly Dictionary<string, TypeTreeValue> EmptyFields = new();

    /// <summary>Member lookup by sanitized node name, e.g. <c>m_ACLArray</c>.</summary>
    public TypeTreeValue? this[string name] => fields is not null && fields.TryGetValue(name, out TypeTreeValue? value) ? value : null;

    public bool AsBoolean() => scalar is bool value ? value : throw Mismatch(nameof(Boolean));

    public byte AsByte() => scalar is byte value ? value : throw Mismatch(nameof(Byte));

    public int AsInt32() => scalar switch
    {
        int value => value,
        sbyte value => value,
        byte value => value,
        short value => value,
        ushort value => value,
        _ => throw Mismatch(nameof(Int32)),
    };

    public uint AsUInt32() => scalar switch
    {
        uint value => value,
        byte value => value,
        ushort value => value,
        _ => throw Mismatch(nameof(UInt32)),
    };

    public float AsSingle() => scalar switch
    {
        float value => value,
        double value => (float)value,
        _ => throw Mismatch(nameof(Single)),
    };

    public byte[] AsByteArray() => scalar as byte[] ?? throw Mismatch("Byte[]");

    public Utf8String AsUtf8String() => scalar as Utf8String ?? throw Mismatch(nameof(Utf8String));

    public object? RawValue => scalar;

    public override string ToString()
    {
        if (elements is not null) return $"{Node.TypeName} {Node.OriginalName}[{elements.Count}]";
        if (fields is not null) return $"{Node.TypeName} {Node.OriginalName}{{{fields.Count}}}";
        return $"{Node.TypeName} {Node.OriginalName} = {scalar ?? "null"}";
    }

    private InvalidOperationException Mismatch(string expected) =>
        new($"[TypeTree] Captured node '{Node.OriginalName}' ({Node.TypeName}) holds {scalar?.GetType().Name ?? "null"}, not {expected}.");
}
