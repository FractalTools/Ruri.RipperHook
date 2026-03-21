using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using System.Numerics;
using Ruri.RipperHook.Crypto;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    public static void CustomReadHeader(FileStreamBundleHeader _this, EndianReader reader)
    {
        _this.Version = BundleVersion.BF_LargeFilesSupport;
        _this.UnityWebBundleVersion = "5.x.x";
        _this.UnityWebMinimumRevision = "2021.3.34f5";

        var originalEndian = reader.EndianType;
        reader.EndianType = EndianType.BigEndian;

        // 1. Magic Check
        var a = reader.ReadUInt32();
        var b = reader.ReadUInt32();

        // 2. Read Values (Obfuscated) - Release field order
        uint compressedBlocksInfoSize2 = reader.ReadUInt16();
        var flags2 = reader.ReadUInt32();
        var encFlagsRaw = reader.ReadUInt32();
        ulong size2 = reader.ReadUInt32();
        var flags1 = reader.ReadUInt32();
        uint uncompressedBlocksInfoSize1 = reader.ReadUInt16();
        reader.ReadUInt32(); // unknown
        uint uncompressedBlocksInfoSize2 = reader.ReadUInt16();
        ulong size1 = reader.ReadUInt32();
        uint compressedBlocksInfoSize1 = reader.ReadUInt16();
        reader.ReadByte(); // unknownByte

        // 3. Descramble - Release constants
        uint compressedBlocksInfoSize = VFSDecryptor.BitConcat(16, compressedBlocksInfoSize1 ^ compressedBlocksInfoSize2 ^ 0xA121, compressedBlocksInfoSize2);
        compressedBlocksInfoSize = BitOperations.RotateRight(compressedBlocksInfoSize, 18) ^ 0xF74324EE;

        uint uncompressedBlocksInfoSize = VFSDecryptor.BitConcat(16, uncompressedBlocksInfoSize1 ^ uncompressedBlocksInfoSize2 ^ 0xA121, uncompressedBlocksInfoSize2);
        uncompressedBlocksInfoSize = BitOperations.RotateRight(uncompressedBlocksInfoSize, 18) ^ 0xF74324EE;

        ulong size = VFSDecryptor.BitConcat64(32, size1 ^ size2 ^ 0xDAD76848, size2);
        size = BitOperations.RotateRight(size, 18) ^ 0xA4F1A11747816520UL;

        var flags = flags1 ^ flags2 ^ 0xA7F49310;

        var encFlags = encFlagsRaw ^ flags2;

        // 4. Set Fields
        _this.CompressedBlocksInfoSize = (int)compressedBlocksInfoSize;
        _this.UncompressedBlocksInfoSize = (int)uncompressedBlocksInfoSize;
        _this.Size = (long)(uint)size;
        _this.Flags = (BundleFlags)flags;

        // Release variant: no explicit padding after header.
        // Alignment to 16 bytes is handled by ReadFileStreamMetadata (version >= 7).
        // Header = 41 bytes → align to 48, which matches the actual data layout.

        reader.EndianType = originalEndian;
    }
}
