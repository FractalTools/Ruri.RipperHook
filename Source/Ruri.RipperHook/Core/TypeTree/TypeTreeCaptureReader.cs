using System;
using System.Collections.Generic;
using AssetRipper.IO.Endian;

namespace Ruri.RipperHook.Core.TypeTree;

internal static class TypeTreeCaptureReader
{
    public static TypeTreeValue Read(TypeTreeNode node, ref EndianSpanReader reader)
    {
        switch (node.NodeType)
        {
            case TypeTreeNodeType.Vector:
            {
                TypeTreeNode arrayNode = node.SubNodes[0];
                TypeTreeNode elementNode = arrayNode.SubNodes[1];
                return ReadSequence(node, elementNode, node.AlignBytes || arrayNode.AlignBytes, ref reader);
            }
            case TypeTreeNodeType.Array:
                return ReadSequence(node, node.SubNodes[1], node.AlignBytes, ref reader);

            case TypeTreeNodeType.Map:
            {
                TypeTreeNode arrayNode = node.SubNodes[0];
                TypeTreeNode pairNode = arrayNode.SubNodes[1];
                return ReadSequence(node, pairNode, node.AlignBytes || arrayNode.AlignBytes, ref reader);
            }
            case TypeTreeNodeType.TypelessData:
            {
                byte[] bytes = TypeTreePrimitives.ReadByteArray(ref reader);
                AlignIf(node.AlignBytes, ref reader);
                return TypeTreeValue.Scalar(node, bytes);
            }
            case TypeTreeNodeType.Pair:
            case TypeTreeNodeType.Type:
            {
                Dictionary<string, TypeTreeValue> members = new(node.SubNodes.Length, StringComparer.Ordinal);
                foreach (TypeTreeNode subNode in node.SubNodes)
                {
                    members[subNode.Name] = Read(subNode, ref reader);
                }
                AlignIf(node.AlignBytes, ref reader);
                return TypeTreeValue.Structure(node, members);
            }
            default:
            {
                object value = ReadPrimitive(node.NodeType, ref reader);
                AlignIf(node.AlignBytes, ref reader);
                return TypeTreeValue.Scalar(node, value);
            }
        }
    }

    public static void Skip(TypeTreeNode node, ref EndianSpanReader reader)
    {
        switch (node.NodeType)
        {
            case TypeTreeNodeType.Vector:
            {
                TypeTreeNode arrayNode = node.SubNodes[0];
                SkipSequence(arrayNode.SubNodes[1], node.AlignBytes || arrayNode.AlignBytes, ref reader);
                break;
            }
            case TypeTreeNodeType.Array:
                SkipSequence(node.SubNodes[1], node.AlignBytes, ref reader);
                break;

            case TypeTreeNodeType.Map:
            {
                TypeTreeNode arrayNode = node.SubNodes[0];
                SkipSequence(arrayNode.SubNodes[1], node.AlignBytes || arrayNode.AlignBytes, ref reader);
                break;
            }
            case TypeTreeNodeType.TypelessData:
            {
                SkipBytes(ref reader);
                AlignIf(node.AlignBytes, ref reader);
                break;
            }
            case TypeTreeNodeType.Pair:
            case TypeTreeNodeType.Type:
            {
                foreach (TypeTreeNode subNode in node.SubNodes)
                {
                    Skip(subNode, ref reader);
                }
                AlignIf(node.AlignBytes, ref reader);
                break;
            }
            default:
                SkipPrimitive(node.NodeType, ref reader);
                AlignIf(node.AlignBytes, ref reader);
                break;
        }
    }

    private static TypeTreeValue ReadSequence(TypeTreeNode node, TypeTreeNode elementNode, bool align, ref EndianSpanReader reader)
    {
        if (IsBulkByteElement(elementNode))
        {
            byte[] bytes = TypeTreePrimitives.ReadByteArray(ref reader);
            AlignIf(align, ref reader);
            return TypeTreeValue.Scalar(node, bytes);
        }

        int count = reader.ReadInt32();
        List<TypeTreeValue> items = new(Math.Min(count, 1024));
        for (int i = 0; i < count; i++)
        {
            items.Add(Read(elementNode, ref reader));
        }
        AlignIf(align, ref reader);
        return TypeTreeValue.Sequence(node, items);
    }

    private static void SkipSequence(TypeTreeNode elementNode, bool align, ref EndianSpanReader reader)
    {
        if (IsBulkByteElement(elementNode))
        {
            SkipBytes(ref reader);
            AlignIf(align, ref reader);
            return;
        }

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            Skip(elementNode, ref reader);
        }
        AlignIf(align, ref reader);
    }

    private static bool IsBulkByteElement(TypeTreeNode elementNode) =>
        elementNode.NodeType is TypeTreeNodeType.UInt8 or TypeTreeNodeType.Int8 && !elementNode.AlignBytes;

    private static void SkipBytes(ref EndianSpanReader reader)
    {
        int count = reader.ReadInt32();
        reader.ReadBytesExact(count);
    }

    private static object ReadPrimitive(TypeTreeNodeType nodeType, ref EndianSpanReader reader) => nodeType switch
    {
        TypeTreeNodeType.Boolean => reader.ReadBoolean(),
        TypeTreeNodeType.Character => reader.ReadChar(),
        TypeTreeNodeType.Int8 => reader.ReadSByte(),
        TypeTreeNodeType.UInt8 => reader.ReadByte(),
        TypeTreeNodeType.Int16 => reader.ReadInt16(),
        TypeTreeNodeType.UInt16 => reader.ReadUInt16(),
        TypeTreeNodeType.Int32 => reader.ReadInt32(),
        TypeTreeNodeType.UInt32 => reader.ReadUInt32(),
        TypeTreeNodeType.Int64 => reader.ReadInt64(),
        TypeTreeNodeType.UInt64 => reader.ReadUInt64(),
        TypeTreeNodeType.Single => reader.ReadSingle(),
        TypeTreeNodeType.Double => reader.ReadDouble(),
        TypeTreeNodeType.String => reader.ReadUtf8String(),
        _ => throw new NotSupportedException($"[TypeTree] Cannot read node type {nodeType}."),
    };

    private static void SkipPrimitive(TypeTreeNodeType nodeType, ref EndianSpanReader reader)
    {
        switch (nodeType)
        {
            case TypeTreeNodeType.Boolean: reader.ReadBoolean(); break;
            case TypeTreeNodeType.Character: reader.ReadChar(); break;
            case TypeTreeNodeType.Int8: reader.ReadSByte(); break;
            case TypeTreeNodeType.UInt8: reader.ReadByte(); break;
            case TypeTreeNodeType.Int16: reader.ReadInt16(); break;
            case TypeTreeNodeType.UInt16: reader.ReadUInt16(); break;
            case TypeTreeNodeType.Int32: reader.ReadInt32(); break;
            case TypeTreeNodeType.UInt32: reader.ReadUInt32(); break;
            case TypeTreeNodeType.Int64: reader.ReadInt64(); break;
            case TypeTreeNodeType.UInt64: reader.ReadUInt64(); break;
            case TypeTreeNodeType.Single: reader.ReadSingle(); break;
            case TypeTreeNodeType.Double: reader.ReadDouble(); break;
            case TypeTreeNodeType.String: reader.ReadUtf8String(); break;
            default: throw new NotSupportedException($"[TypeTree] Cannot skip node type {nodeType}.");
        }
    }

    private static void AlignIf(bool align, ref EndianSpanReader reader)
    {
        if (align)
        {
            reader.Align();
        }
    }
}
