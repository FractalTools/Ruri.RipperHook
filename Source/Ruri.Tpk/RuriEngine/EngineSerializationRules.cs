using AssetRipper.IO.Files.SerializedFiles;
using System.Reflection;
using System.Text;

namespace Ruri.Tpk.RuriEngine;

internal static class EngineSerializationRules
{
    public const string SerializeFieldAttribute = "Ruri.Engine.Serialization.SerializeField";
    public const string SerializeReferenceAttribute = "Ruri.Engine.Serialization.SerializeReference";
    public const int NestingLimit = 10;

    private const string ListDefinition = "System.Collections.Generic.List`1";
    private const string TypedAssetReferenceDefinition = "Ruri.Engine.AssetTables.AssetRef`1";
    private const string UntypedAssetReference = "Ruri.Engine.AssetTables.AssetReference";

    public static IReadOnlyList<FieldInfo> SerializedFields(Type type)
    {
        List<Type> chain = new();
        for (Type? cursor = type; cursor is not null && !IsRoot(cursor); cursor = cursor.BaseType)
        {
            chain.Add(cursor);
        }
        chain.Reverse();

        List<FieldInfo> fields = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Type owner in chain)
        {
            foreach (FieldInfo field in owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (IsSerialized(field) && names.Add(field.Name))
                {
                    fields.Add(field);
                }
            }
        }
        return fields;
    }

    public static bool IsManagedReference(FieldInfo field) => HasAttribute(field, SerializeReferenceAttribute);

    public static bool IsClassCandidate(Type type)
    {
        if (type.IsAbstract || type.IsInterface || type.IsEnum || type.IsGenericTypeDefinition || type.ContainsGenericParameters)
        {
            return false;
        }
        if (type.Name.Contains('<') || IsDelegate(type))
        {
            return false;
        }
        return type.IsClass || type.IsValueType;
    }

    public static bool IsPublicType(Type type) => type.IsPublic || type.IsNestedPublic;

    public static string? PrimitiveNode(Type type, out TransferMetaFlags flags)
    {
        flags = TransferMetaFlags.NoTransferFlags;
        switch (type.FullName)
        {
            case "System.Boolean":
                return "bool";
            case "System.SByte":
                return "SInt8";
            case "System.Byte":
                return "UInt8";
            case "System.Int16":
                return "SInt16";
            case "System.UInt16":
                return "UInt16";
            case "System.Char":
                flags = TransferMetaFlags.CharPropertyMask;
                return "UInt16";
            case "System.Int32":
                return "SInt32";
            case "System.UInt32":
                return "UInt32";
            case "System.Int64":
                return "SInt64";
            case "System.UInt64":
                return "UInt64";
            case "System.Single":
                return "float";
            case "System.Double":
                return "double";
            default:
                return null;
        }
    }

    public static bool IsText(Type type) => type.FullName == "System.String";

    public static bool TryCollectionElement(Type type, out Type element)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            element = type.GetElementType()!;
            return true;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == ListDefinition)
        {
            element = type.GetGenericArguments()[0];
            return true;
        }
        element = type;
        return false;
    }

    public static bool TryAssetReferenceTarget(Type type, out string target)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == TypedAssetReferenceDefinition)
        {
            target = DisplayName(type.GetGenericArguments()[0]);
            return true;
        }
        if (type.FullName == UntypedAssetReference)
        {
            target = UntypedAssetReference;
            return true;
        }
        target = string.Empty;
        return false;
    }

    public static bool IsInlineStructure(Type type)
    {
        if (!IsStructureShape(type))
        {
            return false;
        }
        return type.IsValueType || (type.Attributes & TypeAttributes.Serializable) != 0;
    }

    public static bool IsAssetReference(Type type) =>
        IsStructureShape(type) && !type.IsValueType && (type.Attributes & TypeAttributes.Serializable) == 0;

    public static string DisplayName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName!;
        }
        StringBuilder name = new();
        string definition = type.GetGenericTypeDefinition().FullName!;
        for (int index = 0; index < definition.Length; index++)
        {
            if (definition[index] != '`')
            {
                name.Append(definition[index]);
                continue;
            }
            while (index + 1 < definition.Length && char.IsAsciiDigit(definition[index + 1]))
            {
                index++;
            }
        }
        name.Append('<').Append(string.Join(", ", type.GetGenericArguments().Select(DisplayName))).Append('>');
        return name.ToString();
    }

    private static bool IsStructureShape(Type type)
    {
        if (type.IsEnum || type.IsPrimitive || type.IsPointer || type.IsByRef || type.IsGenericParameter
            || type.ContainsGenericParameters || type.IsArray || type.IsInterface)
        {
            return false;
        }
        return !IsFrameworkNamespace(type.Namespace) && !IsDelegate(type);
    }

    private static bool IsFrameworkNamespace(string? @namespace)
    {
        if (@namespace is null)
        {
            return false;
        }
        if (@namespace.StartsWith("System.Numerics", StringComparison.Ordinal))
        {
            return false;
        }
        return @namespace == "System"
            || @namespace.StartsWith("System.", StringComparison.Ordinal)
            || @namespace.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static bool IsSerialized(FieldInfo field)
    {
        if (field.IsStatic || field.IsLiteral || field.IsInitOnly || (field.Attributes & FieldAttributes.NotSerialized) != 0)
        {
            return false;
        }
        return field.IsPublic || IsManagedReference(field) || HasAttribute(field, SerializeFieldAttribute);
    }

    private static bool HasAttribute(MemberInfo member, string fullName)
    {
        foreach (CustomAttributeData data in member.GetCustomAttributesData())
        {
            if (data.AttributeType.FullName == fullName)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRoot(Type type) => type.FullName is "System.Object" or "System.ValueType" or "System.Enum";

    private static bool IsDelegate(Type type)
    {
        for (Type? cursor = type.BaseType; cursor is not null; cursor = cursor.BaseType)
        {
            if (cursor.FullName == "System.MulticastDelegate")
            {
                return true;
            }
        }
        return false;
    }
}
