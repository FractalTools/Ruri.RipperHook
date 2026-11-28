namespace Ruri.RipperHook.RuriEngine;

internal sealed class NodeObject
{
    public required long FileId { get; init; }

    public required UnityYamlMapping Body { get; init; }

    public required long TransformId { get; init; }

    public required UnityYamlMapping Transform { get; init; }

    public long ParentKey { get; set; }

    public List<NodeComponent> Components { get; } = new();

    public List<NodeObject> Children { get; } = new();
}
