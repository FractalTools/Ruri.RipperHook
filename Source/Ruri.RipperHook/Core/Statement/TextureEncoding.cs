using AssetRipper.Export.Configuration;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Export.UnityProjects.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.TextureDecoder.Rgb.Formats;

namespace Ruri.RipperHook.Statements;

/// <summary>A texture's pixels in a container a host loads directly: the format the asset
/// itself would export as, unless the requester only accepts others. Whether the asset
/// declares sRGB encoding travels with it -- the one fact a host must not guess from a slot
/// or a file name, read here exactly as the project exporter writes it into an importer.</summary>
public static class TextureEncoding
{
    /// <summary>The png encoder's one-time initialisation, forced through on ONE thread before
    /// any worker reaches it: its type initialisers register into a shared unsynchronised
    /// dictionary, and several running at once poison the type for the whole process.</summary>
    private static readonly Lazy<bool> Warm = new(static () =>
    {
        using MemoryStream rgba = new();
        new DirectBitmap<ColorRGBA<byte>, byte>(1, 1).Save(rgba, ImageExportFormat.Png);
        using MemoryStream rgb = new();
        new DirectBitmap<ColorRGB<byte>, byte>(1, 1).Save(rgb, ImageExportFormat.Png);
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool Ready => Warm.Value;

    public static ImageExportFormat Negotiate(ITexture2D texture, ImageExportFormat[] accepted)
    {
        ImageExportFormat natural = texture.GetTextureExportFormat(true, ImageExportFormat.Png);
        if (accepted.Length == 0 || System.Array.IndexOf(accepted, natural) >= 0)
        {
            return natural;
        }
        return accepted[0];
    }

    public static bool DeclaresSrgb(ITexture2D texture) => texture.ColorSpace_C28E == ColorSpace.Linear;

    public static StatementTexture? Encode(ITexture2D texture, string key, ImageExportFormat[] accepted)
    {
        if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
        {
            return null;
        }
        ImageExportFormat negotiated = Negotiate(texture, accepted);
        using MemoryStream stream = new();
        bitmap.Save(stream, negotiated);
        return new StatementTexture
        {
            Key = key,
            Name = texture.Name.String,
            Srgb = DeclaresSrgb(texture),
            Container = negotiated.GetFileExtension().TrimStart('.'),
            Image = stream.ToArray(),
        };
    }
}
