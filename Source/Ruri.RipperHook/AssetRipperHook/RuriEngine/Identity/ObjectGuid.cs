using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Ruri.RipperHook.RuriEngine;

internal enum ObjectDomain : byte
{
    ForeignSubObject = 0,
    DerivedProduct = 1,
    VfsPathAlias = 2,
    UnityFileId = 3,
}

internal static class ObjectGuid
{
    private const int GuidBytes = 16;
    private const int DomainBytes = 1;
    private const int VersionByte = 6;
    private const int SubdomainByte = 7;
    private const int VariantByte = 8;
    private const byte VersionMask = 0x0F;
    private const byte VersionEight = 0x80;
    private const byte SubdomainMask = 0b11;
    private const byte VariantMask = 0x3F;
    private const byte VariantRfc = 0x80;
    private const long MainFileIdStride = 100000;

    public static Guid Derive(ObjectDomain domain, Guid parent, string key)
    {
        byte[] input = new byte[DomainBytes + GuidBytes + Encoding.UTF8.GetByteCount(key)];
        input[0] = (byte)domain;
        parent.TryWriteBytes(input.AsSpan(DomainBytes, GuidBytes), true, out _);
        Encoding.UTF8.GetBytes(key, input.AsSpan(DomainBytes + GuidBytes));
        Span<byte> bytes = stackalloc byte[GuidBytes];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, XxHash128.HashToUInt128(input));
        bytes[VersionByte] = (byte)((bytes[VersionByte] & VersionMask) | VersionEight);
        bytes[SubdomainByte] = (byte)((bytes[SubdomainByte] & ~SubdomainMask) | (byte)domain);
        bytes[VariantByte] = (byte)((bytes[VariantByte] & VariantMask) | VariantRfc);
        return new Guid(bytes, true);
    }

    public static Guid OfUnityObject(Guid fileGuid, long fileId, bool isMain)
    {
        return isMain ? fileGuid : Derive(ObjectDomain.UnityFileId, fileGuid, fileId.ToString(CultureInfo.InvariantCulture));
    }

    public static bool IsMainByStride(long fileId) => fileId % MainFileIdStride == 0;
}
