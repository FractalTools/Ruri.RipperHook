using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using SharpGLTF.Scenes;
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

        // 预扫加载 scope 里所有 humanoid muscle clip(明文命名、无 path_hash);按角色 token 分组,
        // 只把匹配角色的 avatar-portable clip 烘进它的 GLB(逐角色导出时 scope 本就单角色)。
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

    /// <summary>
    /// Every loaded AnimationClip that carries humanoid muscle/root float curves (plaintext attribute
    /// names — no irreversible path hash), tagged with the character token parsed from its name/path so
    /// a clip is only baked onto the matching character's avatar.
    /// </summary>
    private static List<(IAnimationClip, string)> CollectMuscleClips(GameData gameData)
    {
        List<(IAnimationClip, string)> result = new();
        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            if (asset is not IAnimationClip clip || !HasMuscleCurves(clip))
            {
                continue;
            }
            string token = CharacterToken(clip.Name.String, clip.OriginalPath ?? string.Empty);
            result.Add((clip, token));
        }
        return result;
    }

    private static bool HasMuscleCurves(IAnimationClip clip)
    {
        foreach (IFloatCurve floatCurve in clip.FloatCurves_C74)
        {
            string attribute = floatCurve.Attribute.String;
            if (AvatarMuscleReferential.IsMuscleAttribute(attribute) || AvatarMuscleReferential.IsRootAttribute(attribute))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parse the character token that ties a clip to a model: the name after the <c>chr_&lt;id&gt;_</c>
    /// prefix (<c>chr_0004_pelica_postmodel</c> → <c>pelica</c>), or the <c>actor/&lt;group&gt;/&lt;token&gt;/</c>
    /// path segment (<c>A_actor_pelica_battle…</c> / <c>…/actor/girl/pelica/…</c>). Empty when unresolved.
    /// </summary>
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
