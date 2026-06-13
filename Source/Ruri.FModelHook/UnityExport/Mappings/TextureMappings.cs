using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Extensions;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Ruri.FModelHook.UnityExport.Engine;

namespace Ruri.FModelHook.UnityExport.Mappings;

// UTexture2D -> Texture2D. GPU-encoded mip bytes (BC/DXT/PF_*) are passed
// straight into ImageData_C28 — Unity natively ingests block formats, so we do
// NOT decode (FModelHook texture note). StreamData_C28 (the .resS external
// pointer) is deliberately left untouched; the YAML serializer turns ImageData
// into an inline hex blob.
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
            .Set(t => t.IsReadable_C28, s => false);
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

    // Concatenate every resident mip's GPU bytes in stored (descending-size)
    // order — Unity holds the whole mip chain back-to-back in ImageData.
    private static byte[] ImageBytes(UTexture2D texture)
    {
        FTexture2DMipMap[]? mips = texture.PlatformData?.Mips;
        if (mips == null || mips.Length == 0) return Array.Empty<byte>();

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
