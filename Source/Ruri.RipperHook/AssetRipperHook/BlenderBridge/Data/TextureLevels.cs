using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_187;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.TextureDecoder.Rgb.Formats;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// Every image and mip level of a texture or a texture array, decoded to 32-bit float RGBA.
///
/// <para>AssetRipper's public conversion answers mip 0 of each image in a colour type picked per format,
/// and it picks 8-bit RGBA for the block formats it does not list -- BC6H among them -- so an HDR
/// reflection probe comes out clipped at one. A reader that samples a probe the way a GPU does needs
/// the stored values and every mip, which the one decoder AssetRipper has for each format answers when
/// asked for float output: its per-format decode (<c>TextureConverter.DecodeTexture</c>) is generic over
/// the colour it writes and returns the bytes one level consumed, which is exactly the offset of the
/// next level. It is private, so it is reached here once, through a stub that calls it with float
/// RGBA; the format switch stays AssetRipper's own and there is no second one.</para>
///
/// <para>An image's levels run largest first and images follow each other at the texture's own image
/// size -- the layout AssetRipper itself slices cubemap faces and array slices by. Rows come out in
/// storage order, first stored row first.</para>
/// </summary>
public static class TextureLevels
{
    /// <summary>One decoded level: <see cref="Rgba"/> holds <c>Width * Height</c> texels.</summary>
    public readonly record struct Level(int Width, int Height, float[] Rgba);

    private delegate int LevelDecoder(object options, ReadOnlySpan<byte> input, Span<byte> output);

    private static readonly Lazy<(Type Options, LevelDecoder Decode)> Decoder = new(Bind);

    /// <summary>Every level of image <paramref name="image"/> (a cubemap face, an array slice).</summary>
    public static Level[] Decode(ITexture2D texture, int image)
    {
        TextureFormat format = texture.Format_C28E;
        if (format.IsCrunched())
        {
            throw new NotSupportedException(
                $"{texture.GetBestName()}: crunched {format} has no per-level layout to walk");
        }
        return Walk(texture.GetBestName(), format, default, texture.Width_C28, texture.Height_C28,
            Math.Max(1, texture.MipCount_C28), texture.GetImageData(), texture.ActualImageSize,
            Math.Max(1, texture.ImageCount_C28), image, texture.Collection.Version);
    }

    /// <summary>Every level of slice <paramref name="slice"/> of a texture array. An array states its format as
    /// a graphics format and stores each slice's whole mip chain in turn, as AssetRipper slices it.</summary>
    public static Level[] Decode(ITexture2DArray texture, int slice) =>
        Walk(texture.GetBestName(), default, (GraphicsFormat)texture.Format, texture.Width, texture.Height,
            Math.Max(1, texture.MipCount), texture.GetImageData(), texture.GetCompleteImageSize(),
            Math.Max(1, texture.Depth), slice, texture.Collection.Version);

    private static Level[] Walk(string name, TextureFormat format, GraphicsFormat graphicsFormat, int width0,
        int height0, int mips, byte[] data, int imageSize, int images, int image, UnityVersion version)
    {
        if (image < 0 || image >= images)
        {
            throw new ArgumentOutOfRangeException(nameof(image), image, $"{name} holds {images} image(s)");
        }
        if (data.Length < (long)imageSize * images)
        {
            throw new InvalidDataException(
                $"{name}: {data.Length} bytes of image data for {images} image(s) of {imageSize}");
        }
        (Type optionsType, LevelDecoder decode) = Decoder.Value;
        Level[] levels = new Level[mips];
        int offset = image * imageSize;
        int end = offset + imageSize;
        for (int mip = 0; mip < mips; mip++)
        {
            int width = Math.Max(1, width0 >> mip);
            int height = Math.Max(1, height0 >> mip);
            float[] rgba = new float[width * height * 4];
            object options = Activator.CreateInstance(optionsType, format, graphicsFormat, width, height, 1,
                end - offset, version)!;
            int read = decode(options, data.AsSpan(offset, end - offset),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgba.AsSpan()));
            if (read <= 0 || offset + read > end)
            {
                throw new InvalidDataException(
                    $"{name}: level {mip} of image {image} ({(format != default ? format.ToString() : graphicsFormat.ToString())} "
                    + $"{width}x{height}) did not decode");
            }
            levels[mip] = new Level(width, height, rgba);
            offset += read;
        }
        return levels;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "AssetRipper's converter is a rooted dependency of this assembly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "The instantiation is over types this assembly references directly.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The host runs on the JIT; the stub is emitted once.")]
    private static (Type Options, LevelDecoder Decode) Bind()
    {
        Type converter = typeof(TextureConverter);
        Type options = converter.GetNestedType("Options", BindingFlags.NonPublic)
                       ?? throw new MissingMemberException(converter.FullName, "Options");
        MethodInfo generic = converter.GetMethod("DecodeTexture", BindingFlags.NonPublic | BindingFlags.Static)
                             ?? throw new MissingMethodException(converter.FullName, "DecodeTexture");
        MethodInfo decode = generic.MakeGenericMethod(typeof(ColorRGBA<float>), typeof(float));
        DynamicMethod stub = new("DecodeTextureLevel", typeof(int),
            [typeof(object), typeof(ReadOnlySpan<byte>), typeof(Span<byte>)], converter.Module, skipVisibility: true);
        ILGenerator il = stub.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, options);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, decode);
        il.Emit(OpCodes.Ret);
        return (options, stub.CreateDelegate<LevelDecoder>());
    }
}
