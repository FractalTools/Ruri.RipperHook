using System.Globalization;
using System.Text;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using Newtonsoft.Json;
using Ruri.RipperHook.Bridge;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.CLI;

internal static class SceneSeedResolver
{
    internal sealed record Placement(
        string AssetPath, string EntityName, string SourceChunk,
        float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz,
        string[] MaterialAssetPaths);

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

    internal static SceneWindow ResolveWindow(string? landmarkSpec, string? rectSpec)
    {
        if (landmarkSpec is { Length: > 0 } && rectSpec is { Length: > 0 })
        {
            throw new ArgumentException("--scene-landmark and --scene-window state the same thing; pass one.");
        }
        if (landmarkSpec is { Length: > 0 })
        {
            return FromLandmark(landmarkSpec);
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

    private static SceneWindow FromLandmark(string spec)
    {
        ColumnTable places = Read(LandmarksDataset, []);
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
        // Both tails are optional -- "map02_lv002" alone means the place at the size the game
        // gives it, in every scene state it ships. Slicing past the end of a shorter spec threw,
        // so the plain form (the one the help text leads with) never worked at all.
        double scale = fields.Length > 1 ? Number(fields[1], spec) : 1.0;
        SceneWindow window = new(
            Real(places, "minX")[row], Real(places, "minZ")[row],
            Real(places, "maxX")[row], Real(places, "maxZ")[row],
            fields.Length > 2 ? Integers(fields[2..], spec) : []);
        return scale == 1.0 ? window : window.Scaled(scale);
    }

    private const string LandmarksDataset = "endfield.scene.landmarks";
    private const string PlacementsDataset = "endfield.scene.placements";
    private const string PlacementMaterialsDataset = "endfield.scene.placement_materials";
    private const string PlacementCountsDataset = "endfield.scene.placement_counts";
    private const string SeedPathsDataset = "endfield.scene.seed_paths";

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
                + $"hook id (e.g. Endfield_1.4.4). ({exception.Message})", exception);
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

    internal static (string[] LoadFiles, HashSet<string> LoadFilterFileNames, List<Placement> Placements)
        Resolve(CabTable table, string mapName, string? landmarkSpec, string? rectSpec)
    {
        SceneWindow window = ResolveWindow(landmarkSpec, rectSpec);
        List<string> args =
        [
            "map", mapName,
            "minX", window.MinX.ToString(CultureInfo.InvariantCulture),
            "minZ", window.MinZ.ToString(CultureInfo.InvariantCulture),
            "maxX", window.MaxX.ToString(CultureInfo.InvariantCulture),
            "maxZ", window.MaxZ.ToString(CultureInfo.InvariantCulture),
            // Which detail level to keep, the same word the dataset itself takes: 0 is the
            // highest the game authored. This used to say "lod0Only 1", a flag the dataset
            // stopped taking when it grew a level SELECTOR -- and since an unknown argument
            // is a hard error, every --export-scene run had been failing at resolve.
            "detailLevel", "0",
        ];
        foreach (int sceneState in window.SceneStateIds)
        {
            args.Add("sceneState");
            args.Add(sceneState.ToString(CultureInfo.InvariantCulture));
        }

        ColumnTable placements = Read(PlacementsDataset, [.. args]);
        ColumnTable materials = Read(PlacementMaterialsDataset, [.. args]);
        ColumnTable counts = Read(PlacementCountsDataset, [.. args]);

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
        int lodFiltered = (int)Real(counts, "lodFiltered")[0];
        ColumnTable seedPaths = Read(SeedPathsDataset, [.. args]);
        Utf8Column seedPath = Text(seedPaths, "path");
        string[] allPaths = new string[seedPaths.RowCount];
        for (int index = 0; index < allPaths.Length; index++)
        {
            allPaths[index] = seedPath.Text(index);
        }

        Console.Error.WriteLine(
            $"[Ruri.CLI] scene '{mapName}' window x[{window.MinX}..{window.MaxX}] z[{window.MinZ}..{window.MaxZ}] " +
            $"states=[{(window.SceneStateIds.Length == 0 ? "all" : string.Join(' ', window.SceneStateIds))}]: " +
            $"{total} placements → {total - noTransform} with transform+asset → {rows.Count} kept " +
            $"({noTransform} never geometry, {lodFiltered} a lower detail level of something already kept)");

        string[] seedCabs = CabMap.ResolveCabsForPaths(table, allPaths);

        CabClosure closure = new CabSelection { SeedCabNames = seedCabs }.Resolve(table);
        string[] loadFiles = closure.Files;
        HashSet<string> loadFilterFileNames = closure.LoadFilterFileNames;
        Console.Error.WriteLine(
            $"[Ruri.CLI] scene '{mapName}': {allPaths.Length} container paths → {seedCabs.Length} seed CABs → {loadFiles.Length} closure files");

        return (loadFiles, loadFilterFileNames, rows);
    }

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

    private const int MeshClassId = 43;
    private const int MaterialClassId = 21;

    internal sealed record SceneWriteResult(string ScenePath, int Placed, int Instantiated,
        int Unresolved, IReadOnlyList<string> UnresolvedPaths, IReadOnlyList<string> UnresolvedMaterials);

    /// <summary>
    /// Write the window's arrangement as a scene Unity can open, beside the assets it places.
    ///
    /// <para>The join from a placement to what the export made of it comes from
    /// <paramref name="index"/> — recorded off the live objects as they were serialized. It
    /// deliberately does NOT come from the files on disk: export file names are sanitized,
    /// de-suffixed and uniquified, so a name like "S_tree_hongshan_hardwood+1_001_02" cannot
    /// be recovered from the file that holds it.</para>
    /// </summary>
    internal static SceneWriteResult? WriteScene(string exportPath, string mapName,
        List<Placement> placements, ExportedAssetIndex index,
        IReadOnlyDictionary<string, ExportFilter.PrefabExport> prefabs)
    {
        string assetsRoot = Path.Combine(exportPath, "ExportedProject", "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            Console.Error.WriteLine(
                $"[Ruri.CLI] scene: no exported Assets tree under '{exportPath}' — nothing to write a scene against.");
            return null;
        }

        SceneDocument scene = new(mapName);
        Dictionary<string, long> groups = new(StringComparer.Ordinal);
        List<string> unresolved = new();
        HashSet<string> unresolvedSeen = new(StringComparer.Ordinal);
        List<string> unresolvedMaterials = new();
        HashSet<string> unresolvedMaterialsSeen = new(StringComparer.Ordinal);
        int placed = 0;
        int instantiated = 0;

        foreach (Placement placement in placements)
        {
            SceneDocument.Vec3 position = new(placement.Px, placement.Py, placement.Pz);
            SceneDocument.Quat rotation = new(placement.Qx, placement.Qy, placement.Qz, placement.Qw);
            SceneDocument.Vec3 scale = new(placement.Sx, placement.Sy, placement.Sz);

            // A prefab is instantiated whole; anything else is a mesh this scene references.
            if (prefabs.TryGetValue(ExportedAssetIndex.Normalize(placement.AssetPath),
                    out ExportFilter.PrefabExport? prefab))
            {
                scene.AddPrefabInstance(prefab.RootName, Group(scene, groups, placement.AssetPath, prefab.RootName),
                    position, rotation, scale, prefab.SourcePrefab, prefab.RootTransform);
                instantiated++;
                continue;
            }

            ExportedAssetIndex.Entry? resolved = index.Resolve(placement.AssetPath, MeshClassId);
            if (resolved is not ExportedAssetIndex.Entry mesh)
            {
                if (unresolvedSeen.Add(placement.AssetPath))
                {
                    unresolved.Add(placement.AssetPath);
                }
                continue;
            }

            List<MetaPtr> materials = new(placement.MaterialAssetPaths.Length);
            foreach (string materialPath in placement.MaterialAssetPaths)
            {
                if (index.Resolve(materialPath, MaterialClassId) is ExportedAssetIndex.Entry material)
                {
                    materials.Add(Pointer(material));
                }
                else if (unresolvedMaterialsSeen.Add(materialPath))
                {
                    unresolvedMaterials.Add(materialPath);
                }
            }

            scene.AddMeshObject(mesh.Name, Group(scene, groups, placement.AssetPath, mesh.Name),
                position, rotation, scale, Pointer(mesh), materials);
            placed++;
        }

        string sceneDirectory = Path.Combine(assetsRoot, "Scenes");
        Directory.CreateDirectory(sceneDirectory);
        string scenePath = Path.Combine(sceneDirectory, mapName + ".unity");
        File.WriteAllText(scenePath, scene.Build(), new UTF8Encoding(false));
        File.WriteAllText(scenePath + ".meta", SceneMeta(mapName), new UTF8Encoding(false));

        Console.Error.WriteLine(
            $"[Ruri.CLI] scene '{mapName}': {placed} mesh placement(s) + {instantiated} prefab instance(s) "
            + $"in {groups.Count} group(s) → {scenePath}");
        if (unresolved.Count > 0)
        {
            // Lost content, not a detail: these placements are simply absent from the scene.
            Console.Error.WriteLine(
                $"[Ruri.CLI] scene '{mapName}': !! {unresolved.Count} asset path(s) resolved to nothing exported "
                + $"and are MISSING from the scene: {string.Join(", ", unresolved.Take(5))}"
                + (unresolved.Count > 5 ? ", …" : string.Empty));
        }
        if (unresolvedMaterials.Count > 0)
        {
            Console.Error.WriteLine(
                $"[Ruri.CLI] scene '{mapName}': {unresolvedMaterials.Count} material path(s) resolved to nothing "
                + $"— those renderer slots are EMPTY: {string.Join(", ", unresolvedMaterials.Take(5))}"
                + (unresolvedMaterials.Count > 5 ? ", …" : string.Empty));
        }
        return new SceneWriteResult(scenePath, placed, instantiated, unresolved.Count, unresolved, unresolvedMaterials);
    }

    /// <summary>
    /// One empty parent per distinct asset. A window is 10^4 placements of a few hundred
    /// things; hung flat off the root, the hierarchy is unusable, and this is the same
    /// grouping the Blender side files an import under.
    /// </summary>
    private static long Group(SceneDocument scene, Dictionary<string, long> groups, string assetPath, string name)
    {
        if (!groups.TryGetValue(assetPath, out long group))
        {
            groups[assetPath] = group = scene.AddGroup(name, scene.RootTransformId);
        }
        return group;
    }

    private static MetaPtr Pointer(ExportedAssetIndex.Entry entry) =>
        new(entry.FileId, UnityGuid.Parse(entry.Guid), (AssetType)entry.ReferenceType);

    /// <summary>
    /// The scene's own .meta. Its guid is derived from the map id rather than drawn at
    /// random, so re-exporting the same scene keeps the same asset identity and whatever
    /// already referenced it keeps working.
    /// </summary>
    private static string SceneMeta(string mapName)
    {
        byte[] digest = System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes("ruri.scene:" + mapName));
        string guid = Convert.ToHexString(digest).ToLowerInvariant();
        return $"fileFormatVersion: 2\nguid: {guid}\nDefaultImporter:\n  externalObjects: {{}}\n"
            + "  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
    }
}
