using System.Text.RegularExpressions;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// The result of resolving a <see cref="CabSelection"/>: the chunk files AR must load plus the
/// bundle-granular filter that keeps a matched chunk from being extracted whole (EndField packs
/// 100k+ bundles per chunk, so a non-granular load OOMs before export starts).
/// </summary>
public readonly struct CabClosure
{
    /// <summary>On-disk chunk files to hand to AR.</summary>
    public required string[] Files { get; init; }

    /// <summary>Chunk-entry file names to extract; everything else in a matched chunk stays packed.</summary>
    public required HashSet<string> LoadFilterFileNames { get; init; }

    /// <summary>CABs the predicates matched, before the dependency walk.</summary>
    public required int SeedCount { get; init; }

    /// <summary>CABs in the transitive closure (seeds included).</summary>
    public required int ClosureCount { get; init; }
}

/// <summary>
/// The one way to choose what a cabmap-driven load reads: predicates pick seed CABs, one walk over
/// the columnar int graph expands them to their transitive dependencies, one result carries both
/// outputs.
///
/// <para>Predicates are <b>ANDed</b> and constrain <b>seeds only</b> — never the closure. A seed's
/// dependency may legitimately live outside the requested scope (a shared shader, a common
/// skeleton); dropping it would export a broken reference. Narrowing what you ask for must never
/// narrow what it needs.</para>
///
/// <para>Everything runs on <see cref="CabTable"/>'s int graph. A 237k-CAB library resolved through
/// a <c>Dictionary&lt;string, Entry&gt;</c> with string queues costs the whole materialization plus a
/// hash per edge; the columnar table is already loaded and indexes by id.</para>
/// </summary>
public sealed class CabSelection
{
    /// <summary>A CAB qualifies when one of its addressable container paths matches.</summary>
    public Regex[] NamePatterns { get; init; } = [];

    /// <summary>A CAB qualifies when it hosts one of these ClassIDs.</summary>
    public IReadOnlySet<int>? ClassIds { get; init; }

    /// <summary>A CAB qualifies when its on-disk chunk file lives under one of these full paths.</summary>
    public string[] FileScopes { get; init; } = [];

    /// <summary>Explicitly named seed CABs (a browser selection, a scene's placements).</summary>
    public string[] SeedCabNames { get; init; } = [];

    /// <summary>True when nothing was asked for — the caller has no selection to resolve.</summary>
    public bool IsEmpty =>
        NamePatterns.Length == 0 && ClassIds is null && FileScopes.Length == 0 && SeedCabNames.Length == 0;

    public CabClosure Resolve(CabTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        List<int> seeds = new();
        bool hasPredicate = NamePatterns.Length > 0 || ClassIds is not null || FileScopes.Length > 0;
        if (hasPredicate)
        {
            for (int id = 0; id < table.Count; id++)
            {
                if (Matches(table, id))
                {
                    seeds.Add(id);
                }
            }
        }
        foreach (string cab in SeedCabNames)
        {
            if (table.CabToId.TryGetValue(cab, out int id))
            {
                seeds.Add(id);
            }
        }

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loadFilter = new(StringComparer.OrdinalIgnoreCase);
        int closureCount = 0;
        foreach (int id in table.ClosureIds(seeds))
        {
            if (id >= table.Count)
            {
                continue; // phantom dependency: named in the graph, no file behind it
            }
            closureCount++;
            string full = Path.GetFullPath(Path.Combine(table.BaseFolder, table.RelativePath(id)));
            if (File.Exists(full))
            {
                files.Add(full);
            }
            string entryFileName = table.EntryFileName(id);
            if (entryFileName.Length > 0)
            {
                loadFilter.Add(entryFileName);
            }
        }

        return new CabClosure
        {
            Files = files.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            LoadFilterFileNames = loadFilter,
            SeedCount = seeds.Count,
            ClosureCount = closureCount,
        };
    }

    private bool Matches(CabTable table, int id)
    {
        if (ClassIds is { } classIds)
        {
            bool hit = false;
            foreach (int classId in table.ClassIds(id))
            {
                if (classIds.Contains(classId))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit)
            {
                return false;
            }
        }

        if (FileScopes.Length > 0)
        {
            string full = Path.GetFullPath(Path.Combine(table.BaseFolder, table.RelativePath(id)));
            bool inScope = false;
            foreach (string scope in FileScopes)
            {
                if (full.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                {
                    inScope = true;
                    break;
                }
            }
            if (!inScope)
            {
                return false;
            }
        }

        if (NamePatterns.Length > 0)
        {
            int pathCount = table.ContainerPathCount(id);
            for (int i = 0; i < pathCount; i++)
            {
                string path = table.ContainerPath(id, i);
                foreach (Regex pattern in NamePatterns)
                {
                    if (pattern.IsMatch(path))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        return true;
    }
}
