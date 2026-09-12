namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The shader arrays a serialized archive was parsed into: the parser already split the
/// archive's code body per shader, so a shader's bytes are its own array, handed out as is.
/// </summary>
internal sealed class ArrayShaderCodeSource : IShaderCodeSource
{
    private readonly byte[][] code;

    public ArrayShaderCodeSource(byte[][] code)
    {
        this.code = code ?? throw new ArgumentNullException(nameof(code));
        long length = 0;
        foreach (byte[] shader in code)
        {
            length += shader.Length;
        }
        Length = length;
    }

    public long Length { get; }

    public byte[]? Read(int shaderIndex) => shaderIndex >= 0 && shaderIndex < code.Length ? code[shaderIndex] : null;

    public bool CopyTo(int shaderIndex, Stream destination)
    {
        byte[]? shader = Read(shaderIndex);
        if (shader is null)
        {
            return false;
        }
        destination.Write(shader, 0, shader.Length);
        return true;
    }

    public void Dispose()
    {
    }
}
