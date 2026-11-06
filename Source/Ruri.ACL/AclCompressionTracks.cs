namespace Ruri.ACL;

public sealed class AclTransformTrack
{
    public required AclTransform[] Samples { get; init; }
    public int ParentIndex { get; init; } = -1;
    public float Precision { get; init; } = 0.01f;
    public float ShellDistance { get; init; } = 3.0f;
}

public sealed class AclFloatTrack
{
    public required float[] Samples { get; init; }
    public float Precision { get; init; } = 0.00001f;
}
