using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.Extensions;
using K4os.Compression.LZ4;
using Ruri.RipperHook.Crypto;
using System.Numerics;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
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

            // 2. Decompress (Standard LZ4)
            var uncompressedBytes = new byte[uncompressedSize];
            var bytesWritten = LZ4Codec.Decode(compressedBytes, uncompressedBytes);

            if (bytesWritten != uncompressedSize)
            {
                AssetRipper.Import.Logging.Logger.Warning($"[EndField] Metadata decompression mismatch. Expected {uncompressedSize}, got {bytesWritten}");
            }

            uncompressedStream = new MemoryStream(uncompressedBytes);
        }
        else
        {
            uncompressedStream = new MemoryStream(compressedBytes);
        }

        using (var reader = new EndianReader(uncompressedStream, EndianType.BigEndian))
        {
            // 1. Parse Obfuscated BlocksInfo
            var blocksInfo = ReadObfuscatedBlocksInfo(reader);
            SetPrivateProperty(_this, "BlocksInfo", blocksInfo);

            // 2. Parse Obfuscated DirectoryInfo
            var directoryInfo = ReadObfuscatedDirectoryInfo(reader);
            _this.DirectoryInfo = directoryInfo;

            //Logger.Info($"[EndField] Metadata Parsed: {blocksInfo.StorageBlocks.Length} blocks, {directoryInfo.Nodes.Length} nodes.");
        }

        // Critical Fix: Align stream if BlockInfoNeedPaddingAtStart is set (0x200)
        if ((Header.Flags & BundleFlags.BlockInfoNeedPaddingAtStart) != 0)
        {
            stream.Align(16);
        }
    }

    private static BlocksInfo ReadObfuscatedBlocksInfo(EndianReader reader)
    {
        var originalEndian = reader.EndianType;
        reader.EndianType = EndianType.BigEndian;

        var encCount = reader.ReadUInt32() ^ 0xF6825038;
        var low = encCount & 0xFFFF;
        var high = (encCount >> 16) & 0xFFFF;
        var blocksCount = VFSDecryptor.BitConcat(16, low ^ high, low);
        blocksCount = BitOperations.RotateLeft(blocksCount, 3) ^ 0x5F23A227;

        var storageBlocks = new StorageBlock[blocksCount];
        for (int i = 0; i < blocksCount; i++)
        {
            ushort encFlags = (ushort)(reader.ReadUInt16() ^ 0xAFEBU);
            var a = reader.ReadUInt16();
            var b = reader.ReadUInt16();
            var c = reader.ReadUInt16();
            var d = reader.ReadUInt16();

            uint a0 = (ushort)(encFlags & 0xFF);
            uint a1 = (ushort)((encFlags >> 8) & 0xFF);
            ushort flags = (ushort)VFSDecryptor.BitConcat(8, a0 ^ a1, a0);
            flags = (ushort)(b ^ VFSDecryptor.RotateLeft16(flags, 3) ^ 0xB7AF);

            var uncompressedSize = VFSDecryptor.BitConcat(16, (uint)(c ^ b ^ 0xE114), b);
            uncompressedSize = BitOperations.RotateLeft(uncompressedSize, 3) ^ 0x5ADA4ABA;

            var compressedSize = VFSDecryptor.BitConcat(16, (uint)(d ^ a ^ 0xE114), a);
            compressedSize = BitOperations.RotateLeft(compressedSize, 3) ^ 0x5ADA4ABA;

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
        reader.EndianType = EndianType.BigEndian;

        var encCount = reader.ReadUInt32() ^ 0xA9535111;
        var low = encCount & 0xFFFF;
        var high = (encCount >> 16) & 0xFFFF;
        var nodesCount = VFSDecryptor.BitConcat(16, low ^ high, low);
        nodesCount = BitOperations.RotateLeft(nodesCount, 3) ^ 0xAF807AFC;

        var nodes = new FileStreamNode[nodesCount];
        for (int i = 0; i < nodesCount; i++)
        {
            var a = reader.ReadUInt32();
            var b = reader.ReadUInt32();
            var c = reader.ReadUInt32();

            var bytes = new List<byte>();
            int count = 0;
            while (count < 256)
            {
                var bt = reader.ReadByte();
                if (bt == 0) break;
                bytes.Add(bt);
                count++;
            }
            string name = new string(bytes.Select(b => (char)(b ^ 0xAC)).ToArray());

            var d = reader.ReadUInt32() ^ 0xE4A15748;
            var e = reader.ReadUInt32();

            var d0 = d & 0xFFFF;
            var d1 = (d >> 16) & 0xFFFF;
            var flags = (uint)VFSDecryptor.BitConcat(16, d1 ^ d0, d0);
            flags = BitOperations.RotateLeft(flags, 3) ^ 0x54D7A374 ^ b;

            ulong offset = VFSDecryptor.BitConcat64(32, c ^ a ^ 0x342D983F, a);
            offset = VFSDecryptor.RotateLeft64(offset, 3) ^ 0x5B4FA98A430D0E62UL;

            ulong size = VFSDecryptor.BitConcat64(32, e ^ b ^ 0x342D983F, b);
            size = VFSDecryptor.RotateLeft64(size, 3) ^ 0x5B4FA98A430D0E62UL;

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