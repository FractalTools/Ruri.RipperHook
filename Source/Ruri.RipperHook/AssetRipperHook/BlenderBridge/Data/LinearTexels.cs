using System.Buffers.Binary;
using AssetRipper.SourceGenerated.Classes.ClassID_187;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using Ruri.RipperHook.BlenderBridge.Statements;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// A texture's first level the way a GPU reads it before filtering, as a level payload carries it: half-float
/// RGBA with an sRGB texture's colour channels decoded through the sRGB curve and alpha kept linear.
/// </summary>
public static class LinearTexels
{
    public static LevelResources.Tile Tile(ITexture2D texture) =>
        Tile(TextureLevels.Decode(texture, 0)[0], TextureEncoding.DeclaresSrgb(texture));

    public static LevelResources.Tile Tile(ITexture2DArray texture, int slice) =>
        Tile(TextureLevels.Decode(texture, slice)[0], TextureEncoding.DeclaresSrgb(texture));

    private static LevelResources.Tile Tile(TextureLevels.Level level, bool srgb)
    {
        byte[] texels = new byte[level.Rgba.Length * sizeof(ushort)];
        for (int index = 0; index < level.Rgba.Length; index++)
        {
            float value = srgb && index % 4 != 3 ? SrgbColor.Decode(level.Rgba[index]) : level.Rgba[index];
            BinaryPrimitives.WriteHalfLittleEndian(texels.AsSpan(index * sizeof(ushort)), (Half)value);
        }
        return new LevelResources.Tile(level.Width, level.Height, texels);
    }
}
