using System;
using System.Collections.Generic;
using System.Reflection;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.Core.TypeTree;

public delegate T TypeTreePrimitiveReader<T>(ref EndianSpanReader reader);

public static class TypeTreePrimitives
{
    private static readonly Dictionary<TypeTreeNodeType, Type> ClrTypes = new()
    {
        { TypeTreeNodeType.Boolean, typeof(bool) },
        { TypeTreeNodeType.Character, typeof(char) },
        { TypeTreeNodeType.Int8, typeof(sbyte) },
        { TypeTreeNodeType.UInt8, typeof(byte) },
        { TypeTreeNodeType.Int16, typeof(short) },
        { TypeTreeNodeType.UInt16, typeof(ushort) },
        { TypeTreeNodeType.Int32, typeof(int) },
        { TypeTreeNodeType.UInt32, typeof(uint) },
        { TypeTreeNodeType.Int64, typeof(long) },
        { TypeTreeNodeType.UInt64, typeof(ulong) },
        { TypeTreeNodeType.Single, typeof(float) },
        { TypeTreeNodeType.Double, typeof(double) },
        { TypeTreeNodeType.String, typeof(Utf8String) },
    };

    private static readonly Dictionary<TypeTreeNodeType, string> ReaderMethods = new()
    {
        { TypeTreeNodeType.Boolean, nameof(EndianSpanReader.ReadBoolean) },
        { TypeTreeNodeType.Character, nameof(EndianSpanReader.ReadChar) },
        { TypeTreeNodeType.Int8, nameof(EndianSpanReader.ReadSByte) },
        { TypeTreeNodeType.UInt8, nameof(EndianSpanReader.ReadByte) },
        { TypeTreeNodeType.Int16, nameof(EndianSpanReader.ReadInt16) },
        { TypeTreeNodeType.UInt16, nameof(EndianSpanReader.ReadUInt16) },
        { TypeTreeNodeType.Int32, nameof(EndianSpanReader.ReadInt32) },
        { TypeTreeNodeType.UInt32, nameof(EndianSpanReader.ReadUInt32) },
        { TypeTreeNodeType.Int64, nameof(EndianSpanReader.ReadInt64) },
        { TypeTreeNodeType.UInt64, nameof(EndianSpanReader.ReadUInt64) },
        { TypeTreeNodeType.Single, nameof(EndianSpanReader.ReadSingle) },
        { TypeTreeNodeType.Double, nameof(EndianSpanReader.ReadDouble) },
        { TypeTreeNodeType.String, nameof(EndianSpanReader.ReadUtf8String) },
    };

    public static bool IsPrimitive(TypeTreeNodeType nodeType) => ClrTypes.ContainsKey(nodeType);

    public static Type GetClrType(TypeTreeNodeType nodeType) => ClrTypes.TryGetValue(nodeType, out Type? type)
        ? type
        : throw new NotSupportedException($"[TypeTree] {nodeType} is not a primitive node type.");

    public static TypeTreePrimitiveReader<T> GetReader<T>(TypeTreeNodeType nodeType)
    {
        if (GetClrType(nodeType) != typeof(T))
        {
            throw new ArgumentException($"[TypeTree] {nodeType} reads {GetClrType(nodeType).Name}, not {typeof(T).Name}.", nameof(T));
        }

        MethodInfo method = typeof(EndianSpanReader).GetMethod(ReaderMethods[nodeType], BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(EndianSpanReader), ReaderMethods[nodeType]);

        return method.CreateDelegate<TypeTreePrimitiveReader<T>>();
    }

    public static Delegate GetReader(TypeTreeNodeType nodeType)
    {
        Type clrType = GetClrType(nodeType);
        MethodInfo method = typeof(EndianSpanReader).GetMethod(ReaderMethods[nodeType], BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(EndianSpanReader), ReaderMethods[nodeType]);
        return method.CreateDelegate(typeof(TypeTreePrimitiveReader<>).MakeGenericType(clrType));
    }

    public static TypeTreePrimitiveReader<byte[]> GetByteArrayReader() =>
        (TypeTreePrimitiveReader<byte[]>)typeof(TypeTreePrimitives)
            .GetMethod(nameof(ReadByteArray), BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(TypeTreePrimitiveReader<byte[]>));

    public static byte[] ReadByteArray(ref EndianSpanReader reader)
    {
        int count = reader.ReadInt32();
        return reader.ReadBytesExact(count).ToArray();
    }
}
