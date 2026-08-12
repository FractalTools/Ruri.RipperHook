using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.CabMapping;

public readonly struct CabClosure
{
    public required string[] Files { get; init; }

    public required HashSet<string> LoadFilterFileNames { get; init; }

    public required int SeedCount { get; init; }

    public required int ClosureCount { get; init; }
}

public sealed class CabSelection
{
    public Regex[] NamePatterns { get; init; } = [];

    public IReadOnlySet<int>? ClassIds { get; init; }

    public string[] FileScopes { get; init; } = [];

    public string[] SeedCabNames { get; init; } = [];

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

        bool[] fileSeen = new bool[table.FileCount];
        HashSet<string> loadFilter = new(StringComparer.OrdinalIgnoreCase);
        int closureCount = 0;
        foreach (int id in table.ClosureIds(seeds))
        {
            if (id >= table.Count)
            {
                continue;            }
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
