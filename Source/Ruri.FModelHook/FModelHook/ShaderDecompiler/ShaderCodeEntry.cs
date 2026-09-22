namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>One shader of a library: where its bytes sit in the code body, how many there are, and the pipeline stage it runs at.</summary>
internal struct ShaderCodeEntry
{
    public ulong Offset;
    public uint Size;
    public uint UncompressedSize;
    public byte Frequency;
}
