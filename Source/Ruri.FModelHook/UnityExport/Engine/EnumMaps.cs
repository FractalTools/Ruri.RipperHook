using AssetRipper.SourceGenerated.Enums;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace Ruri.FModelHook.UnityExport.Engine;

// UE enum -> Unity enum lookups, declared once and reused by every mapping.
// A switch is simpler and more readable than any reflection scheme; an unmapped
// value throws loudly with the offending name so gaps surface in the self-test
// loop instead of silently producing a wrong asset.
public static class EnumMaps
{
    // GPU pixel format -> Unity TextureFormat. BC/DXT blocks are passed straight
    // through (Unity natively ingests them), so we never decode here; only a
    // format with genuinely no Unity equivalent throws, letting the caller fall
    // back to decoding to RGBA32 if it ever needs to.
    public static TextureFormat Pixel(EPixelFormat format) => format switch
    {
        EPixelFormat.PF_DXT1 => TextureFormat.DXT1,
        EPixelFormat.PF_DXT5 => TextureFormat.DXT5,
        EPixelFormat.PF_BC4 => TextureFormat.BC4,
        EPixelFormat.PF_BC5 => TextureFormat.BC5,
        EPixelFormat.PF_BC6H => TextureFormat.BC6H,
        EPixelFormat.PF_BC7 => TextureFormat.BC7,
        // AR collision-suffixes Unity's BGRA32: _14 is the canonical value-14 form
        // (BGRA32_37 is the deprecated value-37 alias).
        EPixelFormat.PF_B8G8R8A8 => TextureFormat.BGRA32_14,
        EPixelFormat.PF_G8 => TextureFormat.R8,
        EPixelFormat.PF_G16 => TextureFormat.R16,
        EPixelFormat.PF_FloatRGBA => TextureFormat.RGBAHalf,
        EPixelFormat.PF_A32B32G32R32F => TextureFormat.RGBAFloat,
        _ => throw new NotSupportedException($"Unmapped EPixelFormat: {format}"),
    };

    public static TextureWrapMode Wrap(TextureAddress address) => address switch
    {
        TextureAddress.TA_Wrap => TextureWrapMode.Repeat,
        TextureAddress.TA_Clamp => TextureWrapMode.Clamp,
        TextureAddress.TA_Mirror => TextureWrapMode.Mirror,
        _ => TextureWrapMode.Repeat,
    };
}
