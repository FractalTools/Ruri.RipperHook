using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using System.Buffers;
using System.Reflection;
using Ruri.RipperHook.Core;
using AssetRipper.IO.Files.Exceptions;

namespace Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;

public class BundleFileBlockReaderHook : CommonHook, IHookModule
{
    private const string TYPE = "AssetRipper.IO.Files.BundleFiles.FileStream.BundleFileBlockReader, AssetRipper.IO.Files";

    private static readonly MethodInfo CreateStream = Type.GetType(TYPE).GetMethod("CreateStream", ReflectionExtensions.PrivateStaticBindFlag());
    private static readonly MethodInfo CreateTemporaryStream = Type.GetType(TYPE).GetMethod("CreateTemporaryStream", ReflectionExtensions.PrivateStaticBindFlag());
    
    public delegate void BlockCompressionDelegate(FileStreamNode entry, Stream mStream, StorageBlock block, SmartStream cachedBlockStream, CompressionType compressType, int m_cachedBlockIndex);

    /// <summary>
    /// Optional per-game policy for creating the entry's output stream.
    /// When null, falls back to AR's private CreateStream (byte[] for small entries,
    /// MemoryStream for medium, temp file for >= 50MB).
    /// </summary>
    public delegate SmartStream EntryStreamFactoryDelegate(FileStreamNode entry);

    // Static callback used by the hooked method
    public static BlockCompressionDelegate CustomBlockCompression;
    public static EntryStreamFactoryDelegate? CustomEntryStreamFactory;

    private readonly BlockCompressionDelegate _moduleCallback;
    private readonly EntryStreamFactoryDelegate? _entryStreamFactory;

    public BundleFileBlockReaderHook(BlockCompressionDelegate callback)
        : this(callback, null) { }

    public BundleFileBlockReaderHook(BlockCompressionDelegate callback, EntryStreamFactoryDelegate? entryStreamFactory)
    {
        _moduleCallback = callback;
        _entryStreamFactory = entryStreamFactory;
    }

    public void OnApply()
    {
        CustomBlockCompression = _moduleCallback;
        CustomEntryStreamFactory = _entryStreamFactory;
    }

    /// <summary>
    /// Predefined entry-stream factory for container-format games (VFS / WMW / BLK
    /// decrypt paths) where the chunk produces hundreds of thousands of entries.
    /// Without this, every entry's data lives in a byte[] retained by the
    /// ResourceFile / GameBundle graph for the entire export lifetime — the managed
    /// heap saturates and OOMs even with a huge pagefile (managed heap is not
    /// eligible for pagefile-backed swap-out, unlike OS file cache).
    ///
    /// Spills entries that would land in the Large Object Heap (>= 85KB) to a
    /// DeleteOnClose temp file so the OS page cache owns residency; sub-LOH entries
    /// stay as byte[] (Gen0/Gen1 reclaimable, no temp-file overhead, no FileStream
    /// handle).
    ///
    /// Pass to the constructor's second arg from the game's <c>InitAttributeHook</c>:
    /// <code>
    /// RegisterModule(new BundleFileBlockReaderHook(
    ///     CustomBlockCompression,
    ///     BundleFileBlockReaderHook.SpillLargeEntriesToTemp));
    /// </code>
    /// </summary>
    public static SmartStream SpillLargeEntriesToTemp(FileStreamNode entry)
        => entry.Size >= 85_000
            ? SmartStream.CreateTemp()
            : SmartStream.CreateMemory(new byte[entry.Size]);

    [RetargetMethod(TYPE, nameof(ReadEntry))]
    public SmartStream ReadEntry(FileStreamNode entry)
    {
        // Implementation remains same, calling CustomBlockCompression
        var type = Type.GetType(TYPE);
        var m_blocksInfo = (BlocksInfo)GetPrivateField(type, "m_blocksInfo");
        var m_dataOffset = (long)GetPrivateField(type, "m_dataOffset");
        var m_stream = (SmartStream)GetPrivateField(type, "m_stream");
        var m_cachedBlockIndex = (int)GetPrivateField(type, "m_cachedBlockIndex");
        var m_cachedBlockStream = (SmartStream)GetPrivateField(type, "m_cachedBlockStream");

        if ((bool)GetPrivateField(type, "m_isDisposed"))
        {
            throw new ObjectDisposedException(nameof(type));
        }

        // Avoid storing entire non-compresed entries in memory by mapping a stream to the block location.
        if (m_blocksInfo.StorageBlocks.Length == 1 && m_blocksInfo.StorageBlocks[0].CompressionType == CompressionType.None)
        {
            if (m_dataOffset + entry.Offset + entry.Size > m_stream.Length)
            {
                throw new InvalidFormatException("Entry extends beyond the end of the stream.");
            }
            return m_stream.CreatePartial(m_dataOffset + entry.Offset, entry.Size);
        }

        // find block offsets
        int blockIndex;
        long blockCompressedOffset = 0;
        long blockDecompressedOffset = 0;
        for (blockIndex = 0; blockDecompressedOffset + m_blocksInfo.StorageBlocks[blockIndex].UncompressedSize <= entry.Offset; blockIndex++)
        {
            blockCompressedOffset += m_blocksInfo.StorageBlocks[blockIndex].CompressedSize;
            blockDecompressedOffset += m_blocksInfo.StorageBlocks[blockIndex].UncompressedSize;
        }
        long entryOffsetInsideBlock = entry.Offset - blockDecompressedOffset;
        
        using SmartStream entryStream = (SmartStream)CreateStream.Invoke(this, new object[] { entry.Size });
        long left = entry.Size;
        m_stream.Position = m_dataOffset + blockCompressedOffset;

        // copy data of all blocks used by current entry to new stream
        while (left > 0)
        {
            byte[]? rentedArray;

            long blockStreamOffset;
            Stream blockStream;
            StorageBlock block = m_blocksInfo.StorageBlocks[blockIndex];
            if (m_cachedBlockIndex == blockIndex)
            {
                blockStreamOffset = 0;
                blockStream = m_cachedBlockStream;
                rentedArray = null;
                m_stream.Position += block.CompressedSize;
            }
            else
            {
                CompressionType compressType = block.CompressionType;
                if (compressType is CompressionType.None)
                {
                    blockStreamOffset = m_dataOffset + blockCompressedOffset;
                    blockStream = m_stream;
                    rentedArray = null;
                }
                else
                {
                    blockStreamOffset = 0;
                    m_cachedBlockIndex = blockIndex;
                    object[] parameters = new object[] { block.UncompressedSize, null };
                    m_cachedBlockStream.Move((SmartStream)CreateTemporaryStream.Invoke(this, parameters));
                    rentedArray = (byte[]?)parameters[1];

                    // Callback
                    CustomBlockCompression(entry, m_stream, block, m_cachedBlockStream, compressType, m_cachedBlockIndex);

                    blockStream = m_cachedBlockStream;
                }
            }

            long blockSize = block.UncompressedSize - entryOffsetInsideBlock;
            blockStream.Position = blockStreamOffset + entryOffsetInsideBlock;
            entryOffsetInsideBlock = 0;

            long size = Math.Min(blockSize, left);
            using PartialStream partialStream = new(blockStream, blockStream.Position, size);
            partialStream.CopyTo(entryStream);
            blockIndex++;

            blockCompressedOffset += block.CompressedSize;
            left -= size;

            if (rentedArray != null)
            {
                ArrayPool<byte>.Shared.Return(rentedArray);
            }
        }
        if (left < 0)
        {
            throw new Exception($"{entry.PathFixed}, {entry.Size}, {entry.Size - left}");
        }
        entryStream.Position = 0;
        return entryStream.CreateReference();
    }
}