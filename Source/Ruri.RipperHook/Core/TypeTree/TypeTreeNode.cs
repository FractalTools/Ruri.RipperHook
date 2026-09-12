using System;
using System.Collections.Generic;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Tpk.Shared;
using AssetRipper.Tpk.TypeTrees;

namespace Ruri.RipperHook.Core.TypeTree;

public sealed class TypeTreeNode
{
    private static readonly TypeTreeNode[] NoSubNodes = Array.Empty<TypeTreeNode>();

    public string Name { get; }

    public string OriginalName { get; }

    /// <summary>
    /// The type name as Unity's own type trees spell it: the two string kinds this runtime tells
    /// apart are both a plain <c>string</c> there, and a consumer that reads through Unity's
    /// vocabulary (AssetRipper's node structs) must be handed that spelling back.
    /// </summary>
    public string UnityTypeName => TypeName is Utf8StringTypeName or PropertyNameTypeName ? "string" : TypeName;

    public string TypeName { get; }

    public int Version { get; }

    public TransferMetaFlags MetaFlag { get; }

    public TypeTreeNode[] SubNodes { get; }

    public TypeTreeNodeType NodeType { get; }

    public bool AlignBytes => MetaFlag.IsAlignBytes();

    /// <summary>
    /// A node stated directly, for a shape known only from the data in hand rather than from a
    /// packed schema: the same vocabulary a tpk node carries, so the interpreter reads it alike.
    /// </summary>
    public static TypeTreeNode Create(string typeName, string name, TransferMetaFlags metaFlag, params TypeTreeNode[] subNodes)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(subNodes);
        return new TypeTreeNode(typeName, name, 1, metaFlag, subNodes.Length == 0 ? NoSubNodes : subNodes);
    }

    private TypeTreeNode(string typeName, string originalName, int version, TransferMetaFlags metaFlag, TypeTreeNode[] subNodes)
    {
        TypeName = typeName;
        OriginalName = originalName;
        Name = TypeTreeNameFixer.GetValidFieldName(originalName);
        Version = version;
        MetaFlag = metaFlag;
        SubNodes = subNodes;
        NodeType = Classify(typeName, metaFlag, subNodes.Length);
    }

    public TypeTreeNode? TryGetSubNode(string name)
    {
        foreach (TypeTreeNode subNode in SubNodes)
        {
            if (subNode.Name == name || subNode.OriginalName == name)
            {
                return subNode;
            }
        }
        return null;
    }

    public override string ToString() => $"{TypeName} {OriginalName}";

    internal static TypeTreeNode FromTpk(TpkUnityNode node, TpkStringBuffer stringBuffer, TpkUnityNodeBuffer nodeBuffer)
    {
        TypeTreeNode[] subNodes;
        if (node.SubNodes.Length == 0)
        {
            subNodes = NoSubNodes;
        }
        else
        {
            subNodes = new TypeTreeNode[node.SubNodes.Length];
            for (int i = 0; i < node.SubNodes.Length; i++)
            {
                subNodes[i] = FromTpk(nodeBuffer[node.SubNodes[i]], stringBuffer, nodeBuffer);
            }
        }

        string typeName = GetFixedTypeName(stringBuffer[node.TypeName]);
        string originalName = stringBuffer[node.Name];
        TransferMetaFlags metaFlag = (TransferMetaFlags)node.MetaFlag;

        if (typeName == "string")
        {
            ApplyStringRenaming(subNodes, ref typeName, ref metaFlag);
        }

        return new TypeTreeNode(typeName, originalName, node.Version, metaFlag, subNodes);
    }

    private static void ApplyStringRenaming(TypeTreeNode[] subNodes, ref string typeName, ref TransferMetaFlags metaFlag)
    {
        if (subNodes.Length != 1)
        {
            throw new InvalidOperationException($"String has {subNodes.Length} subnodes");
        }

        TypeTreeNode subNode = subNodes[0];
        switch (subNode.TypeName)
        {
            case "Array":
                typeName = Utf8StringTypeName;
                if (subNode.AlignBytes)
                {
                    metaFlag |= TransferMetaFlags.AlignBytes;
                }
                break;
            case Utf8StringTypeName:
            case "SInt32":
                typeName = PropertyNameTypeName;
                break;
            default:
                throw new NotSupportedException($"String subnode has typename: {subNode.TypeName}");
        }
    }

    private static string GetFixedTypeName(string originalName) => originalName switch
    {
        "short" => "SInt16",
        "int" => "SInt32",
        "long long" => "SInt64",
        "unsigned short" => "UInt16",
        "unsigned int" => "UInt32",
        "unsigned long long" => "UInt64",
        _ => originalName,
    };

    private static TypeTreeNodeType Classify(string typeName, TransferMetaFlags metaFlag, int subNodeCount)
    {
        return subNodeCount == 0
            ? typeName switch
            {
                "bool" => TypeTreeNodeType.Boolean,
                "char" => TypeTreeNodeType.UInt8,
                "SInt8" => TypeTreeNodeType.Int8,
                "UInt8" => TypeTreeNodeType.UInt8,
                "short" or "SInt16" => TypeTreeNodeType.Int16,
                "ushort" or "UInt16" or "unsigned short" => metaFlag.IsCharPropertyMask() ? TypeTreeNodeType.Character : TypeTreeNodeType.UInt16,
                "int" or "SInt32" or "Type*" or "EntityId" => TypeTreeNodeType.Int32,
                "uint" or "UInt32" or "unsigned int" => TypeTreeNodeType.UInt32,
                "SInt64" or "long long" => TypeTreeNodeType.Int64,
                "UInt64" or "FileSize" or "unsigned long long" => TypeTreeNodeType.UInt64,
                "float" => TypeTreeNodeType.Single,
                "double" => TypeTreeNodeType.Double,
                _ => TypeTreeNodeType.Type,
            }
            : typeName switch
            {
                "Array" => TypeTreeNodeType.Array,
                "vector" or "staticvector" or "set" => TypeTreeNodeType.Vector,
                "map" => TypeTreeNodeType.Map,
                "pair" => TypeTreeNodeType.Pair,
                "TypelessData" => TypeTreeNodeType.TypelessData,
                "string" or Utf8StringTypeName => TypeTreeNodeType.String,
                _ => TypeTreeNodeType.Type,
            };
    }

    private const string Utf8StringTypeName = "Utf8String";
    private const string PropertyNameTypeName = "PropertyName";
}
