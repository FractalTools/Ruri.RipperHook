using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// Columnar in-memory form of a cabmap: UTF-8 string blobs + offset tables per column, an
/// int-indexed dependency graph in BOTH directions, and NO per-entry string/record
/// materialization. This is the load-time and interop-time optimum: the RCM6 on-disk format
/// stores exactly these buffers, so loading is one sequential stream read straight into the
/// final arrays -- no intermediate whole-file buffer, no per-string parse -- and a pythonnet
/// caller receives the same buffers in one crossing and slices them at C speed.
///
/// Entry ids are [0, Count). Dependency edges may reference CABs that are not entries themselves
/// (a bundle can list a dependency the scan never found); those get PHANTOM ids in
/// [Count, Count+PhantomCount) with a name but no columns, preserving classic BFS semantics
/// exactly: a dangling dependency still appears in closure OUTPUT (callers that then look it up
/// simply miss, same as before) while contributing no forward edges.
///
/// Invariants, guaranteed by construction (<see cref="FromEntries"/> is the only writer path):
///   * real entries [0, Count) are sorted by CAB name, <see cref="StringComparer.OrdinalIgnoreCase"/> --
///     <see cref="TryGetId"/> binary-searches on it, so no name-&gt;id dictionary ever exists;
///   * chunk files are stored ONCE in a distinct-file table; each entry carries an index into it
///     (a 237k-CAB game concentrates into a few dozen chunk files -- per-entry path strings were
///     231x redundant);
///   * the reverse adjacency (<see cref="Dependents"/>) is the exact transpose of
///     <see cref="Dependencies"/>, rebuilt eagerly at every construction in one counting pass --
///     forward and reverse can never disagree.
///
/// Strings are materialized lazily and only where a consumer genuinely needs a System.String
/// (closure results, file-path resolution for the few dozen chunk files of one closure).
/// </summary>
public sealed class CabTable
{
    public required string BaseFolder { get; init; }

    /// <summary>Real entries only -- phantom dependency names live above this.</summary>
    public required int Count { get; init; }
    public required int PhantomCount { get; init; }

    // CAB names: Count + PhantomCount strings (phantoms appended after real entries).
    public required byte[] CabBlob { get; init; }
    public required int[] CabOffsets { get; init; }        // Count + PhantomCount + 1

    // Distinct chunk files + per-entry index into them.
    public required byte[] DistinctFileBlob { get; init; }
    public required int[] DistinctFileOffsets { get; init; }  // FileCount + 1
    public required int[] FileIndex { get; init; }            // Count

    public required byte[] EntryFileNameBlob { get; init; }
    public required int[] EntryFileNameOffsets { get; init; } // Count + 1

    // Container paths: flat string list + per-entry row ranges.
    public required byte[] ContainerPathBlob { get; init; }
    public required int[] ContainerPathOffsets { get; init; } // PathCount + 1
    public required int[] ContainerPathStarts { get; init; }  // Count + 1 (indexes into ContainerPathOffsets rows)

    public required int[] ClassIdsFlat { get; init; }
    public required int[] ClassIdStarts { get; init; }        // Count + 1

    public required int[] DependenciesFlat { get; init; }     // ids, may include phantom ids
    public required int[] DependencyStarts { get; init; }     // Count + 1

    // Transpose of the dependency graph: who depends on me. Phantom ids have rows too (their
    // dependents), which is what lets a reverse walk start from a dangling name.
    public required int[] ReverseFlat { get; init; }          // DependenciesFlat.Length
    public required int[] ReverseStarts { get; init; }        // Count + PhantomCount + 1

    public int FileCount => DistinctFileOffsets.Length - 1;

    public string CabName(int id) => Utf8(CabBlob, CabOffsets, id);
    public string DistinctFile(int fileIndex) => Utf8(DistinctFileBlob, DistinctFileOffsets, fileIndex);
    public string RelativePath(int id) => DistinctFile(FileIndex[id]);
    public string EntryFileName(int id) => Utf8(EntryFileNameBlob, EntryFileNameOffsets, id);

    public ReadOnlySpan<byte> DistinctFileUtf8(int fileIndex)
        => DistinctFileBlob.AsSpan(DistinctFileOffsets[fileIndex], DistinctFileOffsets[fileIndex + 1] - DistinctFileOffsets[fileIndex]);

    public int ContainerPathCount(int id) => ContainerPathStarts[id + 1] - ContainerPathStarts[id];

    public string ContainerPath(int id, int pathIndex)
        => Utf8(ContainerPathBlob, ContainerPathOffsets, ContainerPathStarts[id] + pathIndex);

    /// <summary>Raw UTF-8 bytes of one container path -- the zero-allocation input for scans that
    /// decode into a pooled buffer instead of materializing 438k strings.</summary>
    public ReadOnlySpan<byte> ContainerPathUtf8(int id, int pathIndex)
    {
        int row = ContainerPathStarts[id] + pathIndex;
        return ContainerPathBlob.AsSpan(ContainerPathOffsets[row], ContainerPathOffsets[row + 1] - ContainerPathOffsets[row]);
    }

    public ReadOnlySpan<int> ClassIds(int id)
        => ClassIdsFlat.AsSpan(ClassIdStarts[id], ClassIdStarts[id + 1] - ClassIdStarts[id]);

    public ReadOnlySpan<int> Dependencies(int id)
        => DependenciesFlat.AsSpan(DependencyStarts[id], DependencyStarts[id + 1] - DependencyStarts[id]);

    public int DependencyCount(int id) => DependencyStarts[id + 1] - DependencyStarts[id];

    /// <summary>Entries that DIRECTLY depend on <paramref name="id"/> (real or phantom), ascending
    /// id order. Zero-allocation view into the eagerly built transpose.</summary>
    public ReadOnlySpan<int> Dependents(int id)
        => ReverseFlat.AsSpan(ReverseStarts[id], ReverseStarts[id + 1] - ReverseStarts[id]);

    private static string Utf8(byte[] blob, int[] offsets, int index)
        => Encoding.UTF8.GetString(blob, offsets[index], offsets[index + 1] - offsets[index]);

    private int _maxContainerPathUtf8Length = -1;

    /// <summary>Longest container-path row in bytes -- sizes the pooled decode buffer of a scan
    /// (UTF-16 char count never exceeds UTF-8 byte count). Computed once, benign to race.</summary>
    public int MaxContainerPathUtf8Length
    {
        get
        {
            int max = _maxContainerPathUtf8Length;
            if (max < 0)
            {
                max = 0;
                int[] offsets = ContainerPathOffsets;
                for (int i = 1; i < offsets.Length; i++)
                {
                    max = Math.Max(max, offsets[i] - offsets[i - 1]);
                }
                _maxContainerPathUtf8Length = max;
            }
            return max;
        }
    }

    /// <summary>CAB name (real or phantom) -&gt; id. Binary search over the sorted real entries
    /// (see the class invariants), then the few phantoms linearly. No dictionary, no upfront cost.</summary>
    public bool TryGetId(string cabName, out int id)
    {
        int lo = 0;
        int hi = Count - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int cmp = string.Compare(cabName, CabName(mid), StringComparison.OrdinalIgnoreCase);
            if (cmp == 0)
            {
                id = mid;
                return true;
            }
            if (cmp < 0)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        for (int phantom = Count; phantom < Count + PhantomCount; phantom++)
        {
            if (string.Equals(cabName, CabName(phantom), StringComparison.OrdinalIgnoreCase))
            {
                id = phantom;
                return true;
            }
        }
        id = -1;
        return false;
    }

    /// <summary>Transitive dependency closure over the int graph (seed ids included), classic BFS
    /// semantics: phantom ids are visited/reported but contribute no edges.</summary>
    public int[] ClosureIds(IEnumerable<int> seedIds) => Walk(seedIds, DependencyStarts, DependenciesFlat, Count);

    /// <summary>Transitive DEPENDENT closure -- every entry that directly or indirectly references
    /// a seed (seeds included). Same walk as <see cref="ClosureIds"/> on the transposed edges;
    /// phantom seeds expand too (their dependents are real entries).</summary>
    public int[] ReverseClosureIds(IEnumerable<int> seedIds) => Walk(seedIds, ReverseStarts, ReverseFlat, Count + PhantomCount);

    private int[] Walk(IEnumerable<int> seedIds, int[] starts, int[] flat, int expandableLimit)
    {
        bool[] visited = new bool[Count + PhantomCount];
        List<int> order = new();
        Queue<int> queue = new(seedIds);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if ((uint)id >= (uint)visited.Length || visited[id])
            {
                continue;
            }
            visited[id] = true;
            order.Add(id);
            if (id < expandableLimit)
            {
                for (int edge = starts[id]; edge < starts[id + 1]; edge++)
                {
                    queue.Enqueue(flat[edge]);
                }
            }
        }
        return order.ToArray();
    }

    /// <summary>Exact transpose of the forward edges, one counting pass + one fill pass, fill in
    /// ascending source-id order so each dependents row comes out sorted deterministically.</summary>
    private static (int[] Starts, int[] Flat) BuildReverse(int count, int phantomCount, int[] depStarts, int[] depFlat)
    {
        int total = count + phantomCount;
        int[] starts = new int[total + 1];
        foreach (int dep in depFlat)
        {
            starts[dep + 1]++;
        }
        for (int i = 0; i < total; i++)
        {
            starts[i + 1] += starts[i];
        }
        int[] flat = new int[depFlat.Length];
        int[] cursor = new int[total];
        for (int id = 0; id < count; id++)
        {
            for (int edge = depStarts[id]; edge < depStarts[id + 1]; edge++)
            {
                int dep = depFlat[edge];
                flat[starts[dep] + cursor[dep]++] = id;
            }
        }
        return (starts, flat);
    }

    /// <summary>Columnar build from Build's scan output. Establishes every class invariant:
    /// name-sorted ids, the distinct-file table, and the reverse adjacency.</summary>
    public static CabTable FromEntries(string baseFolder, IReadOnlyDictionary<string, CabMap.Entry> entries)
    {
        string[] cabs = entries.Keys.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();
        int count = cabs.Length;
        Dictionary<string, int> idOf = new(count, StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < count; id++)
        {
            idOf[cabs[id]] = id;
        }

        // Phantom pass: dependency names that are not entries.
        List<string> phantoms = new();
        foreach (string cab in cabs)
        {
            foreach (string dep in entries[cab].Dependencies)
            {
                if (!idOf.ContainsKey(dep))
                {
                    idOf[dep] = count + phantoms.Count;
                    phantoms.Add(dep);
                }
            }
        }

        BlobBuilder cabBlob = new(count + phantoms.Count);
        foreach (string cab in cabs)
        {
            cabBlob.Add(cab);
        }
        foreach (string phantom in phantoms)
        {
            cabBlob.Add(phantom);
        }

        // Distinct chunk files, first-seen order over the sorted entries (deterministic).
        Dictionary<string, int> fileIdOf = new(StringComparer.OrdinalIgnoreCase);
        BlobBuilder fileBlob = new(64);
        int[] fileIndex = new int[count];

        BlobBuilder nameBlob = new(count);
        BlobBuilder pathBlob = new(count);
        int[] pathStarts = new int[count + 1];
        List<int> classFlat = new();
        int[] classStarts = new int[count + 1];
        List<int> depsFlat = new();
        int[] depStarts = new int[count + 1];
        for (int id = 0; id < count; id++)
        {
            CabMap.Entry entry = entries[cabs[id]];
            if (!fileIdOf.TryGetValue(entry.RelativePath, out int fileId))
            {
                fileIdOf[entry.RelativePath] = fileId = fileIdOf.Count;
                fileBlob.Add(entry.RelativePath);
            }
            fileIndex[id] = fileId;
            nameBlob.Add(entry.EntryFileName);
            pathStarts[id + 1] = pathStarts[id] + entry.ContainerPaths.Count;
            foreach (string path in entry.ContainerPaths)
            {
                pathBlob.Add(path);
            }
            classStarts[id + 1] = classStarts[id] + entry.ClassIds.Count;
            classFlat.AddRange(entry.ClassIds);
            depStarts[id + 1] = depStarts[id] + entry.Dependencies.Count;
            foreach (string dep in entry.Dependencies)
            {
                depsFlat.Add(idOf[dep]);
            }
        }

        int[] depFlatArray = depsFlat.ToArray();
        (int[] reverseStarts, int[] reverseFlat) = BuildReverse(count, phantoms.Count, depStarts, depFlatArray);

        return new CabTable
        {
            BaseFolder = baseFolder,
            Count = count,
            PhantomCount = phantoms.Count,
            CabBlob = cabBlob.Blob(),
            CabOffsets = cabBlob.Offsets(),
            DistinctFileBlob = fileBlob.Blob(),
            DistinctFileOffsets = fileBlob.Offsets(),
            FileIndex = fileIndex,
            EntryFileNameBlob = nameBlob.Blob(),
            EntryFileNameOffsets = nameBlob.Offsets(),
            ContainerPathBlob = pathBlob.Blob(),
            ContainerPathOffsets = pathBlob.Offsets(),
            ContainerPathStarts = pathStarts,
            ClassIdsFlat = classFlat.ToArray(),
            ClassIdStarts = classStarts,
            DependenciesFlat = depFlatArray,
            DependencyStarts = depStarts,
            ReverseFlat = reverseFlat,
            ReverseStarts = reverseStarts,
        };
    }

    private sealed class BlobBuilder
    {
        private readonly MemoryStream _bytes = new();
        private readonly List<int> _offsets;

        public BlobBuilder(int expected)
        {
            _offsets = new List<int>(expected + 1) { 0 };
        }

        public void Add(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            _bytes.Write(utf8, 0, utf8.Length);
            _offsets.Add((int)_bytes.Length);
        }

        public byte[] Blob() => _bytes.ToArray();
        public int[] Offsets() => _offsets.ToArray();
    }

    // ── RCM6 serialization ────────────────────────────────────────────────────
    //
    // Layout (little-endian throughout; this is a same-machine cache format, not an
    // interchange format):
    //   u32 magic "RCM6", i32 version
    //   i32 baseLen, utf8 baseFolder (ABSOLUTE game root -- the map file itself is
    //     location-independent: copy it anywhere and it behaves identically. Moving the
    //     GAME invalidates the cache; rebuild, never re-anchor.)
    //   i32 count, i32 phantomCount, i32 fileCount, i32 pathCount, i32 classTotal, i32 depTotal
    //   int32[count+phantomCount+1] cabOffsets,  i32 blobLen + bytes cabBlob
    //   int32[fileCount+1] fileOffsets,          i32 blobLen + bytes fileBlob
    //   int32[count] fileIndex
    //   int32[count+1] nameOffsets,              i32 blobLen + bytes nameBlob
    //   int32[count+1] pathStarts, int32[pathCount+1] pathOffsets, i32 blobLen + bytes pathBlob
    //   int32[count+1] classStarts, int32[classTotal] classFlat
    //   int32[count+1] depStarts,   int32[depTotal]   depFlat
    // The reverse adjacency is NOT stored: its rebuild is one counting pass at load (~3ms at
    // 549k edges), cheaper than the disk bytes and impossible to let drift out of sync.

    internal const uint Magic6 = 0x52434D36; // "RCM6"

    public void Save(string outPath)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);
        byte[] baseUtf8 = Encoding.UTF8.GetBytes(Path.GetFullPath(BaseFolder));

        using FileStream stream = new(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic6);
        writer.Write(6);
        writer.Write(baseUtf8.Length);
        writer.Write(baseUtf8);
        writer.Write(Count);
        writer.Write(PhantomCount);
        writer.Write(FileCount);
        writer.Write(ContainerPathOffsets.Length - 1);
        writer.Write(ClassIdsFlat.Length);
        writer.Write(DependenciesFlat.Length);

        WriteInts(writer, CabOffsets);
        WriteBlob(writer, CabBlob);
        WriteInts(writer, DistinctFileOffsets);
        WriteBlob(writer, DistinctFileBlob);
        WriteInts(writer, FileIndex);
        WriteInts(writer, EntryFileNameOffsets);
        WriteBlob(writer, EntryFileNameBlob);
        WriteInts(writer, ContainerPathStarts);
        WriteInts(writer, ContainerPathOffsets);
        WriteBlob(writer, ContainerPathBlob);
        WriteInts(writer, ClassIdStarts);
        WriteInts(writer, ClassIdsFlat);
        WriteInts(writer, DependencyStarts);
        WriteInts(writer, DependenciesFlat);
    }

    /// <summary>One sequential pass straight into the final arrays -- no whole-file intermediate
    /// buffer, no per-string parse. Throws <see cref="InvalidDataException"/> for anything that is
    /// not RCM6: a cabmap is a regenerable cache, so a format bump means rebuild, never a
    /// multi-format compatibility reader.</summary>
    public static CabTable Load(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

        Span<byte> header = stackalloc byte[8];
        if (stream.Length < header.Length)
        {
            throw new InvalidDataException($"'{path}' is not an RCM6 cabmap -- rebuild it (Build writes RCM6 only).");
        }
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic6)
        {
            throw new InvalidDataException($"'{path}' is not an RCM6 cabmap -- rebuild it (Build writes RCM6 only).");
        }

        int baseLen = ReadInt(stream);
        byte[] baseUtf8 = new byte[baseLen];
        stream.ReadExactly(baseUtf8);
        string baseFolder = Encoding.UTF8.GetString(baseUtf8);

        int count = ReadInt(stream);
        int phantomCount = ReadInt(stream);
        int fileCount = ReadInt(stream);
        int pathCount = ReadInt(stream);
        int classTotal = ReadInt(stream);
        int depTotal = ReadInt(stream);

        int[] cabOffsets = ReadInts(stream, count + phantomCount + 1);
        byte[] cabBlob = ReadBlob(stream);
        int[] fileOffsets = ReadInts(stream, fileCount + 1);
        byte[] fileBlob = ReadBlob(stream);

        // The map is location-independent (the base is absolute), so the only way this probe can
        // come up empty is the GAME having moved or shrunk since the scan. Every downstream miss
        // would be silent (scopes match nothing, closures resolve to zero files), so refuse the
        // stale cache loudly: with entries present, at least one chunk file must still exist.
        if (fileCount > 0)
        {
            bool anyChunkExists = false;
            for (int fileId = 0; fileId < fileCount && !anyChunkExists; fileId++)
            {
                string relative = Encoding.UTF8.GetString(fileBlob, fileOffsets[fileId], fileOffsets[fileId + 1] - fileOffsets[fileId]);
                anyChunkExists = File.Exists(Path.Combine(baseFolder, relative));
            }
            if (!anyChunkExists)
            {
                throw new InvalidDataException(
                    $"'{path}' indexes chunk files under '{baseFolder}', but none of its {fileCount} chunk files exist "
                    + "there anymore. The game folder was moved or the cache is stale -- rebuild the map against the "
                    + "game's current location.");
            }
        }

        int[] fileIndex = ReadInts(stream, count);
        int[] nameOffsets = ReadInts(stream, count + 1);
        byte[] nameBlob = ReadBlob(stream);
        int[] pathStarts = ReadInts(stream, count + 1);
        int[] pathOffsets = ReadInts(stream, pathCount + 1);
        byte[] pathBlob = ReadBlob(stream);
        int[] classStarts = ReadInts(stream, count + 1);
        int[] classFlat = ReadInts(stream, classTotal);
        int[] depStarts = ReadInts(stream, count + 1);
        int[] depFlat = ReadInts(stream, depTotal);

        (int[] reverseStarts, int[] reverseFlat) = BuildReverse(count, phantomCount, depStarts, depFlat);

        return new CabTable
        {
            BaseFolder = baseFolder,
            Count = count,
            PhantomCount = phantomCount,
            CabBlob = cabBlob,
            CabOffsets = cabOffsets,
            DistinctFileBlob = fileBlob,
            DistinctFileOffsets = fileOffsets,
            FileIndex = fileIndex,
            EntryFileNameBlob = nameBlob,
            EntryFileNameOffsets = nameOffsets,
            ContainerPathBlob = pathBlob,
            ContainerPathOffsets = pathOffsets,
            ContainerPathStarts = pathStarts,
            ClassIdsFlat = classFlat,
            ClassIdStarts = classStarts,
            DependenciesFlat = depFlat,
            DependencyStarts = depStarts,
            ReverseFlat = reverseFlat,
            ReverseStarts = reverseStarts,
        };
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
        => writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));

    private static void WriteBlob(BinaryWriter writer, byte[] blob)
    {
        writer.Write(blob.Length);
        writer.Write(blob);
    }

    private static int ReadInt(FileStream stream)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static int[] ReadInts(FileStream stream, int count)
    {
        int[] values = new int[count];
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static byte[] ReadBlob(FileStream stream)
    {
        byte[] blob = new byte[ReadInt(stream)];
        stream.ReadExactly(blob);
        return blob;
    }
}
