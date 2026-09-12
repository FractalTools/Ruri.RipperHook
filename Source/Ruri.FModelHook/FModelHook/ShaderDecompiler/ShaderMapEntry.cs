namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>One shader map of a library: its run of the shared shader-index list, and its run of the preload list.</summary>
internal struct ShaderMapEntry
{
    public uint ShaderIndicesOffset;
    public uint NumShaders;
    public uint FirstPreloadIndex;
    public uint NumPreloadEntries;
}
