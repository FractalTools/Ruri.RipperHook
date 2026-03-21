using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.Extensions;
using K4os.Compression.LZ4;
using Ruri.RipperHook.Crypto;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    public static void CustomReadFileStreamMetadata(FileStreamBundleFile _this, Stream stream, long basePosition)
    {
        var Header = _this.Header;

        if (Header.Version >= BundleVersion.BF_LargeFilesSupport) stream.Align(16);

        // Calculate Metadata Position
        if (Header.Flags.GetBlocksInfoAtTheEnd())
        {
            stream.Position = basePosition + (Header.Size - Header.CompressedBlocksInfoSize);
        }

        // Read Metadata
        var compressedSize = Header.CompressedBlocksInfoSize;
        var uncompressedSize = Header.UncompressedBlocksInfoSize;
        var compressedBytes = new BinaryReader(stream).ReadBytes(compressedSize);

        MemoryStream uncompressedStream;

        // Metadata Decryption
        if ((Header.Flags & BundleFlags.CompressionTypeMask) != 0)
        {
            vfsDecryptor.Decrypt(compressedBytes);

            var uncompressedBytes = new byte[uncompressedSize];
            var bytesWritten = LZ4Codec.Decode(compressedBytes, uncompressedBytes);

            if (bytesWritten != uncompressedSize)
            {
                AssetRipper.Import.Logging.Logger.Warning($"[EndField 1.1.9] Metadata decompression mismatch. Expected {uncompressedSize}, got {bytesWritten}");
            }

            uncompressedStream = new MemoryStream(uncompressedBytes);
        }
        else
        {
            uncompressedStream = new MemoryStream(compressedBytes);
        }

        using (var reader = new EndianReader(uncompressedStream, EndianType.BigEndian))
        {
            var blocksInfo = ReadObfuscatedBlocksInfo(reader);
            SetPrivateProperty(_this, "BlocksInfo", blocksInfo);

            var directoryInfo = ReadObfuscatedDirectoryInfo(reader);
            _this.DirectoryInfo = directoryInfo;
        }

        if ((Header.Flags & BundleFlags.BlockInfoNeedPaddingAtStart) != 0)
        {
            stream.Align(16);
        }
    }

    private static BlocksInfo ReadObfuscatedBlocksInfo(EndianReader reader)
    {
        var originalEndian = reader.EndianType;

        // Release: read encCount in LittleEndian, then ReverseEndianness
        reader.EndianType = EndianType.LittleEndian;
        var encCount = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32() ^ 0x8A7BF723);
        reader.EndianType = originalEndian;

        var low = encCount & 0xFFFF;
        var high = (encCount >> 16) & 0xFFFF;
        var blocksCount = VFSDecryptor.BitConcat(16, low ^ high, low);
        blocksCount = BitOperations.RotateRight(blocksCount, 18) ^ 0x91CE0A4F;

        var storageBlocks = new StorageBlock[blocksCount];
        for (int i = 0; i < blocksCount; i++)
        {
            // Release block entry order: a, b, c, encFlags, d
            var a = reader.ReadUInt16();
            var b = reader.ReadUInt16();
            var c = reader.ReadUInt16();
            ushort encFlags = (ushort)(reader.ReadUInt16() ^ 0x9CD6);
            var d = reader.ReadUInt16();

            uint a0 = (ushort)(encFlags & 0xFF);
            uint a1 = (ushort)((encFlags >> 8) & 0xFF);
            ushort flags = (ushort)VFSDecryptor.BitConcat(8, a0 ^ a1, a0);
            flags = (ushort)(c ^ VFSDecryptor.RotateLeft16(flags, 14) ^ 0x523F);

            var uncompressedSize = VFSDecryptor.BitConcat(16, (uint)(a ^ c ^ 0xA121), (uint)c);
            uncompressedSize = BitOperations.RotateRight(uncompressedSize, 18) ^ 0xF74324EE;

            var compressedSize = VFSDecryptor.BitConcat(16, (uint)(b ^ d ^ 0xA121), (uint)d);
            compressedSize = BitOperations.RotateRight(compressedSize, 18) ^ 0xF74324EE;

            var block = new StorageBlock();
            SetPrivateProperty(block, "CompressedSize", compressedSize);
            SetPrivateProperty(block, "UncompressedSize", uncompressedSize);
            SetPrivateProperty(block, "Flags", (StorageBlockFlags)flags);

            storageBlocks[i] = block;
        }

        reader.EndianType = originalEndian;
        return new BlocksInfo(new AssetRipper.IO.Files.BundleFiles.Hash128(), storageBlocks);
    }

    private static DirectoryInfo<FileStreamNode> ReadObfuscatedDirectoryInfo(EndianReader reader)
    {
        var originalEndian = reader.EndianType;

        // Release: read encCount in LittleEndian, then ReverseEndianness
        reader.EndianType = EndianType.LittleEndian;
        var encCount = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32() ^ 0x5DE50A6B);
        reader.EndianType = originalEndian;

        var low = encCount & 0xFFFF;
        var high = (encCount >> 16) & 0xFFFF;
        var nodesCount = VFSDecryptor.BitConcat(16, low ^ high, low);
        nodesCount = BitOperations.RotateRight(nodesCount, 18) ^ 0xE4C1D9F2;

        var nodes = new FileStreamNode[nodesCount];
        for (int i = 0; i < nodesCount; i++)
        {
            // Release node: a(^0x8E06A9F8), b, c, d
            var a = reader.ReadUInt32() ^ 0x8E06A9F8;
            var b = reader.ReadUInt32();
            var c = reader.ReadUInt32();
            var d = reader.ReadUInt32();

            // Read name (XOR per-byte: j ^ 0x97)
            var bytes = new List<byte>();
            int count = 0;
            while (count < 256)
            {
                var bt = reader.ReadByte();
                if (bt == 0) break;
                bytes.Add(bt);
                count++;
            }
            for (int j = 0; j < bytes.Count; j++)
                bytes[j] ^= (byte)((j ^ 0x97) & 0xFF);
            string name = Encoding.ASCII.GetString(bytes.ToArray());

            // Release node: e after name
            var e = reader.ReadUInt32();

            // Release node descramble
            var a0 = (ushort)(a & 0xFFFF);
            var a1 = (ushort)((a >> 16) & 0xFFFF);
            var flags = (uint)VFSDecryptor.BitConcat(16, (uint)(a1 ^ a0), (uint)a0);
            flags = BitOperations.RotateRight(flags, 18) ^ 0xF13927C4 ^ b;

            ulong offset = VFSDecryptor.BitConcat64(32, (ulong)(d ^ c ^ 0xDAD76848), (ulong)c);
            offset = BitOperations.RotateLeft(offset, 14) ^ 0xA4F1A11747816520UL;

            ulong size = VFSDecryptor.BitConcat64(32, (ulong)(b ^ e ^ 0xDAD76848), (ulong)e);
            size = BitOperations.RotateLeft(size, 14) ^ 0xA4F1A11747816520UL;

            var node = new FileStreamNode
            {
                Offset = (long)offset,
                Size = (long)size,
                Flags = (NodeFlags)flags,
                Path = name
            };
            nodes[i] = node;
        }

        reader.EndianType = originalEndian;
        return new DirectoryInfo<FileStreamNode> { Nodes = nodes };
    }
}
