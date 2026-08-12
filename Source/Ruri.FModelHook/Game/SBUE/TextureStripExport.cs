using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

namespace Ruri.FModelHook.Game.SBUE;

internal static class TextureStripExport
{
    public static CTexture? Decode(UTexture texture, ETexturePlatform platform, out int slices)
    {
        slices = 1;
        if (texture is not UTexture2DArray array) return texture.Decode(platform);

        CTexture[] layers = (array.DecodeTextureArray(platform) ?? [])
            .Where(static layer => layer is not null)
            .Select(static layer => layer!)
            .ToArray();
        if (layers.Length == 0) return null;
        if (layers.Length == 1) return layers[0];

        CTexture head = layers[0];
        byte[] strip = new byte[layers.Sum(static layer => layer.Data.Length)];
        int at = 0;
        foreach (CTexture layer in layers)
        {
            layer.Data.CopyTo(strip, at);
            at += layer.Data.Length;
        }

        slices = layers.Length;
        return new CTexture(head.Width, head.Height * layers.Length, head.PixelFormat, strip);
    }

    public static void WriteSliceCount(string imagePath, int slices)
    {
        if (slices <= 1) return;
        File.WriteAllText(imagePath + ".slices", slices.ToString(CultureInfo.InvariantCulture));
    }

    public static void WriteFloatSidecar(string imagePath, CTexture texture)
    {
        if (!PixelFormatUtils.PixelFormats.TryGetValue(texture.PixelFormat, out var info)) return;
        int channels = info.NumComponents;
        long pixels = (long)texture.Width * texture.Height;
        if (pixels <= 0 || channels < 3 || texture.Data.Length != pixels * channels * 4) return;

        using var writer = new BinaryWriter(File.Create(imagePath + ".f32"));
        writer.Write(new[] { (byte)'R', (byte)'F', (byte)'3', (byte)'2' });
        writer.Write(texture.Width);
        writer.Write(texture.Height);

        for (long i = 0; i < pixels; i++)
        {
            int pixel = (int)(i * channels * 4);
            for (int c = 0; c < 4; c++)
            {
                if (c >= channels) { writer.Write(c == 3 ? 1f : 0f); continue; }
                writer.Write(BitConverter.ToSingle(texture.Data, pixel + (c * 4)));
            }
        }
    }
}
