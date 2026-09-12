using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass120_LoadAssetInfoSidecar
{
    public static void DoPass(PipelineState state)
    {
        string sidecarPath = SidecarBasePath(state.Options.LibraryPath) + ".assetinfo.json";
        if (!File.Exists(sidecarPath))
        {
            state.Log("    .assetinfo.json: missing, ShaderMapToAssets stays empty.");
            return;
        }

        AssetInfoRoot? root = JsonSerializer.Deserialize<AssetInfoRoot>(File.ReadAllText(sidecarPath), JsonOptions);
        if (root?.ShaderCodeToAssets == null) return;

        foreach (AssetInfoEntry entry in root.ShaderCodeToAssets)
        {
            if (string.IsNullOrWhiteSpace(entry.ShaderMapHash) || entry.Assets == null) continue;
            if (!state.ShaderMapToAssets.TryGetValue(entry.ShaderMapHash, out HashSet<string>? assets))
            {
                assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                state.ShaderMapToAssets[entry.ShaderMapHash] = assets;
            }
            foreach (string asset in entry.Assets.Where(static a => !string.IsNullOrWhiteSpace(a)))
            {
                assets.Add(asset.Replace('\\', '/'));
            }
        }

        state.Log($"    .assetinfo.json: {state.ShaderMapToAssets.Count} shader-maps -> assets.");
    }

    private static string SidecarBasePath(string libraryPath)
    {
        const string suffix = ".ushaderlib";
        return libraryPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? libraryPath[..^suffix.Length]
            : libraryPath;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class AssetInfoRoot { public List<AssetInfoEntry>? ShaderCodeToAssets { get; set; } }
    private sealed class AssetInfoEntry { public string? ShaderMapHash { get; set; } public List<string>? Assets { get; set; } }
}
