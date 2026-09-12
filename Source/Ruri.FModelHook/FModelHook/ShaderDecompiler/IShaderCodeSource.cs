namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// Where a library keeps its shader bytecode: the code body of a library file, the arrays a
/// serialized archive was parsed into, or the group chunks an IoStore archive spreads over its
/// container. Shaders are asked for by library index; a source answers with nothing when the
/// archive places a shader beyond what it holds.
/// </summary>
internal interface IShaderCodeSource : IDisposable
{
    /// <summary>How many bytes of code the source holds in all.</summary>
    long Length { get; }

    /// <summary>The bytes of one shader, or null when the source cannot supply that shader.</summary>
    byte[]? Read(int shaderIndex);

    /// <summary>Writes one shader's bytes to the destination; false when the source cannot supply that shader.</summary>
    bool CopyTo(int shaderIndex, Stream destination);
}
