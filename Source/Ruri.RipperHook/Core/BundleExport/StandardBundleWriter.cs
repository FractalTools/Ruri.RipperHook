using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using Ruri.RipperHook.HookUtils;

namespace Ruri.RipperHook.BundleExport;

public readonly record struct BundleEntry(string Path, byte[] Data, NodeFlags Flags);

public static class StandardBundleWriter
{
    public const int BlockSize = 128 * 1024;

    public static byte[] Write(IReadOnlyList<BundleEntry> entries, string generationVersion, string engineRevision)
    {
        ArgumentNullException.ThrowIfNull(entries);

        long payload = 0;
        FileStreamNode[] nodes = new FileStreamNode[entries.Count];
        for (int index = 0; index < entries.Count; index++)
        {
            BundleEntry entry = entries[index];
            nodes[index] = new FileStreamNode
            {
                Offset = payload,
                Size = entry.Data.LongLength,
                Flags = entry.Flags,
                Path = entry.Path,
            };
            payload += entry.Data.LongLength;
        }

        int blockCount = (int)((payload + BlockSize - 1) / BlockSize);
        StorageBlock[] blocks = new StorageBlock[blockCount];
        for (int index = 0; index < blockCount; index++)
        {
            uint size = (uint)Math.Min(BlockSize, payload - ((long)index * BlockSize));
            StorageBlock block = new();
            block.SetUncompressedSize(size);
            block.SetCompressedSize(size);
            block.SetFlags(default);
            blocks[index] = block;
        }

        BlocksInfo blocksInfo = new() { StorageBlocks = blocks };
        DirectoryInfo<FileStreamNode> directory = new() { Nodes = nodes };

        MemoryStream metadataBuffer = new();
        EndianWriter metadataWriter = new(metadataBuffer, EndianType.BigEndian);
        blocksInfo.Write(metadataWriter);
        directory.Write(metadataWriter);
        metadataWriter.Flush();
        byte[] metadata = metadataBuffer.ToArray();

        FileStreamBundleHeader header = new()
        {
            Version = BundleVersion.BF_520_x,
            UnityWebBundleVersion = generationVersion,
            UnityWebMinimumRevision = engineRevision,
            CompressedBlocksInfoSize = metadata.Length,
            UncompressedBlocksInfoSize = metadata.Length,
            Flags = BundleFlags.BlocksAndDirectoryInfoCombined,
        };

        MemoryStream sizingBuffer = new();
        EndianWriter sizingWriter = new(sizingBuffer, EndianType.BigEndian);
        header.Write(sizingWriter);
        sizingWriter.Flush();
        header.Size = sizingBuffer.Length + metadata.Length + payload;

        MemoryStream output = new(checked((int)header.Size));
        EndianWriter outputWriter = new(output, EndianType.BigEndian);
        header.Write(outputWriter);
        outputWriter.Flush();
        output.Write(metadata, 0, metadata.Length);
        foreach (BundleEntry entry in entries)
        {
            output.Write(entry.Data, 0, entry.Data.Length);
        }
        return output.ToArray();
    }
}
