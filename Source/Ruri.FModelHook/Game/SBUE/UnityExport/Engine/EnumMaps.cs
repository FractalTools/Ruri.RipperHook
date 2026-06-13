using AssetRipper.SourceGenerated.Enums;
using CUE4Parse.UE4.Assets.Exports.Texture;
using UnityFilterMode = AssetRipper.SourceGenerated.Enums.FilterMode_0;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

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
    //
    // Covers every EPixelFormat that CUE4Parse marks Supported in PixelFormatUtils
    // and that has a 1:1 Unity TextureFormat (TextureConverter.cs decode tables).
    // Mobile-shipped games (and OniValley's UE5 cooks) almost universally land on
    // ASTC / ETC2 / BC7 — any gap here turns those textures into hard NotSupported
    // throws at convert time, killing whole asset families.
    public static TextureFormat Pixel(EPixelFormat format) => format switch
    {
        // --- BC / DXT (PC + most consoles) ---
        EPixelFormat.PF_DXT1 => TextureFormat.DXT1,
        // DXT3 has no public Unity TextureFormat constant in the enum AR exposes
        // (Unity dropped it from TextureFormat decades ago). AR's TextureConverter
        // still decodes it internally when given TextureFormat.DXT3 (value-emit by
        // legacy assets) — but we can't form that value safely here. UE cooks
        // virtually never produce DXT3, so leaving it to the throw is correct:
        // any DXT3 in the wild flags loudly instead of silently mis-decoding.
        EPixelFormat.PF_DXT5 => TextureFormat.DXT5,
        EPixelFormat.PF_BC4 => TextureFormat.BC4,
        EPixelFormat.PF_BC5 => TextureFormat.BC5,
        EPixelFormat.PF_BC6H => TextureFormat.BC6H,
        EPixelFormat.PF_BC7 => TextureFormat.BC7,

        // --- Plain RGBA / grayscale ---
        // AR collision-suffixes Unity's BGRA32: _14 is the canonical value-14 form
        // (BGRA32_37 is the deprecated value-37 alias).
        EPixelFormat.PF_B8G8R8A8 => TextureFormat.BGRA32_14,
        EPixelFormat.PF_R8G8B8A8 => TextureFormat.RGBA32,
        EPixelFormat.PF_A8R8G8B8 => TextureFormat.ARGB32,
        EPixelFormat.PF_G8 => TextureFormat.R8,
        EPixelFormat.PF_G16 => TextureFormat.R16,
        EPixelFormat.PF_R8 => TextureFormat.R8,
        EPixelFormat.PF_R8G8 => TextureFormat.RG16,
        EPixelFormat.PF_G16R16 => TextureFormat.RG32,
        EPixelFormat.PF_A16B16G16R16 => TextureFormat.RGBA64,

        // --- HDR / floating point ---
        EPixelFormat.PF_R16F or EPixelFormat.PF_R16F_FILTER => TextureFormat.RHalf,
        EPixelFormat.PF_G16R16F or EPixelFormat.PF_G16R16F_FILTER => TextureFormat.RGHalf,
        EPixelFormat.PF_FloatRGBA => TextureFormat.RGBAHalf,
        EPixelFormat.PF_R32_FLOAT => TextureFormat.RFloat,
        EPixelFormat.PF_G32R32F => TextureFormat.RGFloat,
        EPixelFormat.PF_A32B32G32R32F => TextureFormat.RGBAFloat,
        EPixelFormat.PF_R9G9B9EXP5 => TextureFormat.RGB9e5Float,

        // --- ETC (Android / mobile fallback) ---
        EPixelFormat.PF_ETC1 => TextureFormat.ETC_RGB4,
        EPixelFormat.PF_ETC2_RGB => TextureFormat.ETC2_RGB,
        EPixelFormat.PF_ETC2_RGBA => TextureFormat.ETC2_RGBA8,

        // --- ASTC (modern mobile — OniValley likely path) ---
        EPixelFormat.PF_ASTC_4x4 => TextureFormat.ASTC_RGBA_4x4,
        EPixelFormat.PF_ASTC_6x6 => TextureFormat.ASTC_RGBA_6x6,
        EPixelFormat.PF_ASTC_8x8 => TextureFormat.ASTC_RGBA_8x8,
        EPixelFormat.PF_ASTC_10x10 => TextureFormat.ASTC_RGBA_10x10,
        EPixelFormat.PF_ASTC_12x12 => TextureFormat.ASTC_RGBA_12x12,

        _ => throw new NotSupportedException($"Unmapped EPixelFormat: {format}"),
    };

    public static TextureWrapMode Wrap(TextureAddress address) => address switch
    {
        TextureAddress.TA_Wrap => TextureWrapMode.Repeat,
        TextureAddress.TA_Clamp => TextureWrapMode.Clamp,
        TextureAddress.TA_Mirror => TextureWrapMode.Mirror,
        _ => TextureWrapMode.Repeat,
    };

    // UE sampler filter -> Unity FilterMode. TF_Default falls back to Bilinear
    // (Unity's universal default — UE looks the group's setting up at runtime,
    // which we have no portable visibility into; Bilinear is what every Unity
    // import wizard picks for an unspecified texture).
    public static UnityFilterMode Filter(TextureFilter filter) => filter switch
    {
        TextureFilter.TF_Nearest => UnityFilterMode.Point,
        TextureFilter.TF_Bilinear => UnityFilterMode.Bilinear,
        TextureFilter.TF_Trilinear => UnityFilterMode.Trilinear,
        _ => UnityFilterMode.Bilinear,
    };
}
