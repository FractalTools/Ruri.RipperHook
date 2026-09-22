using System.Text;
using AsmResolver.IO;
using AsmResolver.PE.File;

namespace Ruri.Tpk.Unreal.Reflection;

/// <summary>
/// The game executable at rest: its sections, the image base every pointer in its static data
/// assumes, and typed reads at a relative virtual address. Nothing is run; the data the
/// compiler laid out is read where the linker put it.
/// </summary>
internal sealed class ProgramImage
{
    private readonly PEFile file;

    public ProgramImage(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        file = PEFile.FromFile(Path);
        if (file.OptionalHeader.Magic != OptionalHeaderMagic.PE32Plus)
        {
            throw new InvalidDataException($"'{Path}' is not a 64-bit executable; the reflection data is read through 64-bit pointers.");
        }
        ImageBase = file.OptionalHeader.ImageBase;
    }

    public const uint PointerSize = 8;

    public string Path { get; }

    public ulong ImageBase { get; }

    public bool TryRva(ushort segment, uint offset, out uint rva)
    {
        if (segment == 0 || segment > file.Sections.Count)
        {
            rva = 0;
            return false;
        }
        rva = file.Sections[segment - 1].Rva + offset;
        return true;
    }

    public uint RvaOf(ulong pointer)
    {
        if (pointer < ImageBase || pointer - ImageBase > uint.MaxValue)
        {
            throw new InvalidDataException($"Pointer 0x{pointer:X} lies outside the image based at 0x{ImageBase:X}.");
        }
        return (uint)(pointer - ImageBase);
    }

    public BinaryStreamReader ReaderAt(uint rva) => file.CreateReaderAtRva(rva);

    public ulong ReadPointer(uint rva) => ReaderAt(rva).ReadUInt64();

    public uint ReadUInt32(uint rva) => ReaderAt(rva).ReadUInt32();

    public ushort ReadUInt16(uint rva) => ReaderAt(rva).ReadUInt16();

    public short ReadInt16(uint rva) => ReaderAt(rva).ReadInt16();

    public long ReadInt64(uint rva) => ReaderAt(rva).ReadInt64();

    public int ReadInt32(uint rva) => ReaderAt(rva).ReadInt32();

    public byte ReadByte(uint rva) => ReaderAt(rva).ReadByte();

    public string ReadUtf8(ulong pointer)
    {
        BinaryStreamReader reader = ReaderAt(RvaOf(pointer));
        byte[] buffer = new byte[64];
        int length = 0;
        for (byte value = reader.ReadByte(); value != 0; value = reader.ReadByte())
        {
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, length * 2);
            }
            buffer[length++] = value;
        }
        return Encoding.UTF8.GetString(buffer, 0, length);
    }
}
