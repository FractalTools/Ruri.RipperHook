namespace Ruri.RipperHook.RuriEngine;

internal sealed class NodeComponent
{
    public required long FileId { get; init; }

    public required string ClassName { get; init; }

    public required UnityYamlMapping Body { get; init; }

    public RecordClass? Row { get; init; }

    public string? ComponentType { get; init; }

    public long HostKey { get; set; }
}
