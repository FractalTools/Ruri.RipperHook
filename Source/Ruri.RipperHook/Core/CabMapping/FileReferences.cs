namespace Ruri.RipperHook.CabMapping;

internal sealed class FileReferences
{
    public required string Cab { get; init; }

    public required string[] DependencyNames { get; init; }

    public required HashSet<long> RowIds { get; init; }

    public required Dictionary<long, long> MergedOwners { get; init; }

    public required HashSet<(long Source, int FileId, long Target)> Edges { get; init; }

    public bool Holds(long pathId) => RowIds.Contains(pathId) || MergedOwners.ContainsKey(pathId);

    public long OwnerOf(long pathId) => RowIds.Contains(pathId) ? pathId : MergedOwners[pathId];
}
