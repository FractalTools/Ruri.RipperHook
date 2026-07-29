using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
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

    /// <summary>
    /// 浮点贴图额外落 <c>&lt;图片&gt;.f32</c>(magic + w + h + RGBA float32,行主序)= **参数图的唯一数据源**。
    /// 图片那份只供人眼:RGBE 只有三通道且共享指数,把 MPT(A32B32G32R32F,存的是逐布块参数)
    /// 压过去会丢掉 A、并让 (3.01, 300, 300) 这种跨量级值精度塌掉。
    /// </summary>
    public static void WriteFloatSidecar(string imagePath, CTexture texture)
    {
        if (!PixelFormatUtils.PixelFormats.TryGetValue(texture.PixelFormat, out var info)) return;
        int channels = info.NumComponents;
        long pixels = (long)texture.Width * texture.Height;
        if (pixels <= 0 || channels < 3 || texture.Data.Length != pixels * channels * 4) return;

        // 原始内存序就是 RGBA,**不翻**。(CUE4Parse 的 RGBE 编码器对这个格式传 flipOrder=true,
        // 那是它自己那条路的事;照抄过来会把 R 和 A 对调 —— 实测 MPT 列3 读成 (1,300,300,3.01),
        // 而材质里是 (3.01,300,300,1.0)。)
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
