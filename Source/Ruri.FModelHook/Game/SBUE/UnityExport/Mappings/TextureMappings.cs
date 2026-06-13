using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.GLTextureSettings;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;
using SystemArray = System.Array;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

// UTexture2D -> Texture2D. GPU-encoded mip bytes (BC/DXT/PF_*) are passed
// straight into ImageData_C28 — Unity natively ingests block formats, so we do
// NOT decode (FModelHook texture note). StreamData_C28 (the .resS external
// pointer) is deliberately left untouched; the YAML serializer turns ImageData
// into an inline hex blob.
//
// Two AR consumers read this object: (1) TextureAssetExporter -> TextureConverter
// (needs Format_C28E + Width/Height + ImageCount + ImageData + ActualImageSize),
// and (2) ImporterFactory (writes the .png.meta TextureImporter, needs the
// extra metadata: ColorSpace_C28E for sRGB, LightmapFormat_C28E for normal-map
// flagging, TextureSettings_C28 for wrap/filter, StreamingMipmaps_C28 pair, and
// IsReadable_C28). Anything we leave default lands as 0 in the meta, which is
// nonsense for sRGB textures (Unity's convention: ColorSpace=Linear means "store
// as sRGB" — TextureSettingsExtensions.cs:51 spelt out).
public static class TextureMappings
{
    public static void Register()
    {
        MapperRegistry.Map<UTexture2D, ITexture2D>(collection => collection.CreateTexture2D())
            .Set(t => t.Name_C28, s => new Utf8String(s.Name))
            .Set(t => t.Width_C28, s => Width(s))
            .Set(t => t.Height_C28, s => Height(s))
            .Set(t => t.Format_C28E, s => EnumMaps.Pixel(s.Format))
            .Set(t => t.MipCount_C28, s => ResidentMipCount(s))
            .Set(t => t.ImageData_C28, s => ImageBytes(s))
            // AR's TextureConverter treats ImageCount_C28 as the "depth" axis;
            // for a plain 2D texture it must be 1, else TryConvertToBitmap
            // bails with `Invalid texture dimensions ... Depth: 0` and no PNG.
            // (CompleteImageSize is left at 0 -> Texture2DExtensions.ActualImageSize
            // falls back to ImageDataLength, which is exactly what we want for a
            // single-image, multi-mip Texture2D.)
            .Set(t => t.ImageCount_C28, s => 1)
            .Set(t => t.IsReadable_C28, s => false)
            // ColorSpace_C28E drives ImporterFactory:116
            //   SRGBTexture = ColorSpace_C28E == ColorSpace.Linear
            // which is Unity's COUNTERINTUITIVE convention: the in-memory enum
            // value ColorSpace.Linear means "the source is sRGB-encoded, mark for
            // linearization on sample" (see TextureSettingsExtensions.cs:51:
            //   ColorSpace = value ? Linear : Gamma  for SRGB=true/false).
            // UE's UTexture.SRGB defaults true and is set per-asset. Leaving this
            // unset = default 0 = Gamma = sRGBTexture:0 in the .meta even for
            // sRGB diffuse maps -> Unity re-imports them as linear and every
            // surface looks washed-out. The single biggest user-facing bug.
            .Set(t => t.ColorSpace_C28E, s => s.SRGB ? ColorSpace.Linear : ColorSpace.Gamma)
            // LightmapFormat_C28E drives both (a) ImporterFactory:123 ->
            //   TextureType = LightmapFormat.IsNormalmap() ? NormalMap : Default
            // (sets meta textureType:1 -> Unity bumps up the normal-map import
            // pipeline) and (b) TextureConverter:178 -> UnpackNormal *only* when
            // value is NormalmapDXT5nm (a legacy DXT5-swizzled AG normal-map
            // packing). UE normal maps cook as PF_BC5 (two-channel) -> we must
            // flag IsNormalmap but NOT request DXT5nm unswizzle, or AR will
            // garble the post-decode RGBA. NormalmapPlain is the right value.
            .Set(t => t.LightmapFormat_C28E, s => s.IsNormalMap ? TextureUsageMode.NormalmapPlain : TextureUsageMode.Default)
            // Streaming-mipmap fields drive the importer-side checkboxes; AR
            // reads them on every Texture2D (ImporterFactory:117-118).
            // UE has no exact equivalent, so we propagate the closest signal:
            // when the asset has *any* streamed-out mip the engine considers
            // it streaming-enabled. Priority maps from UE.LODGroup with a safe
            // default of 0 (Unity's "normal" priority).
            .Set(t => t.StreamingMipmaps_C28, s => HasStreamedMips(s))
            .Set(t => t.StreamingMipmapsPriority_C28, s => 0)
            // GLTextureSettings sub-object: wrap-U/V/W + FilterMode + Aniso +
            // MipBias. AR fully reads this when generating the importer
            // (ImporterFactory:40 CopyValues). Leaving every field at the
            // struct default (0) silently sets Repeat+Point in the .meta -
            // wrong for any UE asset that picked Clamp or Mirror in source.
            // UE side ground truth: UTexture.Filter (TextureFilter enum),
            // UTexture2D.AddressX / .AddressY (TextureAddress enum). WrapW
            // mirrors WrapU on 2D textures (Unity treats it as the depth axis
            // for cubemap/3D which 2D never uses).
            .After(ApplyTextureSettings);
    }

    // Populate TextureSettings_C28 sub-object after Create+main setters. AR's
    // ImporterFactory reads every field via CopyValues — defaults of 0 silently
    // produce Repeat+Point even when UE picked Clamp+Trilinear.
    private static void ApplyTextureSettings(UTexture2D source, ITexture2D destination, ConversionContext context)
    {
        IGLTextureSettings settings = destination.TextureSettings_C28;

        int wrapU = (int)EnumMaps.Wrap(source.AddressX);
        int wrapV = (int)EnumMaps.Wrap(source.AddressY);

        // Version-gated: WrapU/V/W exist on Unity 5.6+ texture settings;
        // WrapMode is the older singleton field. AR's source-gen surfaces both
        // via Has_* probes (see GameInitializer.VersionChanger.cs:133 for the
        // same pattern). Set whichever the target version exposes; safe to set
        // both when present (ImporterFactory copies both).
        if (settings.Has_WrapU())
        {
            settings.WrapU = wrapU;
            settings.WrapV = wrapV;
            if (settings.Has_WrapW()) settings.WrapW = wrapU;
        }
        if (settings.Has_WrapMode())
        {
            settings.WrapMode = wrapU;
        }

        // FilterMode / Aniso / MipBias have no Has_* version guards on
        // IGLTextureSettings — the source generator surfaces them on every
        // supported Unity version because the underlying field has existed
        // since the dawn of GLTextureSettings. Setting unconditionally.
        settings.FilterMode = (int)EnumMaps.Filter(source.Filter);
        settings.Aniso = 1;
        settings.MipBias = 0;
    }

    private static int Width(UTexture2D texture)
        => texture.GetFirstMip()?.SizeX ?? texture.PlatformData.SizeX;

    private static int Height(UTexture2D texture)
        => texture.GetFirstMip()?.SizeY ?? texture.PlatformData.SizeY;

    // Count only mips whose GPU bytes are actually resident (some top mips can be
    // streamed out to .resS and absent here). Kept consistent with ImageBytes so
    // MipCount always matches the concatenated payload.
    private static int ResidentMipCount(UTexture2D texture)
    {
        FTexture2DMipMap[]? mips = texture.PlatformData?.Mips;
        if (mips == null) return 1;
        int resident = 0;
        foreach (FTexture2DMipMap mip in mips)
            if (mip.BulkData?.Data is { Length: > 0 }) resident++;
        return Math.Max(1, resident);
    }

    // True when the cooked PlatformData declares mips that aren't resident in
    // the .uasset — those came from a separate .ubulk file and would have been
    // streamed in at runtime. Closest UE-side signal to Unity's
    // StreamingMipmaps importer toggle; cheap structural check, no I/O.
    private static bool HasStreamedMips(UTexture2D texture)
    {
        FTexture2DMipMap[]? mips = texture.PlatformData?.Mips;
        if (mips == null) return false;
        foreach (FTexture2DMipMap mip in mips)
            if (mip.BulkData?.Data is not { Length: > 0 }) return true;
        return false;
    }

    // Concatenate every resident mip's GPU bytes in stored (descending-size)
    // order — Unity holds the whole mip chain back-to-back in ImageData.
    private static byte[] ImageBytes(UTexture2D texture)
    {
        FTexture2DMipMap[]? mips = texture.PlatformData?.Mips;
        if (mips == null || mips.Length == 0) return SystemArray.Empty<byte>();

        int total = 0;
        foreach (FTexture2DMipMap mip in mips)
            if (mip.BulkData?.Data is { Length: > 0 } data) total += data.Length;

        byte[] result = new byte[total];
        int offset = 0;
        foreach (FTexture2DMipMap mip in mips)
        {
            if (mip.BulkData?.Data is { Length: > 0 } data)
            {
                Buffer.BlockCopy(data, 0, result, offset, data.Length);
                offset += data.Length;
            }
        }
        return result;
    }
}
