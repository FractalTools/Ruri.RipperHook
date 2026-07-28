using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
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
/// <para>The predicate scan runs over all cores and materializes nothing: container paths are
/// regex-matched as spans decoded into a pooled buffer, the file scope collapses to one bool per
/// DISTINCT chunk file (a 237k-CAB game has a few dozen), and seed names binary-search the sorted
/// name column. The scan's only allocations are the seed lists themselves.</para>
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
            bool[]? scopeByFile = ScopeMatchesByFile(table);
            // Partitions are clamped to core count because each one clones the patterns: Regex
            // caches a single matcher state internally, so concurrent IsMatch on a shared instance
            // degenerates into a per-call allocation storm. A private interpreted clone per
            // partition (~µs each) keeps the whole scan contention- and allocation-free.
            int rangeSize = Math.Max(4096, table.Count / Environment.ProcessorCount + 1);
            ConcurrentBag<(int Start, List<int> Ids)> partitions = new();
            Parallel.ForEach(Partitioner.Create(0, table.Count, rangeSize), range =>
            {
                (int start, int end) = range;
                List<int> local = new();
                char[]? buffer = null;
                Regex[] patterns = NamePatterns;
                if (patterns.Length > 0)
                {
                    buffer = ArrayPool<char>.Shared.Rent(Math.Max(1, table.MaxContainerPathUtf8Length));
                    patterns = new Regex[NamePatterns.Length];
                    for (int i = 0; i < NamePatterns.Length; i++)
                    {
                        patterns[i] = new Regex(NamePatterns[i].ToString(), NamePatterns[i].Options & ~RegexOptions.Compiled);
                    }
                }
                try
                {
                    for (int id = start; id < end; id++)
                    {
                        if (Matches(table, id, scopeByFile, patterns, buffer))
                        {
                            local.Add(id);
                        }
                    }
                }
                finally
                {
                    if (buffer is not null)
                    {
                        ArrayPool<char>.Shared.Return(buffer);
                    }
                }
                if (local.Count > 0)
                {
                    partitions.Add((start, local));
                }
            });
            foreach ((_, List<int> ids) in partitions.OrderBy(static p => p.Start))
            {
                seeds.AddRange(ids);
            }
        }
        foreach (string cab in SeedCabNames)
        {
            if (table.TryGetId(cab, out int id))
            {
                seeds.Add(id);
            }
        }

        // Closure output, file-deduplicated up front: chunk files repeat across the whole closure,
        // so full-path resolution and the exists-probe run once per DISTINCT file, not per CAB.
        bool[] fileSeen = new bool[table.FileCount];
        HashSet<string> loadFilter = new(StringComparer.OrdinalIgnoreCase);
        int closureCount = 0;
        foreach (int id in table.ClosureIds(seeds))
        {
            if (id >= table.Count)
            {
                continue; // phantom dependency: named in the graph, no file behind it
            }
            closureCount++;
            fileSeen[table.FileIndex[id]] = true;
            string entryFileName = table.EntryFileName(id);
            if (entryFileName.Length > 0)
            {
                loadFilter.Add(entryFileName);
            }
        }

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        for (int fileId = 0; fileId < table.FileCount; fileId++)
        {
            if (!fileSeen[fileId])
            {
                continue;
            }
            string full = Path.GetFullPath(Path.Combine(table.BaseFolder, table.DistinctFile(fileId)));
            if (File.Exists(full))
            {
                files.Add(full);
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

    /// <summary><see cref="FileScopes"/> collapsed to one bool per distinct chunk file: relativize
    /// each scope to the map's base ONCE, prefix-compare against the few dozen distinct rows, and
    /// the per-CAB test becomes a single array read. Null when no scope was given.</summary>
    private bool[]? ScopeMatchesByFile(CabTable table)
    {
        if (FileScopes.Length == 0)
        {
            return null;
        }
        string[] scopes = new string[FileScopes.Length];
        for (int i = 0; i < FileScopes.Length; i++)
        {
            string relative = Path.GetRelativePath(table.BaseFolder, FileScopes[i]);
            // The whole base folder as scope constrains nothing; "" would prefix-match everything anyway.
            scopes[i] = relative == "." ? string.Empty : relative.TrimEnd(Path.DirectorySeparatorChar);
        }

        bool[] match = new bool[table.FileCount];
        for (int fileId = 0; fileId < table.FileCount; fileId++)
        {
            string relative = table.DistinctFile(fileId);
            foreach (string scope in scopes)
            {
                if (scope.Length == 0 || relative.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                {
                    match[fileId] = true;
                    break;
                }
            }
        }
        return match;
    }

    private bool Matches(CabTable table, int id, bool[]? scopeByFile, Regex[] namePatterns, char[]? pathBuffer)
    {
        if (scopeByFile is not null && !scopeByFile[table.FileIndex[id]])
        {
            return false;
        }

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

        if (namePatterns.Length > 0)
        {
            int pathCount = table.ContainerPathCount(id);
            for (int i = 0; i < pathCount; i++)
            {
                int written = Encoding.UTF8.GetChars(table.ContainerPathUtf8(id, i), pathBuffer);
                ReadOnlySpan<char> path = pathBuffer.AsSpan(0, written);
                foreach (Regex pattern in namePatterns)
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
