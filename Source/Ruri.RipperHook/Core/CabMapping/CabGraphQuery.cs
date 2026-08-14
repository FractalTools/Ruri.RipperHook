namespace Ruri.RipperHook.CabMapping;

public enum CabDirection
{
    Forward,
    Reverse,
    Both,
}

public readonly record struct CabEdge(int FromCabId, int ToCabId, int Depth, CabDirection Direction);

public static class CabGraphQuery
{
    public static readonly string[] Directions = ["forward", "reverse", "both"];

    public static CabDirection ParseDirection(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return CabDirection.Forward;
        }
        return value.ToLowerInvariant() switch
        {
            "forward" or "deps" or "dependencies" => CabDirection.Forward,
            "reverse" or "dependents" or "users" => CabDirection.Reverse,
            "both" => CabDirection.Both,
            _ => throw new ArgumentException(
                $"direction '{value}' is not one of {string.Join(", ", Directions)}."),
        };
    }

    public static CabEdge[] Walk(CabTable table, IReadOnlyList<int> seeds, CabDirection direction, int depth)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(seeds);
        int limit = depth == 0 ? 1 : depth;
        List<CabEdge> edges = [];
        if (direction is CabDirection.Forward or CabDirection.Both)
        {
            Traverse(table, seeds, CabDirection.Forward, limit, edges);
        }
        if (direction is CabDirection.Reverse or CabDirection.Both)
        {
            Traverse(table, seeds, CabDirection.Reverse, limit, edges);
        }
        return edges.ToArray();
    }

    public static int[] Closure(CabTable table, IReadOnlyList<int> seeds, CabDirection direction, int depth)
    {
        HashSet<int> reached = new(seeds);
        foreach (CabEdge edge in Walk(table, seeds, direction, depth))
        {
            reached.Add(edge.ToCabId);
        }
        int[] ordered = reached.ToArray();
        Array.Sort(ordered);
        return ordered;
    }

    private static void Traverse(CabTable table, IReadOnlyList<int> seeds, CabDirection direction, int limit,
        List<CabEdge> edges)
    {
        HashSet<int> seen = new(seeds);
        Queue<(int Cab, int Depth)> frontier = new();
        foreach (int seed in seeds)
        {
            frontier.Enqueue((seed, 0));
        }
        while (frontier.Count > 0)
        {
            (int cab, int level) = frontier.Dequeue();
            if (limit >= 0 && level >= limit)
            {
                continue;
            }
            ReadOnlySpan<int> next = direction == CabDirection.Forward
                ? table.Dependencies(cab)
                : table.Dependents(cab);
            foreach (int target in next)
            {
                if (!seen.Add(target))
                {
                    continue;
                }
                edges.Add(new CabEdge(cab, target, level + 1, direction));
                frontier.Enqueue((target, level + 1));
            }
        }
    }
}
