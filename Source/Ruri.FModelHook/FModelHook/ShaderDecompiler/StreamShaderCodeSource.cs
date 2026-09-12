namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The code body of a library file: a shader's bytes are read from the stream at its entry's
/// offset past the body's start, one reader at a time since the stream has one position.
/// Owns the stream.
/// </summary>
internal sealed class StreamShaderCodeSource : IShaderCodeSource
{
    private readonly Stream stream;
    private readonly long baseOffset;
    private readonly ShaderCodeEntry[] entries;
    private readonly object gate = new();

    public StreamShaderCodeSource(Stream stream, long baseOffset, ShaderCodeEntry[] entries)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.baseOffset = baseOffset;
        this.entries = entries ?? throw new ArgumentNullException(nameof(entries));
        Length = stream.Length - baseOffset;
    }

    public long Length { get; }

    public byte[]? Read(int shaderIndex)
    {
        if (!Locate(shaderIndex, out long offset, out int size))
        {
            return null;
        }
        if (size == 0)
        {
            return Array.Empty<byte>();
        }
        byte[] code = new byte[size];
        lock (gate)
        {
            stream.Position = baseOffset + offset;
            if (stream.ReadAtLeast(code, size, throwOnEndOfStream: false) < size)
            {
                return null;
            }
        }
        uint uncompressed = entries[shaderIndex].UncompressedSize;
        return ShaderCodeCompression.IsCompressed((uint)size, uncompressed)
            ? ShaderCodeCompression.Decompress(code, (int)uncompressed)
            : code;
    }

    public bool CopyTo(int shaderIndex, Stream destination)
    {
        byte[]? code = Read(shaderIndex);
        if (code is null)
        {
            return false;
        }
        destination.Write(code, 0, code.Length);
        return true;
    }

    /// <summary>The slice an entry names, when it lies inside the body and fits an array.</summary>
    private bool Locate(int shaderIndex, out long offset, out int size)
    {
        offset = 0;
        size = 0;
        if (shaderIndex < 0 || shaderIndex >= entries.Length)
        {
            return false;
        }
        ShaderCodeEntry entry = entries[shaderIndex];
        if (entry.Offset > long.MaxValue || entry.Size > Array.MaxLength || (long)entry.Offset + entry.Size > Length)
        {
            return false;
        }
        offset = (long)entry.Offset;
        size = (int)entry.Size;
        return true;
    }

    public void Dispose() => stream.Dispose();
}
