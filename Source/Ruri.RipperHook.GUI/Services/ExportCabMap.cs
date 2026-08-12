using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.GUI.Services;

internal sealed class ExportCabMap
{
    private CabTable? _table;

    public bool HasMap => _table is not null;
    public int CabCount => _table?.Count ?? 0;
    public string MapPath { get; private set; } = string.Empty;

    internal sealed record CabRow(string Cab, string RelativePath, IReadOnlyList<int> ClassIds, int DependencyCount, IReadOnlyList<string> ContainerPaths);

    public IReadOnlySet<int> AvailableClassIds
    {
        get
        {
            HashSet<int> ids = new();
            if (_table is { } table)
            {
                for (int id = 0; id < table.Count; id++)
                {
                    foreach (int classId in table.ClassIds(id))
                    {
                        ids.Add(classId);
                    }
                }
            }
            return ids;
        }
    }

    public void Clear()
    {
        _table = null;
        MapPath = string.Empty;
    }

    public CabTable? Table => _table;

    public CabRow RowAt(int id)
    {
        CabTable table = _table ?? throw new InvalidOperationException("No cabmap loaded.");
        int pathCount = table.ContainerPathCount(id);
        string[] paths = new string[pathCount];
        for (int i = 0; i < pathCount; i++)
        {
            paths[i] = table.ContainerPath(id, i);
        }
        return new CabRow(
            table.CabName(id), table.RelativePath(id), table.ClassIds(id).ToArray(),
            table.DependencyCount(id), paths);
    }

    public (string[] Files, HashSet<string> LoadFilterFileNames) ResolveScopedClosure(IEnumerable<string> seedCabs)
    {
        if (_table is not { } table)
        {
            return ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        CabClosure closure = new CabSelection { SeedCabNames = seedCabs.ToArray() }.Resolve(table);
        return (closure.Files, closure.LoadFilterFileNames);
    }

    public void Load(string path)
    {
        _table = CabMap.LoadTable(path);
        MapPath = Path.GetFullPath(path);
    }

    public string[] ResolveFilesByTypes(IReadOnlySet<int> targetClassIds)
        => _table is { } table
            ? new CabSelection { ClassIds = targetClassIds }.Resolve(table).Files
            : [];

    public static int Build(string rootFolder, string outPath) => CabMap.Build(rootFolder, outPath);
}
