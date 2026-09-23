namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// The small unsigned floats a GPU packs render targets into (R11G11B10's channels): a 5-bit exponent
/// and a 6- or 5-bit mantissa, no sign. A shading stack that samples such a target reads the stored
/// value, not the one written, so a reader rebuilding the target stores what the GPU would have kept.
/// </summary>
public static class PackedFloat
{
    /// <summary>R11G11B10's red and green channels.</summary>
    public const int Float11Mantissa = 6;

    /// <summary>R11G11B10's blue channel.</summary>
    public const int Float10Mantissa = 5;

    /// <summary><paramref name="value"/> as an unsigned float with a 5-bit exponent and
    /// <paramref name="mantissa"/> mantissa bits, rounded to nearest even; negatives and NaN store zero,
    /// anything past the largest finite value stores that value.</summary>
    public static float Unsigned(float value, int mantissa)
    {
        if (!(value > 0f))
        {
            return 0f;
        }
        float largest = (2f - MathF.ScaleB(1f, -mantissa)) * 32768f;
        if (value >= largest)
        {
            return largest;
        }
        int exponent = Math.Max((int)MathF.Floor(MathF.Log2(value)), -14);
        float step = MathF.ScaleB(1f, exponent - mantissa);
        return MathF.Round(value / step, MidpointRounding.ToEven) * step;
    }
}
