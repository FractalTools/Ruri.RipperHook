using System.Text.RegularExpressions;
using Ruri.RipperHook.Humanoid;
using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using SharpGLTF.Scenes;
using PrefabHierarchyObject = AssetRipper.Processing.Prefabs.PrefabHierarchyObject;

namespace Ruri.RipperHook.GlbExporter;

public static class GlbBatchExporter
{
    public static (int Exported, int Failed) ExportPrefabs(GameData gameData, string outputDirectory, Regex[] nameFilters)
    {
        Directory.CreateDirectory(outputDirectory);

        List<(IAnimationClip Clip, string Token)> muscleClips = CollectMuscleClips(gameData);

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

            string token = CharacterToken(prefabName, originalPath);
            List<IAnimationClip>? pool = token.Length == 0
                ? null
                : muscleClips.Where(entry => entry.Token == token).Select(entry => entry.Clip).ToList();

            string fileName = UniqueFileName(usedFileNames, Sanitize(prefabName));
            string targetPath = Path.Combine(outputDirectory, fileName + ".glb");
            try
            {
                SceneBuilder scene = RuriGlbSceneBuilder.Build(prefabHierarchy.Assets, isScene: false, pool);
                using (Stream fileStream = LocalFileSystem.Instance.File.Create(targetPath))
                {
                    scene.ToGltf2().WriteGLB(fileStream);
                }
                exported++;
                Logger.Info(LogCategory.Export, $"[GLB] ({exported}) '{prefabName}'"
                    + (pool is { Count: > 0 } ? $" +{pool.Count} humanoid clip(s)" : "") + $" → {targetPath}");
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

    private static List<(IAnimationClip, string)> CollectMuscleClips(GameData gameData)
    {
        List<(IAnimationClip, string)> result = new();
        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            if (asset is not IAnimationClip clip || !HumanoidClipGenericizer.HasMuscleCurves(clip))
            {
                continue;
            }
            string token = CharacterToken(clip.Name.String, clip.OriginalPath ?? string.Empty);
            result.Add((clip, token));
        }
        return result;
    }

    private static string CharacterToken(string name, string originalPath)
    {
        Match m = Regex.Match(name, @"chr_\d+_([a-z0-9]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.ToLowerInvariant();
        }
        m = Regex.Match(name, @"_actor_([a-z0-9]+)_", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.ToLowerInvariant();
        }
        m = Regex.Match(originalPath, @"/actor/[^/]+/([^/]+)/", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : string.Empty;
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
