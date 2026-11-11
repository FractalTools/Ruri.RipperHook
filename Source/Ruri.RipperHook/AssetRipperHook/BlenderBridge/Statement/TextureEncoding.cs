using System.Runtime.CompilerServices;
using AssetRipper.Conversions.FastPng;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Export.UnityProjects.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_187;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Subclasses.GLTextureSettings;
using AssetRipper.TextureDecoder.Rgb.Formats;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>A texture's own sampler state: wrap along u, wrap along v, and filter, each by its engine-neutral
/// name (repeat / clamp / mirror / mirroronce; point / bilinear / trilinear, or default where the engine
/// leaves the filter to a group setting the texture does not carry).</summary>
public readonly record struct TextureSampling(string WrapU, string WrapV, string Filter);

/// <summary>A texture's pixels in a container a host loads directly: the format the asset
/// itself would export as, unless the requester only accepts others. Whether the asset
/// declares sRGB encoding travels with it -- the one fact a host must not guess from a slot
/// or a file name, read here exactly as the project exporter writes it into an importer.</summary>
public static class TextureEncoding
{
    /// <summary>The png encoder's one-time initialisation, forced through on ONE thread before
    /// any worker reaches it: its type initialisers register into a shared unsynchronised
    /// dictionary, and several running at once poison the type for the whole process. The 1x1
    /// encodes leave initialisers to the workers, so every initialiser of the module is run here.</summary>
    private static readonly Lazy<bool> Warm = new(static () =>
    {
        using MemoryStream rgba = new();
        new DirectBitmap<ColorRGBA<byte>, byte>(1, 1).Save(rgba, ImageExportFormat.Png);
        using MemoryStream rgb = new();
        new DirectBitmap<ColorRGB<byte>, byte>(1, 1).Save(rgb, ImageExportFormat.Png);
        foreach (Type type in typeof(FPng).Assembly.GetTypes().Where(static type => !type.ContainsGenericParameters))
        {
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        }
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

    /// <summary>A texture array states its encoding the same way, in its own colour space field.</summary>
    public static bool DeclaresSrgb(ITexture2DArray texture) => (ColorSpace)texture.ColorSpace == ColorSpace.Linear;

    private static readonly string[] Addresses = ["repeat", "clamp", "mirror", "mirroronce"];
    private static readonly string[] Filters = ["point", "bilinear", "trilinear"];

    /// <summary>The sampler state a texture carries of its own: the engine's per-axis wrap modes, or the
    /// single wrap mode of a build that stores only that, and its filter mode.</summary>
    public static TextureSampling Sampling(ITexture2D texture)
    {
        IGLTextureSettings settings = texture.TextureSettings_C28;
        int u = settings.Has_WrapU() ? settings.WrapU : settings.WrapMode;
        int v = settings.Has_WrapV() ? settings.WrapV : settings.WrapMode;
        return new TextureSampling(Named(Addresses, u), Named(Addresses, v), Named(Filters, settings.FilterMode));
    }

    private static string Named(string[] names, int value) =>
        value >= 0 && value < names.Length ? names[value] : throw new InvalidDataException(
            $"texture sampler value {value} is none of {string.Join('/', names)}");

    /// <summary>The container name a requester lists to take block-compressed textures as DDS files (<see cref="TextureDds"/>).</summary>
    public const string DdsContainer = "dds";

    /// <summary>The texture as a DDS file where <see cref="TextureDds"/> answers it, else null.</summary>
    public static StatementTexture? EncodeDds(ITexture2D texture, string key)
    {
        if (!TextureDds.TryEncode(texture, out byte[] file))
        {
            return null;
        }
        return new StatementTexture
        {
            Key = key,
            Name = texture.Name.String,
            Srgb = DeclaresSrgb(texture),
            Container = DdsContainer,
            Format = texture.Format_C28E.ToString(),
            Image = file,
            Sampling = Sampling(texture),
        };
    }

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
            Format = texture.Format_C28E.ToString(),
            Image = stream.ToArray(),
            Sampling = Sampling(texture),
        };
    }
}
