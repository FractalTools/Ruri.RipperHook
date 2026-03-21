using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using System.Numerics;
using Ruri.RipperHook.Crypto;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
{
    public static void CustomReadHeader(FileStreamBundleHeader _this, EndianReader reader)
    {
        // 设置版本以确保 AR 正确处理 64位 Size 字段
        _this.Version = BundleVersion.BF_LargeFilesSupport;
        _this.UnityWebBundleVersion = "5.x.x";
        _this.UnityWebMinimumRevision = "2021.3.34f5";

        var originalEndian = reader.EndianType;
        reader.EndianType = EndianType.BigEndian;

        // 1. Magic Check
        var a = reader.ReadUInt32();
        var b = reader.ReadUInt32();

        // 2. Read Values (Obfuscated)
        var flags1 = reader.ReadUInt32();
        uint uncompressedBlocksInfoSize1 = reader.ReadUInt16();
        uint compressedBlocksInfoSize1 = reader.ReadUInt16();
        reader.ReadUInt32(); // unknown
        uint uncompressedBlocksInfoSize2 = reader.ReadUInt16();

        var encFlagsRaw = reader.ReadUInt32(); // 用于判断是否需要跳过 Padding

        ulong size1 = reader.ReadUInt32();
        uint compressedBlocksInfoSize2 = reader.ReadUInt16();
        var flags2 = reader.ReadUInt32();
        ulong size2 = reader.ReadUInt32();

        // 3. Descramble
        uint compressedBlocksInfoSize = VFSDecryptor.BitConcat(16, compressedBlocksInfoSize1 ^ compressedBlocksInfoSize2 ^ 0xE114, compressedBlocksInfoSize2);
        compressedBlocksInfoSize = BitOperations.RotateLeft(compressedBlocksInfoSize, 3) ^ 0x5ADA4ABA;

        uint uncompressedBlocksInfoSize = VFSDecryptor.BitConcat(16, uncompressedBlocksInfoSize1 ^ uncompressedBlocksInfoSize2 ^ 0xE114, uncompressedBlocksInfoSize2);
        uncompressedBlocksInfoSize = BitOperations.RotateLeft(uncompressedBlocksInfoSize, 3) ^ 0x5ADA4ABA;

        ulong size = VFSDecryptor.BitConcat64(32, size1 ^ size2 ^ 0x342D983F, size2);
        size = (BitOperations.RotateLeft(size, 3)) ^ 0x5B4FA98A430D0E62UL;

        var flags = flags1 ^ flags2 ^ 0x98B806A4;

        // Critical Fix: encFlags logic matches AnimeStudio's VFSUtils.ReadHeader
        var encFlags = encFlagsRaw ^ flags2;

        // 4. Set Fields
        _this.CompressedBlocksInfoSize = (int)compressedBlocksInfoSize;
        _this.UncompressedBlocksInfoSize = (int)uncompressedBlocksInfoSize;
        _this.Size = (long)size;
        _this.Flags = (BundleFlags)flags;

        // 5. Handle Header Padding (Fix for offset mismatch)
        // AnimeStudio VFSFile.cs: if (m_Header.encFlags >= 7) blockInfosOffset = 48; else 40;
        // Current Read consumes 40 bytes (8+32). If flags >= 7, we need to consume 8 more.
        if (encFlags >= 7)
        {
            reader.ReadBytes(8);
        }

        //Logger.Info($"[EndField] Header Decrypted: Flags={_this.Flags}, Size={_this.Size}, CompressedMeta={_this.CompressedBlocksInfoSize}, EncFlags={encFlags}");

        reader.EndianType = originalEndian;
    }
}