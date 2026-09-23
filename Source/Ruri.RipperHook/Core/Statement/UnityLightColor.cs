namespace Ruri.RipperHook.Statements;

/// <summary>
/// What a Unity light emits, as UnityPlayer's own <c>Light::UpdateFinalColor</c> computes it.
/// Under linear intensity the serialized colour is decoded, multiplied by the colour
/// temperature's RGB when the light uses one, then by the intensity; otherwise the colour is
/// multiplied by the intensity first and that product decoded, and no temperature enters at all.
/// A statement states a light's colour and its own intensity apart, so the colour that goes out
/// is the emitted colour per unit of intensity: the two multiply back to exactly what the engine
/// hands its shaders.
/// </summary>
public static class UnityLightColor
{
    private const float TemperaturePivot = 6.57f;

    public static (float Red, float Green, float Blue) PerIntensity(float red, float green, float blue,
        float intensity, bool useTemperature, float temperature, bool linearIntensity)
    {
        if (linearIntensity)
        {
            (float tintRed, float tintGreen, float tintBlue) = useTemperature ? Temperature(temperature) : (1f, 1f, 1f);
            return (GammaToLinear(red) * tintRed, GammaToLinear(green) * tintGreen, GammaToLinear(blue) * tintBlue);
        }
        return (DecodedPerIntensity(red, intensity), DecodedPerIntensity(green, intensity),
            DecodedPerIntensity(blue, intensity));
    }

    /// <summary>A gamma-space product decoded, per unit of the intensity in it. At zero intensity
    /// nothing is emitted and the ratio is its own limit, the curve's linear toe.</summary>
    private static float DecodedPerIntensity(float value, float intensity) =>
        intensity == 0f ? value / 12.92f : GammaToLinear(value * intensity) / intensity;

    /// <summary>UnityPlayer's <c>GammaToLinearSpace</c>: the sRGB curve below one, one at one, and
    /// a 2.2 power above it.</summary>
    public static float GammaToLinear(float value) =>
        value <= 0.04045f ? value / 12.92f
        : value < 1f ? MathF.Pow((value + 0.055f) / 1.055f, 2.4f)
        : value == 1f ? 1f
        : MathF.Pow(value, 2.2f);

    /// <summary>UnityPlayer's <c>CorrelatedColorTemperatureToRGB</c>: the rational fit SRP Core's
    /// <c>ColorUtils</c> also carries, over kelvin clamped to [1000, 40000] and counted in
    /// thousands, pivoting at 6570 K. Each fit clamps to [0, 1].</summary>
    public static (float Red, float Green, float Blue) Temperature(float kelvin)
    {
        float k = (kelvin < 1000f ? 1000f : MathF.Min(40000f, kelvin)) / 1000f;
        float k2 = k * k;
        float red = k < TemperaturePivot
            ? 1f
            : Unit((k * 0.216422f + 1.35651f + k2 * 0.000633715f) / (k * 0.918711f - 3.24223f));
        float green = k < TemperaturePivot
            ? Unit((k * 414.271f - 399.809f + k2 * 111.543f) / (k * 164.143f + 2779.24f + k2 * 84.7356f))
            : Unit((k * 734.616f + 1370.38f + k2 * 0.689955f) / (k * 1699.87f - 4625.69f));
        float blue = k > TemperaturePivot
            ? 1f
            : Unit((348.963f - k * 523.53f + k2 * 183.62f) / (2848.82f - k * 214.52f + k2 * 78.8614f));
        return (red, green, blue);
    }

    private static float Unit(float value) => value < 0f ? 0f : MathF.Min(1f, value);
}
