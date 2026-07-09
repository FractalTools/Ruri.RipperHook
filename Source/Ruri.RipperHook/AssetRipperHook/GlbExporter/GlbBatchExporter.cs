using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
// 恢复版旧 AR 与现行 AR 各有一个 PrefabHierarchyObject(FRAMEWORK.md §8);GlbModelExporter 消费的是 Prefabs 版。
using PrefabHierarchyObject = AssetRipper.Processing.Prefabs.PrefabHierarchyObject;

namespace Ruri.RipperHook.GlbExporter;

/// <summary>
/// Direct GLB batch export: every prefab hierarchy in the loaded game data (optionally filtered by
/// name/container-path regex) is written as one complete .glb via GlbModelExporter.ExportModel —
/// which the AR_GlbExporter hook replaces with the full skeleton/material/morph/animation builder.
/// Unlike the Unity-project export this never deletes the output directory; it only adds files.
/// </summary>
public static class GlbBatchExporter
{
    public static (int Exported, int Failed) ExportPrefabs(GameData gameData, string outputDirectory, Regex[] nameFilters)
    {
        Directory.CreateDirectory(outputDirectory);

        HashSet<PrefabHierarchyObject> seen = new();
        HashSet<string> usedFileNames = new(StringComparer.OrdinalIgnoreCase);
        int exported = 0;
        int failed = 0;

        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            if (asset.MainAsset is not PrefabHierarchyObject prefabHierarchy || !seen.Add(prefabHierarchy))
            {
                continue;
            }

            string prefabName = prefabHierarchy.Name.String;
            string originalPath = prefabHierarchy.Root.OriginalPath ?? string.Empty;
            if (nameFilters.Length > 0
                && !nameFilters.Any(filter => filter.IsMatch(prefabName) || (originalPath.Length > 0 && filter.IsMatch(originalPath))))
            {
                continue;
            }

            string fileName = UniqueFileName(usedFileNames, Sanitize(prefabName));
            string targetPath = Path.Combine(outputDirectory, fileName + ".glb");
            try
            {
                if (GlbModelExporter.ExportModel(prefabHierarchy.Assets, targetPath, isScene: false, LocalFileSystem.Instance))
                {
                    exported++;
                    Logger.Info(LogCategory.Export, $"[GLB] ({exported}) '{prefabName}' → {targetPath}");
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Logger.Error(LogCategory.Export, $"[GLB] prefab '{prefabName}' failed: {ex.Message}");
            }
        }

        Logger.Info(LogCategory.Export, $"[GLB] batch export done: {exported} exported, {failed} failed → {outputDirectory}");
        return (exported, failed);
    }

    private static string Sanitize(string name)
    {
        if (name.Length == 0)
        {
            return "prefab";
        }
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }
        return new string(buffer);
    }

    private static string UniqueFileName(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }
        for (int i = 2; ; i++)
        {
            string candidate = $"{name}_{i}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
