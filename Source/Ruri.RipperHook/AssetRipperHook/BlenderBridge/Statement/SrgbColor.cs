namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>A colour component a source keeps sRGB-encoded, the way Unreal decodes it: the
/// piecewise sRGB curve of <c>FLinearColor(FColor)</c>. A statement states light colours decoded,
/// so a host scales linear values in its own units. A Unity light decodes by its own rule,
/// <see cref="UnityLightColor"/>.</summary>
public static class SrgbColor
{
    public static float Decode(float encoded) => encoded <= 0.04045f
        ? encoded / 12.92f
        : System.MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
}
