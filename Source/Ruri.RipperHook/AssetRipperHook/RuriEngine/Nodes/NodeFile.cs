namespace Ruri.RipperHook.RuriEngine;

internal sealed class NodeFile(long rootKey, IReadOnlyList<NodeObject> objects, Dictionary<long, long> hosts, Dictionary<long, string> componentTypes)
{
    public long RootKey { get; } = rootKey;

    public IReadOnlyList<NodeObject> Objects { get; } = objects;

    public ReferenceValue Reference(string fileGuid, long fileId)
    {
        long key = hosts.GetValueOrDefault(fileId, fileId);
        return ReferenceValue.To(fileGuid, key, key == RootKey).WithComponentType(componentTypes.GetValueOrDefault(fileId, string.Empty));
    }
}
