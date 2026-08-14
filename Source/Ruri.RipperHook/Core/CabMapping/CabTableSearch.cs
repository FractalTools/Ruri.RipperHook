using AssetRipper.SourceGenerated;
using Ruri.RipperHook.Tables;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

public sealed class CabTableSearch
{
    private readonly CabTable _table;

    private byte[]? _foldedPaths;
    private byte[]? _foldedCabs;
    private int[]? _cabOffsetsReal;
    private (int ClassId, string FoldedName)[]? _typeNames;
    private int _assetBundleClassId = (int)ClassIDType.AssetBundle;

    private readonly ConcurrentDictionary<string, string[]> _derivedColumns = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int[]> _columnRanks = new(StringComparer.Ordinal);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CabTable, CabTableSearch> Sessions = new();

    public static CabTableSearch For(CabTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Sessions.GetValue(table, static loaded => new CabTableSearch(loaded));
    }

    public CabTableSearch(CabTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }


    public int[] Search(string query, IReadOnlyList<FilterRule>? rules, string sortColumn, int sortDirection)
    {
        int[] candidates = QuickSearch(query);
        if (RuleFilter.AnyEnabled(rules))
        {
            candidates = RuleFilter.Apply(candidates, rules!, Field);
        }
        return SortIds(candidates, sortColumn, sortDirection);
    }

    public int[] SortIds(int[] ids, string sortColumn, int sortDirection)
    {
        if (sortDirection == 0 || ids.Length <= 1)
        {
            Array.Sort(ids);            return ids;
        }
        int[] ranks = ColumnRanks(sortColumn);

        long rowStride = _table.Count + 1L;
        long[] packed = new long[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            long rank = ranks[ids[i]];
            if (sortDirection == 2)
            {
                rank = _table.Count - rank;
            }
            packed[i] = rank * rowStride + ids[i];
        }
        Array.Sort(packed);
        int[] sorted = new int[ids.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            sorted[i] = (int)(packed[i] % rowStride);
        }
        return sorted;
    }

    private int[] ColumnRanks(string column)
    {
        return _columnRanks.GetOrAdd(column, key =>
        {
            int count = _table.Count;
            int[] order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }
            int[] ranks = new int[count];
            if (string.Equals(key, "deps", StringComparison.OrdinalIgnoreCase))
            {
                int[] dependencyCounts = new int[count];
                for (int id = 0; id < count; id++)
                {
                    dependencyCounts[id] = _table.DependencyCount(id);
                }
                Array.Sort(order, (a, b) => dependencyCounts[a].CompareTo(dependencyCounts[b]));
                int rank = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i > 0 && dependencyCounts[order[i]] != dependencyCounts[order[i - 1]])
                    {
                        rank++;
                    }
                    ranks[order[i]] = rank;
                }
            }
            else
            {
                string[] values = DerivedColumn(key);
                Array.Sort(order, (a, b) => string.CompareOrdinal(values[a], values[b]));
                int rank = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i > 0 && !string.Equals(values[order[i]], values[order[i - 1]], StringComparison.Ordinal))
                    {
                        rank++;
                    }
                    ranks[order[i]] = rank;
                }
            }
            return ranks;
        });
    }


    private int[] QuickSearch(string query)
    {
        string needle = query.Trim();
        int count = _table.Count;
        if (needle.Length == 0)
        {
            int[] everything = new int[count];
            for (int id = 0; id < count; id++)
            {
                everything[id] = id;
            }
            return everything;
        }

        byte[] foldedNeedle = Utf8Search.FoldNeedle(needle);
        bool[] mask = new bool[count];
        string foldedQuery = Utf8Search.FoldString(needle);

        bool[] fileMatches = new bool[_table.FileCount];
        bool anyFileMatch = false;
        for (int fileId = 0; fileId < fileMatches.Length; fileId++)
        {
            if (Utf8Search.FoldString(_table.DistinctFile(fileId)).Contains(foldedQuery, StringComparison.Ordinal))
            {
                fileMatches[fileId] = true;
                anyFileMatch = true;
            }
        }
        if (anyFileMatch)
        {
            int[] fileIndex = _table.FileIndex;
            Parallel.For(0, count, id =>
            {
                if (fileMatches[fileIndex[id]])
                {
                    mask[id] = true;
                }
            });
        }

        _foldedPaths ??= Utf8Search.FoldBlob(_table.ContainerPathBlob, _table.ContainerPathOffsets[^1]);
        int[] pathStarts = _table.ContainerPathStarts;
        Utf8Search.ScanColumn(_foldedPaths, _table.ContainerPathOffsets, foldedNeedle, mask, (rowMask, pathId) =>
        {
            int row = UpperBoundMinusOne(pathStarts, pathId);
            if ((uint)row < (uint)rowMask.Length)
            {
                rowMask[row] = true;
            }
        });

        _foldedCabs ??= Utf8Search.FoldBlob(_table.CabBlob, _table.CabOffsets[count]);
        _cabOffsetsReal ??= _table.CabOffsets.AsSpan(0, count + 1).ToArray();
        Utf8Search.ScanColumn(_foldedCabs, _cabOffsetsReal, foldedNeedle, mask,
            static (rowMask, valueId) => rowMask[valueId] = true);

        MarkTypeNameMatches(needle, mask);

        List<int> hits = new();
        for (int id = 0; id < count; id++)
        {
            if (mask[id])
            {
                hits.Add(id);
            }
        }
        return hits.ToArray();
    }

    private void MarkTypeNameMatches(string needle, bool[] mask)
    {
        (int ClassId, string FoldedName)[] typeNames = _typeNames ??= BuildTypeNames();
        string foldedQuery = Utf8Search.FoldString(needle);

        HashSet<int> matchingClassIds = new();
        foreach ((int classId, string foldedName) in typeNames)
        {
            if (classId != _assetBundleClassId && foldedName.Contains(foldedQuery, StringComparison.Ordinal))
            {
                matchingClassIds.Add(classId);
            }
        }

        int[] classFlat = _table.ClassIdsFlat;
        int[] classStarts = _table.ClassIdStarts;
        bool literalAssetBundle = "assetbundle".Contains(foldedQuery, StringComparison.Ordinal);
        if (matchingClassIds.Count == 0 && !literalAssetBundle)
        {
            return;
        }

        Parallel.For(0, _table.Count, id =>
        {
            if (mask[id])
            {
                return;
            }
            int start = classStarts[id];
            int end = classStarts[id + 1];
            bool hasNonBundle = false;
            for (int i = start; i < end; i++)
            {
                int classId = classFlat[i];
                if (classId == _assetBundleClassId)
                {
                    continue;
                }
                hasNonBundle = true;
                if (matchingClassIds.Contains(classId))
                {
                    mask[id] = true;
                    return;
                }
            }
            if (!hasNonBundle && literalAssetBundle)
            {
                mask[id] = true;
            }
        });
    }

    private (int, string)[] BuildTypeNames()
    {
        HashSet<int> distinct = new();
        foreach (int classId in _table.ClassIdsFlat)
        {
            distinct.Add(classId);
        }
        return distinct
            .Select(classId => (classId, Utf8Search.FoldString(Enum.IsDefined(typeof(ClassIDType), classId)
                ? ((ClassIDType)classId).ToString() : classId.ToString())))
            .ToArray();
    }


    public string Field(int id, string field) => field switch
    {
        "cab" => _table.CabName(id),
        "deps" => _table.DependencyCount(id).ToString(),
        _ => DerivedColumn(field)[id],
    };

    private string[] DerivedColumn(string column)
    {
        return _derivedColumns.GetOrAdd(column, key =>
        {
            string[] values = new string[_table.Count];
            Parallel.For(0, _table.Count, id => values[id] = DeriveField(id, key));
            return values;
        });
    }

    private string DeriveField(int id, string column) => column switch
    {
        "name" => DeriveName(id),
        "container" => DeriveContainer(id),
        "type_names" => DeriveTypeNames(id),
        "source" => _table.RelativePath(id),
        "bundle" => _table.EntryFileName(id),
        "cab" => _table.CabName(id),
        "deps" => _table.DependencyCount(id).ToString(),
        _ => string.Empty,
    };

    private string DeriveName(int id)
    {
        int pathCount = _table.ContainerPathCount(id);
        if (pathCount == 0)
        {
            return string.Empty;
        }
        string first = _table.ContainerPath(id, 0);
        int slash = first.LastIndexOf('/');
        string leaf = slash >= 0 ? first[(slash + 1)..] : first;
        return pathCount > 1 ? $"{leaf} (+{pathCount - 1})" : leaf;
    }

    private string DeriveContainer(int id)
    {
        const int MaxJoinChars = 16384;
        int pathCount = _table.ContainerPathCount(id);
        if (pathCount == 0)
        {
            return string.Empty;
        }
        StringBuilder joined = new();
        int length = 0;
        for (int p = 0; p < pathCount; p++)
        {
            if (p > 0)
            {
                joined.Append("  |  ");
                length += 5;
            }
            string path = _table.ContainerPath(id, p);
            if (length + path.Length > MaxJoinChars)
            {
                joined.Append($"…(+{pathCount - p} more names)");
                break;
            }
            joined.Append(path);
            length += path.Length;
        }
        return joined.ToString();
    }

    private string DeriveTypeNames(int id)
    {
        int[] classFlat = _table.ClassIdsFlat;
        int[] classStarts = _table.ClassIdStarts;
        int start = classStarts[id];
        int end = classStarts[id + 1];
        StringBuilder names = new();
        for (int i = start; i < end; i++)
        {
            int classId = classFlat[i];
            if (classId == _assetBundleClassId)
            {
                continue;
            }
            if (names.Length > 0)
            {
                names.Append(", ");
            }
            names.Append(Enum.IsDefined(typeof(ClassIDType), classId) ? ((ClassIDType)classId).ToString() : classId.ToString());
        }
        return names.Length > 0 ? names.ToString() : "AssetBundle";
    }




    private static int UpperBoundMinusOne(int[] ascending, int position)
    {
        int index = Array.BinarySearch(ascending, position);
        if (index >= 0)
        {
            while (index + 1 < ascending.Length && ascending[index + 1] == position)
            {
                index++;
            }
            return index;
        }
        return ~index - 1;
    }
}
