using System.Globalization;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

namespace Ruri.FModelHook.Game.SBUE;

/// <summary>
/// 贴图解码的唯一入口:数组贴图各层竖向拼成条带图,层数写同名 <c>.slices</c> 旁文件。
/// CUE4Parse 的通用 <c>Decode</c> 只出第 0 层,而 shader 会按 slice 取到后面的层。
/// </summary>
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

    /// <summary>层数 &gt;1 时写 <c>&lt;图片&gt;.slices</c>,消费侧据此把条带还原成数组;单层不写。</summary>
    public static void WriteSliceCount(string imagePath, int slices)
    {
        if (slices <= 1) return;
        File.WriteAllText(imagePath + ".slices", slices.ToString(CultureInfo.InvariantCulture));
    }
}
