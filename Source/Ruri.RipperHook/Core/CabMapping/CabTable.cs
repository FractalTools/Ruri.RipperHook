using System.Buffers.Binary;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// Columnar in-memory form of a cabmap: UTF-8 string blobs + offset tables per column, an
/// int-indexed dependency graph, and NO per-entry string/record materialization. This is the
/// load-time and interop-time optimum: the RCM4 on-disk format (see <see cref="CabMap"/>) stores
/// exactly these buffers, so loading is one File.ReadAllBytes plus a handful of Buffer.BlockCopy
/// slices -- no per-string length walk, no string allocation, no dependency-name re-hashing --
/// and a pythonnet caller receives the same buffers in one crossing and slices them at C speed.
///
/// Entry ids are [0, Count). Dependency edges may reference CABs that are not entries themselves
/// (a bundle can list a dependency the scan never found); those get PHANTOM ids in
/// [Count, Count+PhantomCount) with a name but no columns, preserving the classic
/// Dictionary-of-Entry BFS semantics exactly: a dangling dependency still appears in closure
/// OUTPUT (callers that then look it up simply miss, same as before) while contributing no edges.
///
/// Strings are materialized lazily and only where a consumer genuinely needs a System.String
/// (closure results, file-path resolution for the few dozen CABs of one closure, dictionary keys).
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

    public required byte[] RelativePathBlob { get; init; }
    public required int[] RelativePathOffsets { get; init; }  // Count + 1

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

    private Dictionary<string, int>? _cabToId;

    /// <summary>CAB name (real AND phantom) -> id, case-insensitive like the classic entries
    /// dictionary. Built lazily once (~40ms at 240k entries).</summary>
    public Dictionary<string, int> CabToId
    {
        get
        {
            if (_cabToId is null)
            {
                Dictionary<string, int> map = new(Count + PhantomCount, StringComparer.OrdinalIgnoreCase);
                for (int id = 0; id < Count + PhantomCount; id++)
                {
                    map[CabName(id)] = id;
                }
                _cabToId = map;
            }
            return _cabToId;
        }
    }

    public string CabName(int id) => Utf8(CabBlob, CabOffsets, id);
    public string RelativePath(int id) => Utf8(RelativePathBlob, RelativePathOffsets, id);
    public string EntryFileName(int id) => Utf8(EntryFileNameBlob, EntryFileNameOffsets, id);

    public int ContainerPathCount(int id) => ContainerPathStarts[id + 1] - ContainerPathStarts[id];

    public string ContainerPath(int id, int pathIndex)
        => Utf8(ContainerPathBlob, ContainerPathOffsets, ContainerPathStarts[id] + pathIndex);

    public ReadOnlySpan<int> ClassIds(int id)
        => ClassIdsFlat.AsSpan(ClassIdStarts[id], ClassIdStarts[id + 1] - ClassIdStarts[id]);

    public ReadOnlySpan<int> Dependencies(int id)
        => DependenciesFlat.AsSpan(DependencyStarts[id], DependencyStarts[id + 1] - DependencyStarts[id]);

    public int DependencyCount(int id) => DependencyStarts[id + 1] - DependencyStarts[id];

    private static string Utf8(byte[] blob, int[] offsets, int index)
        => Encoding.UTF8.GetString(blob, offsets[index], offsets[index + 1] - offsets[index]);

    /// <summary>Transitive dependency closure over the int graph (seed ids included), classic BFS
    /// semantics: phantom ids are visited/reported but contribute no edges.</summary>
    public int[] ClosureIds(IEnumerable<int> seedIds)
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
            if (id < Count)
            {
                foreach (int dep in Dependencies(id))
                {
                    queue.Enqueue(dep);
                }
            }
        }
        return order.ToArray();
    }

    private int[][]? _reverseAdjacency;

    /// <summary>dependency id -> ids of entries that directly depend on it, counting-sort built
    /// once per table (one pass to count, one to fill).</summary>
    public int[][] ReverseAdjacency
    {
        get
        {
            if (_reverseAdjacency is null)
            {
                int total = Count + PhantomCount;
                int[] counts = new int[total];
                foreach (int dep in DependenciesFlat)
                {
                    counts[dep]++;
                }
                int[][] reverse = new int[total][];
                for (int id = 0; id < total; id++)
                {
                    reverse[id] = counts[id] > 0 ? new int[counts[id]] : Array.Empty<int>();
                }
                int[] cursor = new int[total];
                for (int id = 0; id < Count; id++)
                {
                    foreach (int dep in Dependencies(id))
                    {
                        reverse[dep][cursor[dep]++] = id;
                    }
                }
                _reverseAdjacency = reverse;
            }
            return _reverseAdjacency;
        }
    }

    /// <summary>Columnar build from the classic dictionary shape -- the compatibility path for
    /// RCM3/RCM2/legacy maps (parsed by the old reader) and for Build's scan output.</summary>
    public static CabTable FromEntries(string baseFolder, Dictionary<string, CabMap.Entry> entries)
    {
        // Deterministic id order (matches the RCM3 writer's ordering).
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

        BlobBuilder relBlob = new(count);
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
            relBlob.Add(entry.RelativePath);
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

        return new CabTable
        {
            BaseFolder = baseFolder,
            Count = count,
            PhantomCount = phantoms.Count,
            CabBlob = cabBlob.Blob(),
            CabOffsets = cabBlob.Offsets(),
            RelativePathBlob = relBlob.Blob(),
            RelativePathOffsets = relBlob.Offsets(),
            EntryFileNameBlob = nameBlob.Blob(),
            EntryFileNameOffsets = nameBlob.Offsets(),
            ContainerPathBlob = pathBlob.Blob(),
            ContainerPathOffsets = pathBlob.Offsets(),
            ContainerPathStarts = pathStarts,
            ClassIdsFlat = classFlat.ToArray(),
            ClassIdStarts = classStarts,
            DependenciesFlat = depsFlat.ToArray(),
            DependencyStarts = depStarts,
        };
    }

    /// <summary>Classic dictionary materialization -- the compatibility bridge for the
    /// Dictionary-of-Entry consumers (CLI/GUI resolvers). Costs the string allocations the
    /// columnar model exists to avoid; only call on paths where seconds-scale work follows.</summary>
    public Dictionary<string, CabMap.Entry> ToEntries()
    {
        Dictionary<string, CabMap.Entry> entries = new(Count, StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < Count; id++)
        {
            int pathCount = ContainerPathCount(id);
            List<string> paths = new(pathCount);
            for (int p = 0; p < pathCount; p++)
            {
                paths.Add(ContainerPath(id, p));
            }
            ReadOnlySpan<int> classIds = ClassIds(id);
            List<int> classList = new(classIds.Length);
            foreach (int classId in classIds)
            {
                classList.Add(classId);
            }
            ReadOnlySpan<int> deps = Dependencies(id);
            List<string> depList = new(deps.Length);
            foreach (int dep in deps)
            {
                depList.Add(CabName(dep));
            }
            entries[CabName(id)] = new CabMap.Entry(
                RelativePath(id), EntryFileName(id), depList, classList, paths);
        }
        return entries;
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

    // ── RCM4 serialization ────────────────────────────────────────────────────
    //
    // Layout (little-endian throughout; this is a same-machine cache format, not an
    // interchange format):
    //   u32 magic "RCM4", i32 version
    //   i32 baseLen, utf8 baseFolder (relative to the map file's directory)
    //   i32 count, i32 phantomCount, i32 pathCount, i32 classTotal, i32 depTotal
    //   int32[count+phantomCount+1] cabOffsets,  i32 blobLen + bytes cabBlob
    //   int32[count+1] relOffsets,               i32 blobLen + bytes relBlob
    //   int32[count+1] nameOffsets,              i32 blobLen + bytes nameBlob
    //   int32[count+1] pathStarts, int32[pathCount+1] pathOffsets, i32 blobLen + bytes pathBlob
    //   int32[count+1] classStarts, int32[classTotal] classFlat
    //   int32[count+1] depStarts,   int32[depTotal]   depFlat

    internal const uint Magic4 = 0x52434D34; // "RCM4"

    public void Save(string outPath)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);
        string relativeBase = Path.GetRelativePath(outDir, BaseFolder);
        byte[] baseUtf8 = Encoding.UTF8.GetBytes(relativeBase);

        using FileStream stream = File.Create(outPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic4);
        writer.Write(4);
        writer.Write(baseUtf8.Length);
        writer.Write(baseUtf8);
        writer.Write(Count);
        writer.Write(PhantomCount);
        writer.Write(ContainerPathOffsets.Length - 1);
        writer.Write(ClassIdsFlat.Length);
        writer.Write(DependenciesFlat.Length);

        WriteInts(writer, CabOffsets);
        WriteBlob(writer, CabBlob);
        WriteInts(writer, RelativePathOffsets);
        WriteBlob(writer, RelativePathBlob);
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

    public static CabTable LoadRcm4(string path, byte[] file)
    {
        string mapDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        int cursor = 8; // magic + version already validated by the caller
        int baseLen = ReadInt(file, ref cursor);
        string storedBase = Encoding.UTF8.GetString(file, cursor, baseLen);
        cursor += baseLen;
        string baseFolder = Path.GetFullPath(Path.Combine(mapDir, storedBase));

        int count = ReadInt(file, ref cursor);
        int phantomCount = ReadInt(file, ref cursor);
        int pathCount = ReadInt(file, ref cursor);
        int classTotal = ReadInt(file, ref cursor);
        int depTotal = ReadInt(file, ref cursor);

        int[] cabOffsets = ReadInts(file, ref cursor, count + phantomCount + 1);
        byte[] cabBlob = ReadBlob(file, ref cursor);
        int[] relOffsets = ReadInts(file, ref cursor, count + 1);
        byte[] relBlob = ReadBlob(file, ref cursor);
        int[] nameOffsets = ReadInts(file, ref cursor, count + 1);
        byte[] nameBlob = ReadBlob(file, ref cursor);
        int[] pathStarts = ReadInts(file, ref cursor, count + 1);
        int[] pathOffsets = ReadInts(file, ref cursor, pathCount + 1);
        byte[] pathBlob = ReadBlob(file, ref cursor);
        int[] classStarts = ReadInts(file, ref cursor, count + 1);
        int[] classFlat = ReadInts(file, ref cursor, classTotal);
        int[] depStarts = ReadInts(file, ref cursor, count + 1);
        int[] depFlat = ReadInts(file, ref cursor, depTotal);

        return new CabTable
        {
            BaseFolder = baseFolder,
            Count = count,
            PhantomCount = phantomCount,
            CabBlob = cabBlob,
            CabOffsets = cabOffsets,
            RelativePathBlob = relBlob,
            RelativePathOffsets = relOffsets,
            EntryFileNameBlob = nameBlob,
            EntryFileNameOffsets = nameOffsets,
            ContainerPathBlob = pathBlob,
            ContainerPathOffsets = pathOffsets,
            ContainerPathStarts = pathStarts,
            ClassIdsFlat = classFlat,
            ClassIdStarts = classStarts,
            DependenciesFlat = depFlat,
            DependencyStarts = depStarts,
        };
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(int)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteBlob(BinaryWriter writer, byte[] blob)
    {
        writer.Write(blob.Length);
        writer.Write(blob);
    }

    private static int ReadInt(byte[] file, ref int cursor)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(cursor));
        cursor += sizeof(int);
        return value;
    }

    private static int[] ReadInts(byte[] file, ref int cursor, int count)
    {
        int[] values = new int[count];
        Buffer.BlockCopy(file, cursor, values, 0, count * sizeof(int));
        cursor += count * sizeof(int);
        return values;
    }

    private static byte[] ReadBlob(byte[] file, ref int cursor)
    {
        int length = ReadInt(file, ref cursor);
        byte[] blob = new byte[length];
        Buffer.BlockCopy(file, cursor, blob, 0, length);
        cursor += length;
        return blob;
    }
}
