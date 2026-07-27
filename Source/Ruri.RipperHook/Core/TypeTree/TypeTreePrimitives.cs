using System;
using System.Collections.Generic;
using System.Reflection;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>Open-instance reader over <see cref="EndianSpanReader"/>, e.g. <c>ReadSingle</c>.</summary>
public delegate T TypeTreePrimitiveReader<T>(ref EndianSpanReader reader);

/// <summary>
/// Maps a <see cref="TypeTreeNodeType"/> onto the CLR type and <see cref="EndianSpanReader"/> method
/// the assembly dumper would have emitted for it (<c>Pass100_FillReadMethods.GetPrimitiveMethod</c> +
/// <c>NodeTypeExtensions.ToPrimitiveTypeName</c>). Delegates are open-instance, so calling one costs
/// a delegate invoke and no boxing.
/// </summary>
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

    /// <summary>
    /// Creates the open-instance reader for <paramref name="nodeType"/>. <typeparamref name="T"/> must
    /// be <see cref="GetClrType"/> for that node type.
    /// </summary>
    public static TypeTreePrimitiveReader<T> GetReader<T>(TypeTreeNodeType nodeType)
    {
        if (GetClrType(nodeType) != typeof(T))
        {
            throw new ArgumentException($"[TypeTree] {nodeType} reads {GetClrType(nodeType).Name}, not {typeof(T).Name}.", nameof(T));
        }

        MethodInfo method = typeof(EndianSpanReader).GetMethod(ReaderMethods[nodeType], BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(EndianSpanReader), ReaderMethods[nodeType]);

        // An open-instance delegate over a struct method takes the receiver by reference, which is
        // exactly the `ref EndianSpanReader` first parameter of TypeTreePrimitiveReader<T>.
        return method.CreateDelegate<TypeTreePrimitiveReader<T>>();
    }

    /// <summary>
    /// Same as <see cref="GetReader{T}"/> when the CLR type is only known at plan-build time. Returns a
    /// <c>TypeTreePrimitiveReader&lt;clrType&gt;</c>.
    /// </summary>
    public static Delegate GetReader(TypeTreeNodeType nodeType)
    {
        Type clrType = GetClrType(nodeType);
        MethodInfo method = typeof(EndianSpanReader).GetMethod(ReaderMethods[nodeType], BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(EndianSpanReader), ReaderMethods[nodeType]);
        return method.CreateDelegate(typeof(TypeTreePrimitiveReader<>).MakeGenericType(clrType));
    }

    /// <summary>The <see cref="ReadByteArray"/> reader, typed as <c>TypeTreePrimitiveReader&lt;byte[]&gt;</c>.</summary>
    public static TypeTreePrimitiveReader<byte[]> GetByteArrayReader() =>
        (TypeTreePrimitiveReader<byte[]>)typeof(TypeTreePrimitives)
            .GetMethod(nameof(ReadByteArray), BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(TypeTreePrimitiveReader<byte[]>));

    /// <summary>
    /// The TypelessData / byte-array read: an int32 count followed by that many raw bytes. 1:1 port of
    /// <c>Pass100_FillReadMethods.MakeTypelessDataMethod</c> + <c>TypelessDataHelper.ReadByteArray</c>
    /// (the align, when the node asks for it, is applied by the calling step).
    /// </summary>
    public static byte[] ReadByteArray(ref EndianSpanReader reader)
    {
        int count = reader.ReadInt32();
        return reader.ReadBytesExact(count).ToArray();
    }
}
