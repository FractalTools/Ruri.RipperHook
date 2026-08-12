using System.Globalization;
using Newtonsoft.Json;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// Resolves a VFS streaming map name (e.g. "base01_lv002") into the load-file set + bundle-granular
/// load filter the shared export flow consumes, plus the placement manifest a render-side scene
/// assembler needs to rebuild the map. Headless equivalent of the Blender addon's interactive
/// discovery pipeline: discover placements -> keep the best available LOD per instance ->
/// resolve mesh/material container paths to their hosting CABs -> expand to the load-file closure.
/// </summary>
internal static class SceneSeedResolver
{
    /// <summary>One placeable scene placement, as written into the manifest. Rotation is the Unity
    /// quaternion (x,y,z,w); transform fields are the resolved values from EndfieldSceneBridge
    /// (ECS blob LocalToWorld / FBPropertyBytesData pose / bounds center).</summary>
    internal sealed record Placement(
        string AssetPath, string EntityName, string SourceChunk,
        float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz,
        string[] MaterialAssetPaths);

    /// <summary>Which piece of a map to read: the world rect (<see cref="MinX"/>, <see cref="MinZ"/>)..
    /// (<see cref="MaxX"/>, <see cref="MaxZ"/>), restricted to <see cref="SceneStateIds"/> (empty = every
    /// state the map ships). An infinite rect is the whole map, which is the default.</summary>
    internal sealed record SceneWindow(double MinX, double MinZ, double MaxX, double MaxZ, int[] SceneStateIds)
    {
        internal static SceneWindow WholeMap { get; } = new(
            double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity, []);

        internal SceneWindow Scaled(double scale)
        {
            double centreX = (MinX + MaxX) * 0.5;
            double centreZ = (MinZ + MaxZ) * 0.5;
            double halfX = (MaxX - MinX) * 0.5 * scale;
            double halfZ = (MaxZ - MinZ) * 0.5 * scale;
            return this with
            {
                MinX = centreX - halfX, MaxX = centreX + halfX,
                MinZ = centreZ - halfZ, MaxZ = centreZ + halfZ,
            };
        }
    }

    /// <summary>
    /// Resolves the window from the two options that can state one. <c>--scene-landmark</c> names a place
    /// the game itself publishes a rect for ("map01_lv007"), optionally scaled and restricted to scene
    /// states: <c>&lt;levelId&gt;[,&lt;scale&gt;[,&lt;sceneStateId&gt;...]]</c>. <c>--scene-window</c>
    /// states the rect outright: <c>&lt;minX&gt;,&lt;minZ&gt;,&lt;maxX&gt;,&lt;maxZ&gt;[,&lt;sceneStateId&gt;...]</c>.
    /// Neither is the whole map.
    /// </summary>
    internal static SceneWindow ResolveWindow(string? landmarkSpec, string? rectSpec, string[] vfsRoots)
    {
        if (landmarkSpec is { Length: > 0 } && rectSpec is { Length: > 0 })
        {
            throw new ArgumentException("--scene-landmark and --scene-window state the same thing; pass one.");
        }
        if (landmarkSpec is { Length: > 0 })
        {
            return FromLandmark(landmarkSpec, vfsRoots);
        }
        if (rectSpec is not { Length: > 0 })
        {
            return SceneWindow.WholeMap;
        }
        string[] fields = Fields(rectSpec);
        if (fields.Length < 4)
        {
            throw new ArgumentException(
                $"--scene-window '{rectSpec}' needs at least <minX>,<minZ>,<maxX>,<maxZ>, optionally followed by scene state ids.");
        }
        return new SceneWindow(Number(fields[0], rectSpec), Number(fields[1], rectSpec),
            Number(fields[2], rectSpec), Number(fields[3], rectSpec), Integers(fields[4..], rectSpec));
    }

    private static SceneWindow FromLandmark(string spec, string[] vfsRoots)
    {
        ColumnTable places = Read(LandmarksDataset, vfsRoots);
        Utf8Column levelIds = Text(places, "levelId");
        string[] fields = Fields(spec);
        string levelId = fields[0];
        int row = -1;
        for (int index = 0; index < places.RowCount; index++)
        {
            if (levelIds.Text(index).Equals(levelId, StringComparison.OrdinalIgnoreCase))
            {
                row = index;
                break;
            }
        }
        if (row < 0)
        {
            IEnumerable<string> known = Enumerable.Range(0, places.RowCount).Select(levelIds.Text);
            throw new ArgumentException(
                $"--scene-landmark '{levelId}' is not a place the game's own map UI lists. " +
                $"It lists: {string.Join(", ", known)}");
        }
        double scale = fields.Length > 1 ? Number(fields[1], spec) : 1.0;
        SceneWindow window = new(
            Real(places, "minX")[row], Real(places, "minZ")[row],
            Real(places, "maxX")[row], Real(places, "maxZ")[row],
            Integers(fields[2..], spec));
        return scale == 1.0 ? window : window.Scaled(scale);
    }

    // The datasets the active game publishes for its streaming scenes. Ids, not entry points:
    // the game hook registers what it can answer and this asks by name, so a game that ships no
    // streaming scenes simply publishes none and --export-scene says so.
    private const string LandmarksDataset = "endfield.scene.landmarks";
    private const string PlacementsDataset = "endfield.scene.placements";
    private const string PlacementMaterialsDataset = "endfield.scene.placement_materials";
    private const string PlacementCountsDataset = "endfield.scene.placement_counts";
    private const string SeedPathsDataset = "endfield.scene.seed_paths";

    /// <summary>One dataset read, with the "no game hook" case worded as the action that fixes it
    /// (the registry's own message only knows that nothing is published).</summary>
    private static ColumnTable Read(string datasetId, string[] args)
    {
        try
        {
            return Datasets.Table(datasetId, args, CancellationToken.None).Table;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"No game hook publishing '{datasetId}' is active — pass --hook with a VFS-game "
                + $"hook id (e.g. EndField_1.4.4). ({exception.Message})", exception);
        }
    }

    private static Utf8Column Text(ColumnTable table, string column) => (Utf8Column)table[column];

    private static double[] Real(ColumnTable table, string column) => ((RealColumn)table[column]).Values;

    private static string[] Fields(string spec)
        => spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static double Number(string field, string spec)
        => double.TryParse(field, out double value) ? value
            : throw new ArgumentException($"'{spec}': '{field}' is not a number.");

    private static int[] Integers(string[] fields, string spec)
    {
        int[] values = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], out values[i]))
            {
                throw new ArgumentException($"'{spec}': '{fields[i]}' is not a scene state id.");
            }
        }
        return values;
    }

    /// <summary>VFS root search order — hot-update overlay first, then base client.</summary>
    private static string[] VfsRoots(string gameRoot) =>
    [
        Path.Combine(gameRoot, "Endfield_Data", "Persistent", "VFS"),
        Path.Combine(gameRoot, "Endfield_Data", "StreamingAssets", "VFS"),
    ];

    /// <summary>
    /// Discover one streaming window's importable placements (reduced game-side: transform-verified,
    /// best available LOD per instance), resolve their seed container paths to hosting CABs, and expand
    /// to the load-file closure -- the seed set the shared ImportCabs export flow consumes.
    /// The window is the game's own: the world rect a named place is published with, gated by scene
    /// state -- see ResolveWindow and EndfieldSceneBridge.DiscoverScenePlacements. No window is the
    /// whole map.
    /// </summary>
    internal static (string[] LoadFiles, HashSet<string> LoadFilterFileNames, List<Placement> Placements)
        Resolve(CabTable table, string gameRoot, string mapName, string? landmarkSpec, string? rectSpec)
    {
        string[] vfsRoots = VfsRoots(gameRoot);
        SceneWindow window = ResolveWindow(landmarkSpec, rectSpec, vfsRoots);
        // Reduced game-side: the rows ARE the importable placements, and seed_paths is already the
        // distinct mesh+material container path set an import needs. The four reads share one
        // argument list, which is also the producer's discovery cache key -- so the window is
        // discovered once and projected four ways.
        string[] args = [mapName,
            window.MinX.ToString(CultureInfo.InvariantCulture),
            window.MinZ.ToString(CultureInfo.InvariantCulture),
            window.MaxX.ToString(CultureInfo.InvariantCulture),
            window.MaxZ.ToString(CultureInfo.InvariantCulture),
            string.Join(',', window.SceneStateIds), "1", .. vfsRoots];

        ColumnTable placements = Read(PlacementsDataset, args);
        ColumnTable materials = Read(PlacementMaterialsDataset, args);
        ColumnTable counts = Read(PlacementCountsDataset, args);

        // Materials are normalized as a child table keyed by the parent's row index.
        List<string>[] materialsByPlacement = new List<string>[placements.RowCount];
        double[] owner = Real(materials, "placement");
        Utf8Column materialPath = Text(materials, "path");
        for (int index = 0; index < materials.RowCount; index++)
        {
            int placement = (int)owner[index];
            (materialsByPlacement[placement] ??= new List<string>()).Add(materialPath.Text(index));
        }

        Utf8Column assetPath = Text(placements, "assetPath");
        Utf8Column entityName = Text(placements, "entityName");
        Utf8Column sourceChunk = Text(placements, "sourceChunk");
        double[] px = Real(placements, "px"), py = Real(placements, "py"), pz = Real(placements, "pz");
        double[] qx = Real(placements, "qx"), qy = Real(placements, "qy");
        double[] qz = Real(placements, "qz"), qw = Real(placements, "qw");
        double[] sx = Real(placements, "sx"), sy = Real(placements, "sy"), sz = Real(placements, "sz");
        List<Placement> rows = new(placements.RowCount);
        for (int index = 0; index < placements.RowCount; index++)
        {
            rows.Add(new Placement(assetPath.Text(index), entityName.Text(index), sourceChunk.Text(index),
                (float)px[index], (float)py[index], (float)pz[index],
                (float)qx[index], (float)qy[index], (float)qz[index], (float)qw[index],
                (float)sx[index], (float)sy[index], (float)sz[index],
                materialsByPlacement[index]?.ToArray() ?? []));
        }

        int total = (int)Real(counts, "total")[0];
        int noTransform = (int)Real(counts, "noTransform")[0];
        ColumnTable seedPaths = Read(SeedPathsDataset, args);
        Utf8Column seedPath = Text(seedPaths, "path");
        string[] allPaths = new string[seedPaths.RowCount];
        for (int index = 0; index < allPaths.Length; index++)
        {
            allPaths[index] = seedPath.Text(index);
        }

        Console.Error.WriteLine(
            $"[Ruri.CLI] scene '{mapName}' window x[{window.MinX}..{window.MaxX}] z[{window.MinZ}..{window.MaxZ}] " +
            $"states=[{(window.SceneStateIds.Length == 0 ? "all" : string.Join(' ', window.SceneStateIds))}]: " +
            $"{total} placements → {total - noTransform} with transform+asset → {rows.Count} after best-LOD selection");

        // Unmatched paths are silently dropped by the resolver.
        string[] seedCabs = CabMap.ResolveCabsForPaths(table, allPaths);

        CabClosure closure = new CabSelection { SeedCabNames = seedCabs }.Resolve(table);
        string[] loadFiles = closure.Files;
        HashSet<string> loadFilterFileNames = closure.LoadFilterFileNames;
        Console.Error.WriteLine(
            $"[Ruri.CLI] scene '{mapName}': {allPaths.Length} container paths → {seedCabs.Length} seed CABs → {loadFiles.Length} closure files");

        return (loadFiles, loadFilterFileNames, rows);
    }

    /// <summary>Manifest lands at the export root next to ExportedProject/ so a consumer gets
    /// assembly data + assets from one directory.</summary>
    internal static void WriteManifest(string exportPath, string mapName, List<Placement> placements)
    {
        string manifestPath = Path.Combine(exportPath, "ruri_scene_placements.json");
        var payload = new
        {
            map = mapName,
            placements = placements.Select(p => new
            {
                assetPath = p.AssetPath,
                entityName = p.EntityName,
                sourceChunk = p.SourceChunk,
                position = new[] { p.Px, p.Py, p.Pz },
                rotation = new[] { p.Qx, p.Qy, p.Qz, p.Qw },
                scale = new[] { p.Sx, p.Sy, p.Sz },
                materialAssetPaths = p.MaterialAssetPaths,
            }),
        };
        File.WriteAllText(manifestPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
        Console.Error.WriteLine($"[Ruri.CLI] scene manifest: {placements.Count} placements → {manifestPath}");
    }

}
