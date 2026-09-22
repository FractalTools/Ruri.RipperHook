using Ruri.RipperHook.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// One texture of a statement re-packed for a host whose texture sets take one channel or
/// one full normal per slot: a channel pulled out, a smoothness inverted into roughness,
/// Unity's two-channel tangent-space normal (X in R multiplied by A, Y in G, Z dropped -- the
/// yellow/olive look) reconstructed into the full RGB normal such a host needs, or the
/// specular half of a split normal map read off its own two channels.
/// </summary>
public static class TextureBake
{
    public const string BakeId = "core.texture.bake";
    public const string Texture = "texture";
    public const string Operation = "operation";
    public const string FlipGreen = "flip_green";

    public const string CopyRgb = "copy_rgb";
    public const string CopyRgba = "copy_rgba";
    public const string ChannelR = "channel_r";
    public const string ChannelG = "channel_g";
    public const string ChannelB = "channel_b";
    public const string ChannelA = "channel_a";
    public const string InvertA = "invert_a";
    public const string NormalUnity = "normal_unity";
    public const string NormalSplitRg = "normal_split_rg";
    public const string NormalSplitBa = "normal_split_ba";
    public const string ExtractPrefix = "extract:";
    public const string InvertPrefix = "invert:";

    public static void Register()
    {
        Datasets.PublishBlob(BakeId, DataRole.Payload,
            [.. StatementDatasets.CommonParameters(), DataParam.Text(Texture), DataParam.Text(Operation), DataParam.Flag(FlipGreen, required: false)],
            "One statement texture re-packed as PNG bytes: copy_rgb, copy_rgba, channel_r|g|b|a (one grey channel), "
            + "invert_a (roughness out of smoothness), normal_unity (the two-channel tangent-space normal rebuilt to RGB, "
            + "left alone when already standard), normal_split_rg, normal_split_ba, extract:<rgba letters>, "
            + "invert:<rgba letters>; flip_green flips Y for a host whose normals point the other way." + StatementDatasets.CommonText,
            Bake);
    }

    private static byte[] Bake(DataRequest request)
    {
        Statement statement = StatementDatasets.Flatten(request);
        string key = request.Text(Texture);
        StatementTexture source = statement.Textures.FirstOrDefault(texture => texture.Key == key)
            ?? throw new ArgumentException($"the statement carries no texture '{key}'.");
        using Image<Rgba32> image = Image.Load<Rgba32>(source.Image);
        int width = image.Width;
        int height = image.Height;
        byte[] rgba = new byte[width * height * 4];
        image.CopyPixelDataTo(rgba);
        (byte[] pixels, int channels) = Apply(request.Text(Operation), rgba, width, height, request.Flag(FlipGreen));
        using MemoryStream stream = new();
        switch (channels)
        {
            case 1:
                Image.LoadPixelData<L8>(pixels, width, height).SaveAsPng(stream);
                break;
            case 3:
                Image.LoadPixelData<Rgb24>(pixels, width, height).SaveAsPng(stream);
                break;
            default:
                Image.LoadPixelData<Rgba32>(pixels, width, height).SaveAsPng(stream);
                break;
        }
        return stream.ToArray();
    }

    private static readonly Dictionary<char, int> ChannelIndex = new() { ['r'] = 0, ['g'] = 1, ['b'] = 2, ['a'] = 3 };

    public static (byte[] Pixels, int Channels) Apply(string operation, byte[] rgba, int width, int height, bool flipGreen)
    {
        int count = width * height;
        switch (operation)
        {
            case CopyRgba:
                return (rgba, 4);
            case CopyRgb:
                return (Pick(rgba, count, [0, 1, 2], invert: false), 3);
            case ChannelR:
                return (Pick(rgba, count, [0], invert: false), 1);
            case ChannelG:
                return (Pick(rgba, count, [1], invert: false), 1);
            case ChannelB:
                return (Pick(rgba, count, [2], invert: false), 1);
            case ChannelA:
                return (Pick(rgba, count, [3], invert: false), 1);
            case InvertA:
                return (Pick(rgba, count, [3], invert: true), 1);
            case NormalUnity:
                return NormalAlreadyStandard(rgba, count)
                    ? (Pick(rgba, count, [0, 1, 2], invert: false), 3)
                    : (ReconstructNormal(rgba, count, flipGreen, useAlpha: true, 0, 1), 3);
            case NormalSplitRg:
                return (ReconstructNormal(rgba, count, flipGreen, useAlpha: false, 0, 1), 3);
            case NormalSplitBa:
                return (ReconstructNormal(rgba, count, flipGreen, useAlpha: false, 2, 3), 3);
        }
        foreach ((string prefix, bool invert) in new[] { (ExtractPrefix, false), (InvertPrefix, true) })
        {
            if (!operation.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            int[] picks = operation[prefix.Length..].Select(letter => ChannelIndex.TryGetValue(letter, out int index)
                ? index
                : throw new ArgumentException($"'{letter}' is not a channel letter (r, g, b, a).")).ToArray();
            if (picks.Length == 2)
            {
                picks = [picks[0], picks[1], -1];
            }
            return (Pick(rgba, count, picks, invert), picks.Length == 1 ? 1 : picks.Length);
        }
        throw new ArgumentException($"unknown texture operation: {operation}");
    }

    /// <summary>The named channels, in order, into the output's first components; -1 is a
    /// zero pad. Inversion is 255 - x.</summary>
    private static byte[] Pick(byte[] rgba, int count, int[] picks, bool invert)
    {
        byte[] output = new byte[count * picks.Length];
        for (int pixel = 0; pixel < count; pixel++)
        {
            for (int slot = 0; slot < picks.Length; slot++)
            {
                byte value = picks[slot] < 0 ? (byte)0 : rgba[pixel * 4 + picks[slot]];
                output[pixel * picks.Length + slot] = invert && picks[slot] >= 0 ? (byte)(255 - value) : value;
            }
        }
        return output;
    }

    /// <summary>A non-empty blue channel means the map is already a standard normal: some
    /// exports ship pre-decoded, and decoding twice would be wrong.</summary>
    private static bool NormalAlreadyStandard(byte[] rgba, int count)
    {
        if (count == 0)
        {
            return false;
        }
        double sum = 0;
        double squares = 0;
        for (int pixel = 0; pixel < count; pixel++)
        {
            double blue = rgba[pixel * 4 + 2];
            sum += blue;
            squares += blue * blue;
        }
        double mean = sum / count;
        double variance = squares / count - mean * mean;
        return Math.Sqrt(Math.Max(variance, 0)) > 8.0 && mean > 100.0;
    }

    /// <summary>X = channel x (times A when the map packs it that way), Y = channel y, Z =
    /// sqrt(1 - X² - Y²); written back as R = X, G = Y, B = (Z + 1) / 2 so flat is 128,128,255.</summary>
    private static byte[] ReconstructNormal(byte[] rgba, int count, bool flipGreen, bool useAlpha, int xChannel, int yChannel)
    {
        byte[] output = new byte[count * 3];
        for (int pixel = 0; pixel < count; pixel++)
        {
            float xRaw = rgba[pixel * 4 + xChannel] / 255f;
            float yRaw = rgba[pixel * 4 + yChannel] / 255f;
            float alpha = rgba[pixel * 4 + 3] / 255f;
            float x = useAlpha ? xRaw * alpha : xRaw;
            float y = flipGreen ? 1f - yRaw : yRaw;
            float xn = x * 2f - 1f;
            float yn = y * 2f - 1f;
            float zn = MathF.Sqrt(Math.Clamp(1f - (xn * xn + yn * yn), 0f, 1f));
            output[pixel * 3] = Quantise(x);
            output[pixel * 3 + 1] = Quantise(y);
            output[pixel * 3 + 2] = Quantise((zn + 1f) * 0.5f);
        }
        return output;
    }

    private static byte Quantise(float value) => (byte)Math.Clamp(value * 255f + 0.5f, 0f, 255f);
}
