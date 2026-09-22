using CUE4Parse.UE4.Readers;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The code body of a serialized shader archive: a shader's bytes sit at its entry's offset past
/// the body's start and are read from there when the shader is asked for, so a caller after one
/// character's shaders never touches the rest of the archive. Owns the reader.
/// </summary>
internal sealed class ArchiveShaderCodeSource : IShaderCodeSource
{
    private readonly FArchive archive;
    private readonly long bodyStart;
    private readonly ShaderCodeEntry[] entries;
    private readonly object gate = new();

    public ArchiveShaderCodeSource(FArchive archive, long bodyStart, ShaderCodeEntry[] entries)
    {
        this.archive = archive ?? throw new ArgumentNullException(nameof(archive));
        this.bodyStart = bodyStart;
        this.entries = entries ?? throw new ArgumentNullException(nameof(entries));
        Length = archive.Length - bodyStart;
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
        byte[] code;
        lock (gate)
        {
            archive.Position = bodyStart + offset;
            code = archive.ReadBytes(size);
        }
        if (code.Length < size)
        {
            return null;
        }
        uint uncompressed = entries[shaderIndex].UncompressedSize;
        return ShaderCodeCompression.IsCompressed((uint)size, uncompressed)
            ? ShaderCodeCompression.Decompress(code, (int)uncompressed)
            : code;
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

    public void Dispose() => archive.Dispose();
}
