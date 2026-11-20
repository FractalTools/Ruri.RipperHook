using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Tpk.TypeTrees;
using Ruri.RipperHook.Core.TypeTree;
using System.Reflection;

namespace Ruri.Tpk.RuriEngine;

internal sealed class EngineTypeTreeBuilder
{
    private readonly TypeTreeBlobWriter writer = new();
    private readonly Dictionary<Type, bool> referenceMemo = new();

    public IReadOnlyList<TpkTypeTreeBlob> Blobs => writer.Blobs;

    public int ClassCount { get; private set; }

    public List<string> Failures { get; } = new();

    public Dictionary<string, (int Count, string Example)> Unsupported { get; } = new(StringComparer.Ordinal);

    public void Build(IEnumerable<Type> candidates)
    {
        List<Type> ordered = candidates.OrderBy(static type => type.FullName, StringComparer.Ordinal).ToList();
        int id = 1;
        foreach (Type type in ordered)
        {
            writer.BeginClass();
            try
            {
                ushort root = ClassRoot(type);
                string baseName = type.BaseType is { } baseType && baseType.FullName is { } baseFullName
                    && baseFullName is not ("System.Object" or "System.ValueType")
                    ? baseFullName
                    : string.Empty;
                writer.AddClass(id, type.FullName!, baseName, root);
                id++;
            }
            catch (Exception exception)
            {
                Failures.Add($"{type.FullName}: {exception.GetType().Name}: {exception.Message}");
            }
        }
        writer.Finish();
        ClassCount = id - 1;
    }

    private ushort ClassRoot(Type type)
    {
        HashSet<Type> path = [type];
        List<ushort> children = new();
        foreach (FieldInfo field in EngineSerializationRules.SerializedFields(type))
        {
            ushort? node = FieldNode(field.Name, field.FieldType, EngineSerializationRules.IsManagedReference(field), path, 1);
            if (node is not null)
            {
                children.Add(node.Value);
            }
            else
            {
                RecordUnsupported(type, field);
            }
        }
        if (ContainsManagedReference(type))
        {
            children.Add(RegistryNode());
        }
        return writer.Node(type.FullName!, "Base", 1, TransferMetaFlags.NoTransferFlags, children);
    }

    private ushort? FieldNode(string name, Type type, bool managed, HashSet<Type> path, int depth)
    {
        return managed ? ManagedNode(name, type) : ValueNode(name, type, path, depth, allowCollection: true);
    }

    private ushort? ValueNode(string name, Type type, HashSet<Type> path, int depth, bool allowCollection)
    {
        if (EngineSerializationRules.PrimitiveNode(type, out TransferMetaFlags flags) is { } primitive)
        {
            return Leaf(primitive, name, flags);
        }
        if (EngineSerializationRules.IsText(type))
        {
            return Text(name);
        }
        if (type.IsEnum)
        {
            return ValueNode(name, type.GetEnumUnderlyingType(), path, depth, allowCollection: false);
        }
        if (allowCollection && EngineSerializationRules.TryCollectionElement(type, out Type element))
        {
            ushort? child = ValueNode("data", element, path, depth + 1, allowCollection: false);
            return child is null ? null : Vector(name, child.Value);
        }
        if (EngineSerializationRules.TryAssetReferenceTarget(type, out string referenceTarget))
        {
            return Pointer("PPtr<$" + referenceTarget + ">", name);
        }
        if (EngineSerializationRules.IsInlineStructure(type))
        {
            return StructureNode(name, type, path, depth);
        }
        if (EngineSerializationRules.IsAssetReference(type))
        {
            return Pointer("PPtr<$" + EngineSerializationRules.DisplayName(type) + ">", name);
        }
        return null;
    }

    private ushort? StructureNode(string name, Type type, HashSet<Type> path, int depth)
    {
        if (depth >= EngineSerializationRules.NestingLimit || !path.Add(type))
        {
            return null;
        }
        try
        {
            List<ushort> children = new();
            foreach (FieldInfo field in EngineSerializationRules.SerializedFields(type))
            {
                ushort? node = FieldNode(field.Name, field.FieldType, EngineSerializationRules.IsManagedReference(field), path, depth + 1);
                if (node is not null)
                {
                    children.Add(node.Value);
                }
                else
                {
                    RecordUnsupported(type, field);
                }
            }
            return writer.Node(EngineSerializationRules.DisplayName(type), name, 1, TransferMetaFlags.NoTransferFlags, children);
        }
        finally
        {
            path.Remove(type);
        }
    }

    private void RecordUnsupported(Type owner, FieldInfo field)
    {
        string key = EngineSerializationRules.DisplayName(field.FieldType);
        Unsupported[key] = Unsupported.TryGetValue(key, out (int Count, string Example) known)
            ? (known.Count + 1, known.Example)
            : (1, $"{owner.FullName}.{field.Name}");
    }

    private ushort? ManagedNode(string name, Type type)
    {
        ushort rid = Leaf("SInt64", "rid", TransferMetaFlags.NoTransferFlags);
        if (EngineSerializationRules.TryCollectionElement(type, out _))
        {
            return Vector(name, writer.Node("managedRefArrayItem", "data", 1, TransferMetaFlags.NoTransferFlags, [rid]));
        }
        return writer.Node("managedReference", name, 1, TransferMetaFlags.NoTransferFlags, [rid]);
    }

    private bool ContainsManagedReference(Type type)
    {
        if (!referenceMemo.TryGetValue(type, out bool contains))
        {
            contains = ContainsManagedReference(type, new HashSet<Type>());
            referenceMemo[type] = contains;
        }
        return contains;
    }

    private static bool ContainsManagedReference(Type type, HashSet<Type> path)
    {
        if (!path.Add(type))
        {
            return false;
        }
        try
        {
            foreach (FieldInfo field in EngineSerializationRules.SerializedFields(type))
            {
                if (EngineSerializationRules.IsManagedReference(field))
                {
                    return true;
                }
                Type fieldType = EngineSerializationRules.TryCollectionElement(field.FieldType, out Type element) ? element : field.FieldType;
                if (EngineSerializationRules.IsInlineStructure(fieldType) && ContainsManagedReference(fieldType, path))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            path.Remove(type);
        }
    }

    private ushort RegistryNode()
    {
        ushort version = Leaf("SInt32", "version", TransferMetaFlags.NoTransferFlags);
        ushort rid = Leaf("SInt64", "rid", TransferMetaFlags.NoTransferFlags);
        ushort managedType = writer.Node("ReferencedManagedType", "type", 1, TransferMetaFlags.NoTransferFlags, [Text("class"), Text("ns"), Text("asm")]);
        ushort data = writer.Node("ReferencedObjectData", "data", 1, TransferMetaFlags.NoTransferFlags, []);
        ushort referencedObject = writer.Node("ReferencedObject", "data", 1, TransferMetaFlags.NoTransferFlags, [rid, managedType, data]);
        return writer.Node("ManagedReferencesRegistry", "references", 1, TransferMetaFlags.NoTransferFlags, [version, Vector("RefIds", referencedObject)]);
    }

    private ushort Leaf(string typeName, string name, TransferMetaFlags flags)
        => writer.Node(typeName, name, 1, flags, []);

    private ushort Text(string name)
    {
        ushort character = Leaf("char", "data", TransferMetaFlags.NoTransferFlags);
        ushort size = Leaf("SInt32", "size", TransferMetaFlags.NoTransferFlags);
        ushort array = writer.Node("Array", "Array", 1, TransferMetaFlags.AlignBytes, [size, character]);
        return writer.Node("string", name, 1, TransferMetaFlags.NoTransferFlags, [array]);
    }

    private ushort Pointer(string typeName, string name)
    {
        ushort fileId = Leaf("SInt32", "m_FileID", TransferMetaFlags.NoTransferFlags);
        ushort pathId = Leaf("SInt64", "m_PathID", TransferMetaFlags.NoTransferFlags);
        return writer.Node(typeName, name, 1, TransferMetaFlags.NoTransferFlags, [fileId, pathId]);
    }

    private ushort Vector(string name, ushort element)
    {
        ushort size = Leaf("SInt32", "size", TransferMetaFlags.NoTransferFlags);
        ushort array = writer.Node("Array", "Array", 1, TransferMetaFlags.AlignBytes, [size, element]);
        return writer.Node("vector", name, 1, TransferMetaFlags.NoTransferFlags, [array]);
    }
}
