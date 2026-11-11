using System.Buffers.Binary;
using System.Reflection;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_89;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>A block-compressed texture as a DDS file: the blocks as the source stores them, the whole mip chain from the
/// largest level to the smallest, rows in storage order (the bottom row first, the order a GPU upload of a block-compressed
/// texture takes, so nothing is flipped and no block is touched), under a header that states only what the layout of the
/// bytes already implies. A Crunch-compressed source is expanded to its blocks first, by the same selection AssetRipper's own
/// converter makes. Any other format, a cubemap and a texture with several images stay with the other containers: this
/// answers exactly the textures a loader can hand to the GPU as they are.</summary>
public static class TextureDds
{
    private const uint Magic = 0x20534444;
    private const uint HeaderSize = 124;
    private const uint PixelFormatSize = 32;
    private const uint FlagCaps = 0x1;
    private const uint FlagHeight = 0x2;
    private const uint FlagWidth = 0x4;
    private const uint FlagPixelFormat = 0x1000;
    private const uint FlagMipMapCount = 0x20000;
    private const uint FlagLinearSize = 0x80000;
    private const uint PixelFormatFourCc = 0x4;
    private const uint CapsComplex = 0x8;
    private const uint CapsTexture = 0x1000;
    private const uint CapsMipMap = 0x400000;
    private const uint FourCcDxt1 = 0x31545844;
    private const uint FourCcDxt5 = 0x35545844;
    private const uint FourCcDx10 = 0x30315844;
    private const uint Dxgi7Unorm = 98;
    private const uint Texture2D = 3;
    private const int Dx10HeaderSize = 20;

    private readonly record struct BlockFormat(uint FourCc, int BlockBytes);

    private delegate bool Expander(TextureFormat format, UnityVersion version, ReadOnlySpan<byte> data, out byte[]? blocks);

    private static readonly Lazy<Expander> Crunch = new(Bind);

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "AssetRipper's converter is a rooted dependency of this assembly.")]
    private static Expander Bind()
    {
        Type handler = typeof(AssetRipper.Export.Modules.Textures.TextureConverter).Assembly
            .GetType("AssetRipper.Export.UnityProjects.Textures.CrunchHandler")
            ?? throw new MissingMemberException("AssetRipper.Export.UnityProjects.Textures.CrunchHandler");
        MethodInfo method = handler.GetMethod("DecompressCrunch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(handler.FullName, "DecompressCrunch");
        return method.CreateDelegate<Expander>();
    }

    /// <summary>The DDS file of <paramref name="texture"/>, or false where the texture is not one this container answers.</summary>
    public static bool TryEncode(ITexture2D texture, out byte[] file)
    {
        file = [];
        if (texture is ICubemap || texture.ImageCount_C28 > 1 || !TryFormat(texture.Format_C28E, out BlockFormat format))
        {
            return false;
        }
        string name = texture.GetBestName();
        int width = texture.Width_C28;
        int height = texture.Height_C28;
        int levels = Math.Max(1, texture.MipCount_C28);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"{name}: texture dimensions {width}x{height}");
        }
        byte[] blocks = Blocks(texture, name);
        long expected = 0;
        for (int level = 0; level < levels; level++)
        {
            expected += LevelBytes(Math.Max(1, width >> level), Math.Max(1, height >> level), format.BlockBytes);
        }
        if (blocks.LongLength != expected)
        {
            throw new InvalidDataException(
                $"{name}: {texture.Format_C28E} {width}x{height} with {levels} level(s) is {expected} bytes of blocks, the source holds {blocks.Length}");
        }

        bool extended = format.FourCc == FourCcDx10;
        byte[] bytes = new byte[4 + HeaderSize + (extended ? Dx10HeaderSize : 0) + blocks.Length];
        Span<byte> span = bytes;
        int at = 0;
        Put(span, ref at, Magic);
        Put(span, ref at, HeaderSize);
        Put(span, ref at, FlagCaps | FlagHeight | FlagWidth | FlagPixelFormat | FlagMipMapCount | FlagLinearSize);
        Put(span, ref at, (uint)height);
        Put(span, ref at, (uint)width);
        Put(span, ref at, (uint)LevelBytes(width, height, format.BlockBytes));
        Put(span, ref at, 0u);
        Put(span, ref at, (uint)levels);
        at += 11 * sizeof(uint);
        Put(span, ref at, PixelFormatSize);
        Put(span, ref at, PixelFormatFourCc);
        Put(span, ref at, format.FourCc);
        at += 5 * sizeof(uint);
        Put(span, ref at, CapsTexture | (levels > 1 ? CapsMipMap | CapsComplex : 0u));
        at += 4 * sizeof(uint);
        if (extended)
        {
            Put(span, ref at, Dxgi7Unorm);
            Put(span, ref at, Texture2D);
            Put(span, ref at, 0u);
            Put(span, ref at, 1u);
            Put(span, ref at, 0u);
        }
        blocks.CopyTo(span[at..]);
        file = bytes;
        return true;
    }

    private static bool TryFormat(TextureFormat source, out BlockFormat format)
    {
        switch (source)
        {
            case TextureFormat.DXT1 or TextureFormat.DXT1Crunched:
                format = new BlockFormat(FourCcDxt1, 8);
                return true;
            case TextureFormat.DXT5 or TextureFormat.DXT5Crunched:
                format = new BlockFormat(FourCcDxt5, 16);
                return true;
            case TextureFormat.BC7:
                format = new BlockFormat(FourCcDx10, 16);
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static byte[] Blocks(ITexture2D texture, string name)
    {
        byte[] stored = texture.GetImageData();
        if (!texture.Format_C28E.IsCrunched())
        {
            return stored;
        }
        if (!Crunch.Value(texture.Format_C28E, texture.Collection.Version, stored, out byte[]? blocks) || blocks is null)
        {
            throw new InvalidDataException($"{name}: the {texture.Format_C28E} data did not expand to blocks");
        }
        return blocks;
    }

    private static long LevelBytes(int width, int height, int blockBytes) =>
        (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;

    private static void Put(Span<byte> span, ref int at, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], value);
        at += sizeof(uint);
    }
}
