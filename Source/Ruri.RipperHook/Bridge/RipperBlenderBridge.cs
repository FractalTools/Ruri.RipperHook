using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using Ruri.Hook.Config;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Public, self-contained entry point for an in-process caller (pythonnet, hosted inside Blender) to
/// build/load a cabmap, browse its rows, and pull a selection's fully-resolved model/animation/material/
/// texture data straight into memory. Every method here composes existing, already-working pieces
/// (<see cref="CabMap"/>, AssetRipper's own <see cref="ExportHandler"/>, <see cref="InMemoryFileSystem"/>)
/// — no new export/serialization logic, no AOP hook. The only thing this class adds is: (1) a
/// cabmap-name-seeded scoped load, and (2) pairing the exporter's own ".meta" sidecars back into
/// GUID-keyed dictionaries, exactly the shape a GUID-keyed consumer (a Unity-YAML parser) already expects.
/// </summary>
public static class RipperBlenderBridge
{
    private static bool _loggingConfigured;

    /// <summary>
    /// One-call bootstrap: assembly resolver, a stderr logger sink (AssetRipper logging is a black hole
    /// with no sink attached), and every hook in <paramref name="enabledHookIds"/> (e.g. "EndField_1.3.3").
    /// Safe to call more than once per process — the resolver install and hook application are both
    /// idempotent; the logger sink is only added once.
    /// </summary>
    public static void Initialize(IEnumerable<string> enabledHookIds)
    {
        Bootstrap.InstallAssemblyResolver();

        if (!_loggingConfigured)
        {
            _loggingConfigured = true;
            Logger.Clear();
            Logger.Add(new BridgeLogger { MinLevel = LogType.Info });
        }

        HookConfig config = new();
        foreach (string id in enabledHookIds)
        {
            config.EnabledHooks.Add(id);
        }
        Bootstrap.ApplyHooks(config);
    }

    /// <summary>Scan <paramref name="gameRoot"/> and write a fresh cabmap to <paramref name="outPath"/>.</summary>
    public static int BuildCabMap(string gameRoot, string outPath) => CabMap.Build(gameRoot, outPath);

    /// <summary>Load an existing cabmap file. Returns an opaque handle for <see cref="EnumerateRows"/>/<see cref="ImportCabs"/>.</summary>
    public static CabMapHandle LoadCabMap(string cabMapPath)
    {
        (string baseFolder, Dictionary<string, CabMap.Entry> entries) = CabMap.Load(cabMapPath);
        return new CabMapHandle(cabMapPath, baseFolder, entries);
    }

    /// <summary>Every CAB in the map, projected to the flat browsable row shape (Name/Container/Type/Source/Deps).</summary>
    public static CabRowDto[] EnumerateRows(CabMapHandle map)
    {
        ArgumentNullException.ThrowIfNull(map);
        CabRowDto[] rows = new CabRowDto[map.Entries.Count];
        int i = 0;
        foreach ((string cab, CabMap.Entry entry) in map.Entries)
        {
            rows[i++] = new CabRowDto(
                Cab: cab,
                Name: DisplayName(entry.ContainerPaths),
                Container: string.Join("  |  ", entry.ContainerPaths),
                TypeNames: TypeNames(entry.ClassIds),
                Source: entry.RelativePath,
                DependencyCount: entry.Dependencies.Count);
        }
        return rows;
    }

    /// <summary>
    /// Resolve the seed CABs' full dependency closure, load exactly those bundles, run AssetRipper's real
    /// Unity-project exporter against an <see cref="InMemoryFileSystem"/> (the same exporter that backs
    /// the CLI's --export and the GUI's project export — byte-identical output, just memory-backed instead
    /// of disk-backed), and return the result partitioned into GUID-keyed YAML text / PNG bytes.
    /// </summary>
    public static ClosureResult ImportCabs(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seedCabNames);

        (string[] closureFiles, HashSet<string> loadFilterFileNames) =
            CabMap.ResolveScopedClosure(map.BaseFolder, map.Entries, seedCabNames);
        if (closureFiles.Length == 0)
        {
            return ClosureResult.Empty;
        }

        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
        ExportHandler handler = new(settings);

        GameData gameData;
        GameBundleHook.LoadIncludeFile = loadFilterFileNames.Count > 0 ? name => loadFilterFileNames.Contains(name) : null;
        try
        {
            gameData = handler.LoadAndProcess(closureFiles, LocalFileSystem.Instance);
        }
        finally
        {
            GameBundleHook.LoadIncludeFile = null;
        }

        InMemoryFileSystem memoryFileSystem = new();
        handler.Export(gameData, "mem:/out", memoryFileSystem);

        return Partition(memoryFileSystem.Files);
    }

    private static ClosureResult Partition(IReadOnlyDictionary<string, byte[]> files)
    {
        Dictionary<string, string> documents = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> textures = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> other = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = new();
        UTF8Encoding utf8 = new(false);

        foreach ((string path, byte[] bytes) in files)
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue; // paired below, from the content file's side
            }
            if (!files.TryGetValue(path + ".meta", out byte[]? metaBytes))
            {
                other[path] = bytes;
                continue;
            }
            string? guid = ExtractGuid(metaBytes, utf8);
            if (guid is null)
            {
                other[path] = bytes;
                continue;
            }
            if (IsPng(bytes))
            {
                textures[guid] = bytes;
                continue;
            }

            documents[guid] = utf8.GetString(bytes);
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(guid);
            }
        }

        return new ClosureResult(documents, textures, other, roots.ToArray());
    }

    private static readonly Regex GuidPattern = new(@"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

    private static string? ExtractGuid(byte[] metaBytes, UTF8Encoding utf8)
    {
        Match match = GuidPattern.Match(utf8.GetString(metaBytes));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>First container path's leaf name, "(+N)" suffixed when the CAB has more than one; the raw CAB hash has already been substituted by the caller when there are none.</summary>
    private static string DisplayName(IReadOnlyList<string> containerPaths)
    {
        if (containerPaths.Count == 0)
        {
            return string.Empty;
        }
        string path = containerPaths[0];
        int slash = path.LastIndexOf('/');
        string leaf = slash >= 0 ? path[(slash + 1)..] : path;
        return containerPaths.Count > 1 ? $"{leaf} (+{containerPaths.Count - 1})" : leaf;
    }

    private static string TypeNames(IReadOnlyList<int> classIds)
    {
        List<string> names = new();
        foreach (int id in classIds)
        {
            if (id == (int)ClassIDType.AssetBundle)
            {
                continue;
            }
            names.Add(Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : id.ToString());
        }
        return names.Count > 0 ? string.Join(", ", names) : nameof(ClassIDType.AssetBundle);
    }
}

/// <summary>Opaque handle to a loaded cabmap — the base folder it was built from plus its CAB entries.</summary>
public sealed class CabMapHandle
{
    public string CabMapPath { get; }
    public string BaseFolder { get; }
    public Dictionary<string, CabMap.Entry> Entries { get; }

    internal CabMapHandle(string cabMapPath, string baseFolder, Dictionary<string, CabMap.Entry> entries)
    {
        CabMapPath = cabMapPath;
        BaseFolder = baseFolder;
        Entries = entries;
    }
}

/// <summary>One browsable row — mirrors the WinForms "Virtual Asset List" columns 1:1.</summary>
public sealed record CabRowDto(string Cab, string Name, string Container, string TypeNames, string Source, int DependencyCount);

/// <summary>
/// The in-memory import payload for a resolved selection: Unity-project YAML text and texture PNG bytes,
/// both GUID-keyed (matching the {fileID, guid} cross-references already embedded in the YAML text
/// itself), plus anything else the exporter wrote that isn't a recognized text/image pair, and the GUIDs
/// of the top-level (.prefab) assets that should actually be imported.
/// </summary>
public sealed record ClosureResult(
    IReadOnlyDictionary<string, string> Documents,
    IReadOnlyDictionary<string, byte[]> Textures,
    IReadOnlyDictionary<string, byte[]> OtherFiles,
    string[] Roots)
{
    public static ClosureResult Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>(),
        new Dictionary<string, byte[]>(),
        Array.Empty<string>());
}
