using System.Text;

namespace Ruri.RipperHook.Data;

/// <summary>
/// Engine state a level sets for every material, as one payload a host applies without knowing what
/// any of it means: named four-component globals and named 3D textures, each under the engine's own
/// name. A dataset that rebuilds such state (a level's baked lighting around a camera, say) states it
/// here; the shading stacks that read those names are the ones that know what they are.
///
/// <para>Layout, little-endian: <c>"RLVR", version</c>; <c>count</c>, then per global a UTF-8 name
/// (u16 length) and four f32; <c>count</c>, then per volume a UTF-8 name, <c>width, height, depth,
/// channels</c> (i32), a format byte and the texels, x fastest, then y, then z.</para>
/// </summary>
public sealed class LevelResources
{
    public enum TexelFormat : byte
    {
        Half = 1,
        Unorm8 = 2,
    }

    private const uint Magic = 0x52564C52;
    private const uint Version = 1;

    private readonly List<(string Name, float X, float Y, float Z, float W)> _globals = [];
    private readonly List<(string Name, int Width, int Height, int Depth, int Channels, TexelFormat Format, byte[] Texels)> _volumes = [];

    public LevelResources Global(string name, float x, float y, float z, float w)
    {
        _globals.Add((name, x, y, z, w));
        return this;
    }

    public LevelResources Volume(string name, int width, int height, int depth, int channels, TexelFormat format,
        byte[] texels)
    {
        int expected = width * height * depth * channels * (format == TexelFormat.Half ? 2 : 1);
        if (texels.Length != expected)
        {
            throw new ArgumentException($"volume {name}: {texels.Length} bytes for {width}x{height}x{depth}x{channels} {format}");
        }
        _volumes.Add((name, width, height, depth, channels, format, texels));
        return this;
    }

    public byte[] Build()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(_globals.Count);
        foreach ((string name, float x, float y, float z, float w) in _globals)
        {
            WriteName(writer, name);
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(w);
        }
        writer.Write(_volumes.Count);
        foreach ((string name, int width, int height, int depth, int channels, TexelFormat format, byte[] texels) in _volumes)
        {
            WriteName(writer, name);
            writer.Write(width);
            writer.Write(height);
            writer.Write(depth);
            writer.Write(channels);
            writer.Write((byte)format);
            writer.Write(texels);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }
}
