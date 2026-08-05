using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;

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

    /// <summary>Which piece of a map to read: a disc of <see cref="Radius"/> chunk cells around
    /// (<see cref="CenterX"/>, <see cref="CenterY"/>), restricted to <see cref="SceneStateIds"/> (empty =
    /// every state the map ships). Parsed from --scene-window; see <see cref="Parse"/> for the default,
    /// which is the whole map.</summary>
    internal sealed record SceneWindow(int CenterX, int CenterY, int Radius, int[] SceneStateIds);

    /// <summary>
    /// Parses --scene-window. Empty/absent is the whole map. Otherwise
    /// <c>&lt;centerX&gt;,&lt;centerY&gt;,&lt;radius&gt;[,&lt;sceneStateId&gt;...]</c>, e.g.
    /// <c>-11,-2,1</c> for the 3x3 cells around (-11,-2) in every scene state, or <c>-11,-2,1,0</c> for
    /// the same cells in scene state 0 only.
    /// </summary>
    internal static SceneWindow Parse(string? spec)
    {
        if (spec is not { Length: > 0 })
        {
            return new SceneWindow(0, 0, int.MaxValue, []);
        }
        string[] fields = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3)
        {
            throw new ArgumentException(
                $"--scene-window '{spec}' needs at least <centerX>,<centerY>,<radius>, optionally followed by scene state ids.");
        }
        int[] values = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], out values[i]))
            {
                throw new ArgumentException($"--scene-window '{spec}': '{fields[i]}' is not an integer.");
            }
        }
        return new SceneWindow(values[0], values[1], values[2], values[3..]);
    }

    /// <summary>VFS root search order — hot-update overlay first, then base client.</summary>
    private static string[] VfsRoots(string gameRoot) =>
    [
        Path.Combine(gameRoot, "Endfield_Data", "Persistent", "VFS"),
        Path.Combine(gameRoot, "Endfield_Data", "StreamingAssets", "VFS"),
    ];

    /// <summary>
    /// Discover the placements inside one streaming window of the map, keep the best available LOD
    /// sibling per instance, resolve the mesh+material container paths to their hosting CABs, and expand
    /// to the load-file closure -- the seed set the shared ImportCabs export flow consumes.
    /// The window is the game's own (a disc of chunk cells around a centre, gated by scene state); see
    /// EndfieldSceneBridge.DiscoverScenePlacements. A radius past the map's grid extent is the whole map.
    /// </summary>
    internal static (string[] LoadFiles, HashSet<string> LoadFilterFileNames, List<Placement> Placements)
        Resolve(CabTable table, string gameRoot, string mapName, SceneWindow window)
    {
        if (GameBundleHook.DiscoverScenePlacements is not { } discover)
        {
            throw new InvalidOperationException(
                "No VFS game hook active — pass --hook with a VFS-game hook id (e.g. EndField_1.4.4).");
        }

        string[] vfsRoots = VfsRoots(gameRoot);
        int rawCount = 0;
        // A placement without a usable transform or a resolved asset path isn't geometry and
        // doesn't get placed (not "placed at the origin").
        var withTransform = new List<Placement>();
        foreach (var p in discover(vfsRoots, mapName, window.CenterX, window.CenterY, window.Radius, window.SceneStateIds))
        {
            rawCount++;
            if (p.HasTransform && p.AssetPath.Length > 0)
            {
                withTransform.Add(new Placement(p.AssetPath, p.EntityName, p.SourceChunk,
                    p.Px, p.Py, p.Pz, p.Qx, p.Qy, p.Qz, p.Qw, p.Sx, p.Sy, p.Sz, p.MaterialAssetPaths));
            }
        }
        List<Placement> rows = SelectBestLod(withTransform);

        Console.Error.WriteLine(
            $"[Ruri.CLI] scene '{mapName}' window centre=({window.CenterX},{window.CenterY}) radius={window.Radius} " +
            $"states=[{(window.SceneStateIds.Length == 0 ? "all" : string.Join(' ', window.SceneStateIds))}]: " +
            $"{rawCount} placements → {withTransform.Count} with transform+asset → {rows.Count} after best-LOD selection");

        // Distinct mesh paths ∪ distinct material paths, sorted, resolved to hosting CABs;
        // unmatched paths are silently dropped by the resolver.
        var meshPaths = rows.Select(p => p.AssetPath).ToHashSet(StringComparer.Ordinal);
        var materialPaths = rows.SelectMany(p => p.MaterialAssetPaths).ToHashSet(StringComparer.Ordinal);
        string[] allPaths = meshPaths.Union(materialPaths).OrderBy(p => p, StringComparer.Ordinal).ToArray();
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

    // ── best-available-LOD selection ────────────────────────────────────────────────────────────────

    private static readonly Regex LodSuffixRegex = new(@"_lod(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantSuffixRegex = new(@"_(?:lod\d+|col\d+_[a-z]+\d*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColSuffixRegex = new(@"_col\d+_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The identity of a mesh within a placement: the ##subname suffix of a multi-object
    /// FBX path, or the bare file stem, lowercased.</summary>
    private static string ExpectedMeshName(string assetPath)
    {
        int hashIndex = assetPath.IndexOf("##", StringComparison.Ordinal);
        if (hashIndex >= 0)
        {
            return assetPath[(hashIndex + 2)..].ToLowerInvariant();
        }
        string leaf = assetPath[(assetPath.LastIndexOf('/') + 1)..];
        int dotIndex = leaf.LastIndexOf('.');
        return (dotIndex >= 0 ? leaf[..dotIndex] : leaf).ToLowerInvariant();
    }

    /// <summary>LOD priority of a mesh name: lod0=0, lodN=N, unsuffixed=-1 (as good as lod0),
    /// collision meshes rank 1000 (picked only when nothing else exists in the group).</summary>
    private static int LodRank(string assetPath)
    {
        string name = ExpectedMeshName(assetPath);
        Match match = LodSuffixRegex.Match(name);
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value);
        }
        return ColSuffixRegex.IsMatch(name) ? 1000 : -1;
    }

    /// <summary>Group key (rounded position, variant-stripped stem) that identifies the parallel
    /// sibling entities placed for the SAME instance at different detail levels; position rounded to
    /// collapse float noise between identically-placed siblings.</summary>
    private static (double, double, double, string) LodGroupKey(Placement row)
    {
        string stem = VariantSuffixRegex.Replace(ExpectedMeshName(row.AssetPath), "");
        return (Math.Round(row.Px, 2), Math.Round(row.Py, 2), Math.Round(row.Pz, 2), stem);
    }

    /// <summary>Group placements into per-instance LOD-sibling sets and keep only the best-ranked
    /// member of each (first wins on rank ties); falls back to whatever detail level actually
    /// shipped when no lod0 sibling exists at all.</summary>
    private static List<Placement> SelectBestLod(List<Placement> rows)
    {
        var groupOrder = new List<(double, double, double, string)>();
        var groups = new Dictionary<(double, double, double, string), List<Placement>>();
        foreach (Placement row in rows)
        {
            var key = LodGroupKey(row);
            if (!groups.TryGetValue(key, out List<Placement>? members))
            {
                groups[key] = members = [];
                groupOrder.Add(key);
            }
            members.Add(row);
        }

        var result = new List<Placement>(groupOrder.Count);
        foreach (var key in groupOrder)
        {
            List<Placement> members = groups[key];
            Placement best = members[0];
            int bestRank = LodRank(best.AssetPath);
            for (int m = 1; m < members.Count; m++)
            {
                int rank = LodRank(members[m].AssetPath);
                if (rank < bestRank)
                {
                    best = members[m];
                    bestRank = rank;
                }
            }
            result.Add(best);
        }
        return result;
    }
}
