using CUE4Parse.Compression;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// How a shader library stores its code body. Both shapes a library comes in compress it and
/// neither says with what: a Zstd block announces itself with its own magic and an Oodle block
/// carries none, so the bytes themselves are the only statement of which it is.
///
/// One reading, because a library read without it hands the decompiler compressed bytes. Those
/// bytes still contain a stray "DXBC" or "DXIL" somewhere by chance, so the shader is not rejected
/// as unreadable -- it is confidently identified as the wrong format and fails inside the
/// translator instead, which reads as "this title's shaders are unsupported" rather than as a
/// missing decompression step.
/// </summary>
internal static class ShaderCodeCompression
{
    private static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xB5, 0x2F, 0xFD];
    private static ReadOnlySpan<byte> Lz4FrameMagic => [0x04, 0x22, 0x4D, 0x18];

    /// <summary>
    /// Whether an entry's stored bytes are compressed: the library states both sizes and a
    /// shrunk one is compressed. Measured on a shipped archive, 3170 of 3170 entries state it.
    /// </summary>
    public static bool IsCompressed(uint storedSize, uint uncompressedSize) =>
        uncompressedSize > 0 && storedSize > 0 && storedSize < uncompressedSize;

    /// <summary>
    /// The codec the bytes themselves name. Zstd and LZ4's frame format each carry a magic and
    /// zlib's first byte states its own window and method, so only Oodle -- which carries nothing
    /// -- is ever assumed, and only once everything that CAN identify itself has declined.
    /// </summary>
    public static byte[] Decompress(byte[] data, int uncompressedSize)
    {
        ReadOnlySpan<byte> head = data.AsSpan();
        if (head.StartsWith(ZstdMagic))
        {
            return Compression.Decompress(data, uncompressedSize, CompressionMethod.Zstd);
        }
        if (head.StartsWith(Lz4FrameMagic))
        {
            return Compression.Decompress(data, uncompressedSize, CompressionMethod.LZ4);
        }
        if (IsZlib(head))
        {
            return Compression.Decompress(data, uncompressedSize, CompressionMethod.Zlib);
        }

        // LZ4's BLOCK format carries no magic at all -- only its frame format does -- so a build
        // that compressed its shaders with it is indistinguishable from one that used Oodle until
        // one of them is tried. It is tried first because it fails loudly on bytes that are not
        // its own, while Oodle answers 0 and leaves the caller holding a buffer of nothing.
        if (TryLz4Block(data, uncompressedSize) is { } unpacked)
        {
            return unpacked;
        }
        if (OodleHelper.Instance is null)
        {
            throw new InvalidOperationException(
                "The shader code names no codec it could be read with and the Oodle codec is not loaded.");
        }
        byte[] result = new byte[uncompressedSize];
        OodleHelper.Decompress(data, 0, data.Length, result, 0, uncompressedSize);
        return result;
    }

    /// <summary>A zlib stream states its method and window in the first byte and checksums the pair in the second.</summary>
    private static bool IsZlib(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;

    /// <summary>
    /// The bytes read as an LZ4 block, or nothing when they are not one. The stated uncompressed
    /// size is the whole check: a block that is not LZ4 runs off its own token stream long before
    /// it fills that many bytes.
    /// </summary>
    private static byte[]? TryLz4Block(byte[] data, int uncompressedSize)
    {
        try
        {
            byte[] unpacked = Compression.Decompress(data, uncompressedSize, CompressionMethod.LZ4);
            return unpacked.Length == uncompressedSize ? unpacked : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
