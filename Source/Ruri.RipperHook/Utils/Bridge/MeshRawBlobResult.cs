namespace Ruri.RipperHook.Bridge;

internal readonly record struct MeshRawBlobResult(string MetaJson, byte[] Payload, string SkipReason)
{
    public static MeshRawBlobResult Built(string metaJson, byte[] payload)
        => new(metaJson, payload, string.Empty);

    public static MeshRawBlobResult Skipped(string reason)
        => new(string.Empty, [], reason);

    public bool HasBlob => SkipReason.Length == 0;
}
