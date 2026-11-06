namespace Ruri.ACL;

public enum AclCompressionLevel : byte
{
    Lowest = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Highest = 4,
    Automatic = 100,
}

public enum AclRotationFormat : byte
{
    QuatFull = 0,
    QuatDropWFull = 2,
    QuatDropWVariable = 3,
}

public enum AclVectorFormat : byte
{
    Vector3Full = 0,
    Vector3Variable = 1,
}

public sealed class AclCompressionSettings
{
    public AclCompressionLevel Level { get; set; } = AclCompressionLevel.Low;
    public AclRotationFormat RotationFormat { get; set; } = AclRotationFormat.QuatFull;
    public AclVectorFormat TranslationFormat { get; set; } = AclVectorFormat.Vector3Full;
    public AclVectorFormat ScaleFormat { get; set; } = AclVectorFormat.Vector3Full;
    public bool OptimizeLoops { get; set; }
    public bool StripTrivialKeyframes { get; set; }
    public float KeyframeStrippingProportion { get; set; }
    public float KeyframeStrippingThreshold { get; set; }

    internal bool IsKeyframeStrippingEnabled => StripTrivialKeyframes || KeyframeStrippingProportion > 0.0f || KeyframeStrippingThreshold > 0.0f;
    internal AclCompressionSettings Copy() => (AclCompressionSettings)MemberwiseClone();

    public static AclCompressionSettings GetRaw() => new();

    public static AclCompressionSettings GetDefault() => new()
    {
        Level = AclCompressionLevel.Automatic,
        RotationFormat = AclRotationFormat.QuatDropWVariable,
        TranslationFormat = AclVectorFormat.Vector3Variable,
        ScaleFormat = AclVectorFormat.Vector3Variable,
        OptimizeLoops = true,
        StripTrivialKeyframes = true,
    };
}
