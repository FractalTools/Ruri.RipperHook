using System.Reflection;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.TextureDecoder.Rgb;
using AssetRipper.TextureDecoder.Rgb.Formats;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Two things this pipeline changes about the one place AssetRipper turns a Texture2D into a
/// bitmap, both of them corrections rather than preferences:
///
/// <para>THE THIRD CHANNEL of a BC5 normal map, which the format does not store and the decoder
/// leaves at zero -- see <see cref="RestoreNormalZ"/>.</para>
///
/// <para>THE READ, put behind a lock so the conversion is safe to run on every core -- see
/// <see cref="ReadImageDataSerially"/>.</para>
/// </summary>
public class TextureConversionHook : CommonHook, IHookModule
{
    public void OnApply()
    {
    }

    [RetargetMethodFunc(typeof(TextureConverter), nameof(TextureConverter.TryConvertToBitmap), typeof(ITexture2D), typeof(DirectBitmap))]
    public static bool CorrectTheBitmap(ILContext il)
    {
        MethodInfo restore = typeof(TextureConversionHook).GetMethod(nameof(RestoreNormalZ), BindingFlags.Public | BindingFlags.Static)!;
        ILCursor cursor = new(il);
        bool rewritten = false;
        // The turn is the anchor, not the subject: it is the one point in this method where the
        // finished bitmap is on the stack with the texture still in argument zero.
        if (cursor.TryGotoNext(MoveType.Before, instruction => IsCallTo(instruction, nameof(DirectBitmap), nameof(DirectBitmap.FlipY))))
        {
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Call, restore);
            rewritten = true;
        }
        SerializeTheRead(il);
        return rewritten;
    }

    /// <summary>
    /// Put a two-channel normal map's Z back.
    ///
    /// BC5 stores X and Y only: Z is not stored because the vector is unit length and every
    /// engine that ships these maps derives it when sampling. The decoder leaves the third
    /// channel at zero, so an exported image hands a host (x, y, -1) after the usual 2c-1
    /// decode and every normal points into the surface -- with nothing to see but shading that
    /// is subtly wrong everywhere. An image has no shader behind it, so it has to carry Z.
    ///
    /// The arithmetic is upstream's own (TextureConverter.UnpackNormal), which reconstructs the
    /// same channel for the DXT5nm packing -- that one also swaps alpha and red, which BC5 does
    /// not, so only the reconstruction is shared.
    /// </summary>
    public static void RestoreNormalZ(DirectBitmap bitmap, ITexture2D texture)
    {
        if (texture.Format_C28E != TextureFormat.BC5 || bitmap is not DirectBitmap<ColorRGBA<byte>, byte> pixels)
        {
            return;
        }
        Span<ColorRGBA<byte>> span = pixels.Pixels;
        for (int index = 0; index < span.Length; index++)
        {
            span[index].GetChannels(out byte red, out byte green, out _, out byte alpha);
            const double MagnitudeSquared = 255d * 255d;
            double x = red * 2d - 255d;
            double y = green * 2d - 255d;
            double z = double.Sqrt(MagnitudeSquared - double.Min(x * x + y * y, MagnitudeSquared));
            span[index].SetChannels(red, green, (byte)Math.Clamp((z + 255d) / 2d, 0d, 255d), alpha);
        }
    }

    /// <summary>
    /// Put the image-data read behind a lock, so the conversion is safe to call from several
    /// threads at once. The bytes of a streamed texture come off the one stream its resource
    /// file is: read it concurrently and the stream position interleaves, which shows up as
    /// corrupted pixels and never as an error. Everything after the read is computation over a
    /// buffer, which is what the caller then spreads across cores.
    /// </summary>
    private static void SerializeTheRead(ILContext il)
    {
        MethodInfo serialized = typeof(TextureConversionHook).GetMethod(nameof(ReadImageDataSerially), BindingFlags.Public | BindingFlags.Static)!;
        ILCursor cursor = new(il);
        while (cursor.TryGotoNext(MoveType.Before, IsImageDataRead))
        {
            cursor.Remove();
            cursor.Emit(OpCodes.Call, serialized);
            ReadsSerialized = true;
        }
    }

    /// <summary>Whether the read inside the conversion is serialised, so a caller may convert on every core.</summary>
    public static bool ReadsSerialized { get; private set; }

    private static readonly object ImageDataGate = new();

    public static byte[] ReadImageDataSerially(ITexture2D texture)
    {
        lock (ImageDataGate)
        {
            return texture.GetImageData();
        }
    }

    /// <summary>
    /// The image-data read, whichever extension container the compiler put it in: AssetRipper
    /// declares it as an extension MEMBER, which lowers into the type holding that block
    /// (Texture2DExtensions), not the one whose interface it extends.
    /// </summary>
    private static bool IsImageDataRead(Instruction instruction) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference reference
        && reference.Name == nameof(Texture2DExtensions.GetImageData)
        && reference.DeclaringType.Name.EndsWith("Extensions", StringComparison.Ordinal);

    private static bool IsCallTo(Instruction instruction, string declaringType, string method) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference reference
        && reference.Name == method
        && reference.DeclaringType.Name == declaringType;
}
