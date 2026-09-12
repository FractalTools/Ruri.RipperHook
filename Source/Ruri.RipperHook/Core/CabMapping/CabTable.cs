using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

public sealed class CabTable
{
    public required string BaseFolder { get; init; }

    public required int Count { get; init; }
    public required int PhantomCount { get; init; }

    public required byte[] CabBlob { get; init; }
    public required int[] CabOffsets { get; init; }
    public required byte[] DistinctFileBlob { get; init; }
    public required int[] DistinctFileOffsets { get; init; }    public required int[] FileIndex { get; init; }
    public required byte[] EntryFileNameBlob { get; init; }
    public required int[] EntryFileNameOffsets { get; init; }
    public required byte[] ContainerPathBlob { get; init; }
    public required int[] ContainerPathOffsets { get; init; }    public required int[] ContainerPathStarts { get; init; }
    public required int[] ClassIdsFlat { get; init; }
    public required int[] ClassIdStarts { get; init; }
    public required int[] DependenciesFlat { get; init; }    public required int[] DependencyStarts { get; init; }
    public required int[] ReverseFlat { get; init; }    public required int[] ReverseStarts { get; init; }
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

    public ReadOnlySpan<int> Dependents(int id)
        => ReverseFlat.AsSpan(ReverseStarts[id], ReverseStarts[id + 1] - ReverseStarts[id]);

    private static string Utf8(byte[] blob, int[] offsets, int index)
        => Encoding.UTF8.GetString(blob, offsets[index], offsets[index + 1] - offsets[index]);

    private int _maxContainerPathUtf8Length = -1;

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

    public int[] ClosureIds(IEnumerable<int> seedIds) => Walk(seedIds, DependencyStarts, DependenciesFlat, Count);

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

    public static CabTable FromEntries(string baseFolder, IReadOnlyDictionary<string, CabMap.Entry> entries)
    {
        string[] cabs = entries.Keys.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();
        int count = cabs.Length;
        Dictionary<string, int> idOf = new(count, StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < count; id++)
        {
            idOf[cabs[id]] = id;
        }

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


    internal const uint Magic6 = 0x52434D36;
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
