using AssetRipper.Import.Structure.Assembly.TypeTrees;

namespace Ruri.RipperHook.RuriEngine;

internal abstract class EngineShape
{
}

internal enum PrimitiveKind
{
    Boolean,
    SByte,
    Byte,
    Short,
    UShort,
    Char,
    Int,
    UInt,
    Long,
    ULong,
    Single,
    Double,
}

internal sealed class PrimitiveShape(PrimitiveKind kind) : EngineShape
{
    public PrimitiveKind Kind { get; } = kind;
}

internal sealed class TextShape : EngineShape
{
    public static TextShape Instance { get; } = new();

    private TextShape()
    {
    }
}

internal sealed class ManagedShape : EngineShape
{
    public static ManagedShape Instance { get; } = new();

    private ManagedShape()
    {
    }
}

internal sealed class ReferenceShape(string target) : EngineShape
{
    public string Target { get; } = target;
}

internal sealed class ArrayShape(EngineShape element) : EngineShape
{
    public EngineShape Element { get; } = element;
}

internal sealed record EngineField(string Name, EngineShape Shape);

internal sealed class StructShape : EngineShape
{
    private readonly Dictionary<string, EngineField> byName = new(StringComparer.Ordinal);

    public StructShape(string typeName, IReadOnlyList<EngineField> fields, bool hasRegistry)
    {
        TypeName = typeName;
        Fields = fields;
        HasRegistry = hasRegistry;
        foreach (EngineField field in fields)
        {
            byName.TryAdd(field.Name, field);
        }
    }

    public string TypeName { get; }

    public IReadOnlyList<EngineField> Fields { get; }

    public bool HasRegistry { get; }

    public bool TryGetField(string name, [NotNullWhen(true)] out EngineField? field) => byName.TryGetValue(name, out field);
}

internal static class EngineShapes
{
    private const string ReferencePrefix = "PPtr<$";

    public static StructShape OfClass(TypeTreeNodeStruct root)
    {
        List<EngineField> fields = new();
        bool hasRegistry = false;
        foreach (TypeTreeNodeStruct child in root.SubNodes)
        {
            if (child.IsManagedReferencesRegistry)
            {
                hasRegistry = true;
                continue;
            }
            fields.Add(new EngineField(child.Name, OfNode(child)));
        }
        return new StructShape(root.TypeName, fields, hasRegistry);
    }

    private static EngineShape OfNode(TypeTreeNodeStruct node)
    {
        if (node.IsManagedReference)
        {
            return ManagedShape.Instance;
        }
        if (node.IsString)
        {
            return TextShape.Instance;
        }
        if (node.IsArray)
        {
            return new ArrayShape(OfNode(node[1]));
        }
        if (node.IsVector || node.IsNamedVector)
        {
            return new ArrayShape(OfNode(node[0][1]));
        }
        if (node.IsPPtr)
        {
            return new ReferenceShape(ReferenceTarget(node.TypeName));
        }
        if (node.Count == 0 && PrimitiveOf(node) is { } kind)
        {
            return new PrimitiveShape(kind);
        }
        return OfClass(node);
    }

    private static string ReferenceTarget(string typeName)
    {
        return typeName.StartsWith(ReferencePrefix, StringComparison.Ordinal)
            ? typeName[ReferencePrefix.Length..^1]
            : typeName[5..^1];
    }

    private static PrimitiveKind? PrimitiveOf(TypeTreeNodeStruct node)
    {
        return node.TypeName switch
        {
            "bool" => PrimitiveKind.Boolean,
            "SInt8" => PrimitiveKind.SByte,
            "char" or "UInt8" => PrimitiveKind.Byte,
            "short" or "SInt16" => PrimitiveKind.Short,
            "ushort" or "UInt16" or "unsigned short" => node.TreatIntegerAsChar ? PrimitiveKind.Char : PrimitiveKind.UShort,
            "int" or "SInt32" or "Type*" => PrimitiveKind.Int,
            "uint" or "UInt32" or "unsigned int" => PrimitiveKind.UInt,
            "SInt64" or "long long" => PrimitiveKind.Long,
            "UInt64" or "FileSize" or "unsigned long long" => PrimitiveKind.ULong,
            "float" => PrimitiveKind.Single,
            "double" => PrimitiveKind.Double,
            _ => null,
        };
    }
}
