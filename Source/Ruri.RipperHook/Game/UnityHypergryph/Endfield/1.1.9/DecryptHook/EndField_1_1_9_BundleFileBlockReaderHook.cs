using AssetRipper.Import.Logging;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.IO.Files.Streams.Smart;
using K4os.Compression.LZ4;
using Ruri.RipperHook.Crypto;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    public static void CustomBlockCompression(FileStreamNode entry, Stream m_stream, StorageBlock block, SmartStream m_cachedBlockStream, CompressionType compressType, int m_cachedBlockIndex)
    {
        switch (compressType)
        {
            case CompressionType.Lzma:
                LzmaCompression.DecompressLzmaStream(m_stream, block.CompressedSize, m_cachedBlockStream, block.UncompressedSize);
                break;

            case CompressionType.Lz4:
            case CompressionType.Lz4HC:
                {
                    uint uncompressedSize = block.UncompressedSize;
                    byte[] uncompressedBytes = new byte[uncompressedSize];
                    Span<byte> compressedBytes = new BinaryReader(m_stream).ReadBytes((int)block.CompressedSize);
                    int bytesWritten = LZ4Codec.Decode(compressedBytes, uncompressedBytes);
                    if (bytesWritten != uncompressedSize)
                    {
                        ARIntelnalReflection.ThrowIncorrectNumberBytesWrittenMethod.Invoke(null, new object[] { entry.PathFixed, compressType, (long)uncompressedSize, (long)bytesWritten });
                    }
                    m_cachedBlockStream.Write(uncompressedBytes, 0, uncompressedBytes.Length);
                }
                break;

            case (CompressionType)CustomCompressionType.Lz4Inv: // Endfield Encrypted Block (Type 5)
                {
                    var compressedSize = (int)block.CompressedSize;
                    var uncompressedSize = (int)block.UncompressedSize;

                    var compressedBytes = new BinaryReader(m_stream).ReadBytes(compressedSize);
                    var uncompressedBytes = new byte[uncompressedSize];

                    vfsDecryptor.Decrypt(compressedBytes);

                    var numWrite = EndField_0_8_25_LZ4Inv.Instance.Decompress(compressedBytes, uncompressedBytes);

                    if (numWrite != uncompressedSize)
                    {
                        Logger.Error($"[EndField 1.1.9] Block {m_cachedBlockIndex} decompression CRITICAL failure. Expected {uncompressedSize}, Got {numWrite}.");
                    }

                    m_cachedBlockStream.Write(uncompressedBytes, 0, uncompressedBytes.Length);
                }
                break;

            default:
                if (ZstdCompression.IsZstd(m_stream))
                {
                    ZstdCompression.DecompressStream(m_stream, block.CompressedSize, m_cachedBlockStream, block.UncompressedSize);
                }
                else
                {
                    UnsupportedBundleDecompression.Throw("UnsupportedBundleDecompression", compressType);
                }
                break;
        }
    }
}
