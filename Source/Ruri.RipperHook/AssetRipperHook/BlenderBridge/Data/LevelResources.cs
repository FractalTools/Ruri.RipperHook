using System.Text;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// Engine state a level sets for every material, as one payload a host applies without knowing what
/// any of it means: named four-component globals and named texel blocks, each under the engine's own
/// name. A dataset that rebuilds such state (a level's baked lighting around a camera, say) states it
/// here; the shading stacks that read those names are the ones that know what they are.
///
/// <para>A texel block is a 3D texture, a 2D texture array, or a set of tiles. A volume and an array carry
/// their whole mip chain and differ only in what a mip halves: a volume's depth halves with its width and
/// height, an array keeps its slice count. A uniform array of four-component rows is a one-slice array
/// whose width is the components per element and whose height is the element count. A tile set is 2D
/// images of their own sizes, first mip only, that a host lays out as the tiles of one image (UDIM
/// tile 1001 + i for image i) and samples with its own mips and filtering.</para>
///
/// <para>Layout, little-endian: <c>"RLVR", version</c>; <c>count</c>, then per global a UTF-8 name
/// (u16 length) and four f32; <c>count</c>, then per block a UTF-8 name, a kind byte, <c>width, height,
/// depth, mips, channels</c> (i32), a format byte and the texels of every mip from the largest, each x
/// fastest, then y, then z. A tile set states width and height 0, its tile count as depth and one mip;
/// each tile follows as its own <c>width, height</c> (i32) and texels.</para>
/// </summary>
public sealed class LevelResources
{
    public enum TexelFormat : byte
    {
        Half = 1,
        Unorm8 = 2,
        Float = 3,
    }

    public enum BlockKind : byte
    {
        Volume = 0,
        Array = 1,
        Tiles = 2,
    }

    /// <summary>One tile of a tile set: its size and texels, first mip only.</summary>
    public readonly record struct Tile(int Width, int Height, byte[] Texels);

    private const uint Magic = 0x52564C52;
    private const uint Version = 3;

    private readonly List<(string Name, float X, float Y, float Z, float W)> _globals = [];
    private readonly List<Block> _blocks = [];

    private sealed record Block(string Name, BlockKind Kind, int Width, int Height, int Depth, int Mips,
        int Channels, TexelFormat Format, byte[] Texels, IReadOnlyList<Tile>? Tiles = null);

    public LevelResources Global(string name, float x, float y, float z, float w)
    {
        _globals.Add((name, x, y, z, w));
        return this;
    }

    public LevelResources Volume(string name, int width, int height, int depth, int channels, TexelFormat format,
        byte[] texels) => Add(new Block(name, BlockKind.Volume, width, height, depth, 1, channels, format, texels));

    public LevelResources Array(string name, int width, int height, int slices, int mips, int channels,
        TexelFormat format, byte[] texels) =>
        Add(new Block(name, BlockKind.Array, width, height, slices, mips, channels, format, texels));

    /// <summary>A tile set: <paramref name="tiles"/> in tile order, each of its own size.</summary>
    public LevelResources Tiles(string name, IReadOnlyList<Tile> tiles, int channels, TexelFormat format)
    {
        foreach (Tile tile in tiles)
        {
            long expected = (long)tile.Width * tile.Height * channels * BytesPerChannel(format);
            if (tile.Width <= 0 || tile.Height <= 0 || tile.Texels.Length != expected)
            {
                throw new ArgumentException($"Tiles {name}: a {tile.Width}x{tile.Height} tile of {tile.Texels.Length} "
                    + $"bytes, expected {expected} for x{channels} {format}");
            }
        }
        _blocks.Add(new Block(name, BlockKind.Tiles, 0, 0, tiles.Count, 1, channels, format, [], tiles));
        return this;
    }

    /// <summary>The texels one block of this shape holds across its whole mip chain.</summary>
    public static long TexelCount(BlockKind kind, int width, int height, int depth, int mips)
    {
        long total = 0;
        for (int mip = 0; mip < mips; mip++)
        {
            total += (long)Math.Max(1, width >> mip) * Math.Max(1, height >> mip)
                     * (kind == BlockKind.Volume ? Math.Max(1, depth >> mip) : depth);
        }
        return total;
    }

    public static int BytesPerChannel(TexelFormat format) => format switch
    {
        TexelFormat.Half => 2,
        TexelFormat.Unorm8 => 1,
        TexelFormat.Float => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private LevelResources Add(Block block)
    {
        long expected = TexelCount(block.Kind, block.Width, block.Height, block.Depth, block.Mips)
                        * block.Channels * BytesPerChannel(block.Format);
        if (block.Texels.Length != expected)
        {
            throw new ArgumentException($"{block.Kind} {block.Name}: {block.Texels.Length} bytes for "
                + $"{block.Width}x{block.Height}x{block.Depth} x{block.Channels} {block.Format} over {block.Mips} mip(s), "
                + $"expected {expected}");
        }
        _blocks.Add(block);
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
        writer.Write(_blocks.Count);
        foreach (Block block in _blocks)
        {
            WriteName(writer, block.Name);
            writer.Write((byte)block.Kind);
            writer.Write(block.Width);
            writer.Write(block.Height);
            writer.Write(block.Depth);
            writer.Write(block.Mips);
            writer.Write(block.Channels);
            writer.Write((byte)block.Format);
            writer.Write(block.Texels);
            foreach (Tile tile in block.Tiles ?? [])
            {
                writer.Write(tile.Width);
                writer.Write(tile.Height);
                writer.Write(tile.Texels);
            }
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
