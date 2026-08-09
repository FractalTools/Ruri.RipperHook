using AssetRipper.SourceGenerated;
using Ruri.RipperHook.Tables;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// The one quick-search / rule-filter / sort engine over a <see cref="CabTable"/> -- the single
/// implementation behind BOTH the WinForms browser and the pythonnet bridge (each previously ran
/// its own per-row scalar copy; the Blender one measured 2.3s for a 1-char search over 237k rows).
///
/// Quick search scans the RAW UTF-8 column blobs: an ASCII-case-folded copy of each blob is built
/// once (SIMD OR 0x20 over the A-Z lanes), then every query is one vectorized IndexOf sweep per
/// column, partitioned across all cores on value boundaries. Nothing per-row is ever materialized
/// for the scan; rules and sorting derive row values lazily and cache per column. Matching is
/// ASCII-case-insensitive and byte-exact above ASCII -- the columns are lowercase-ASCII by
/// construction (cab names, container paths) or filesystem-relative paths.
///
/// Semantics are the RowTable ones both UIs already show: quick search covers Source, the raw
/// container paths, the type-name column (AssetBundle elided, with the literal "assetbundle"
/// matching rows whose display says exactly that), never straddling a value boundary; rules see
/// the same derived display strings the row views render; sorting is ordinal with the row id as
/// the stable tie-break.
/// </summary>
public sealed class CabTableSearch
{
    private readonly CabTable _table;

    // ASCII-folded copies of the searchable blobs, built once on first use. The source column
    // needs no blob at all: the table stores each chunk file ONCE (a few dozen distinct rows),
    // so its leg of the search matches the distinct names and marks rows through FileIndex.
    private byte[]? _foldedPaths;
    private byte[]? _foldedCabs;
    private int[]? _cabOffsetsReal; // CabOffsets sliced to the real (non-phantom) entries

    // Distinct class ids present in the table with their folded type names, for the
    // type-name leg of the quick search; built once.
    private (int ClassId, string FoldedName)[]? _typeNames;
    private int _assetBundleClassId = (int)ClassIDType.AssetBundle;

    // Per-column derived sort/rule values, materialized once per column on first need.
    private readonly ConcurrentDictionary<string, string[]> _derivedColumns = new(StringComparer.Ordinal);

    // Per-column ordinal RANK of every row's key (equal keys share a rank), built once: every
    // later sort is a pure int64 sort over (rank << 21 | id) -- no string comparison, no
    // comparer delegate -- with the row id packed in as the deterministic tie-break.
    private readonly ConcurrentDictionary<string, int[]> _columnRanks = new(StringComparer.Ordinal);

    public CabTableSearch(CabTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    // ── public surface ───────────────────────────────────────────────────────────────────────────

    /// <summary>Quick search + rules + sort in one pass: the visible row ids, sorted.
    /// <paramref name="sortDirection"/>: 0 = load order (ascending id), 1 = ascending, 2 = descending.</summary>
    public int[] Search(string query, IReadOnlyList<FilterRule>? rules, string sortColumn, int sortDirection)
    {
        int[] candidates = QuickSearch(query);
        if (RuleFilter.AnyEnabled(rules))
        {
            candidates = RuleFilter.Apply(candidates, rules!, Field);
        }
        return SortIds(candidates, sortColumn, sortDirection);
    }

    /// <summary>Sort an explicit id subset (the folder view's listing) by a display column.</summary>
    public int[] SortIds(int[] ids, string sortColumn, int sortDirection)
    {
        if (sortDirection == 0 || ids.Length <= 1)
        {
            Array.Sort(ids); // load order
            return ids;
        }
        int[] ranks = ColumnRanks(sortColumn);

        // (rank, id) packed into one long -- ascending long order IS (key ordinal, load order),
        // so the whole sort is branch-free int64 comparisons. Descending flips the rank half
        // only, keeping the id tie-break ascending: the same outcome as a stable reverse sort.
        // rank and id are both < Count, so rank * rowStride + id cannot collide or overflow.
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

    /// <summary>Ordinal rank of every row's key for a column (equal keys share a rank), built
    /// once per column and cached -- the string comparisons happen exactly once per table.</summary>
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

    // ── quick search ─────────────────────────────────────────────────────────────────────────────

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

        // Source column, via the distinct-file invariant: match the few dozen distinct chunk
        // files once, then mark every row whose FileIndex points at a matching one.
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

        // Container paths: path row -> entry row via the per-row path starts.
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

        // CAB names: value id == row id (the blob's tail may hold phantom names -- the offsets
        // slice stops at the last REAL entry, so phantoms never scan).
        _foldedCabs ??= Utf8Search.FoldBlob(_table.CabBlob, _table.CabOffsets[count]);
        _cabOffsetsReal ??= _table.CabOffsets.AsSpan(0, count + 1).ToArray();
        Utf8Search.ScanColumn(_foldedCabs, _cabOffsetsReal, foldedNeedle, mask,
            static (rowMask, valueId) => rowMask[valueId] = true);

        // Type names: needle -> matching class ids -> rows carrying one (AssetBundle elided,
        // because the display column elides it); plus the literal-"assetbundle" fallback for
        // rows whose class list is empty or AssetBundle-only.
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
            // Rows whose class list is empty or AssetBundle-only DISPLAY the literal
            // "AssetBundle" -- keep those searchable by that word.
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

    // ── derived row values (rule evaluation + sorting), cached per column ────────────────────────

    /// <summary>One row's display/rule value for a column -- the same derivation the row
    /// views render, cached per column. Public so a host list view can paint straight from it.</summary>
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

    /// <summary>Mirrors the packed-table consumer's JoinContainerPaths display rule exactly:
    /// separator appended BEFORE the overflow check, cap compared against the separator-inclusive
    /// running length, capped rows end in an "(+N more names)" tail.</summary>
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


    // ── ASCII case folding ───────────────────────────────────────────────────────────────────────


    private static int UpperBoundMinusOne(int[] ascending, int position)
    {
        int index = Array.BinarySearch(ascending, position);
        if (index >= 0)
        {
            // Exact offset hit: the value STARTING at position; BinarySearch may land on any
            // duplicate, walk to the last equal entry (empty values share offsets).
            while (index + 1 < ascending.Length && ascending[index + 1] == position)
            {
                index++;
            }
            return index;
        }
        return ~index - 1;
    }
}
