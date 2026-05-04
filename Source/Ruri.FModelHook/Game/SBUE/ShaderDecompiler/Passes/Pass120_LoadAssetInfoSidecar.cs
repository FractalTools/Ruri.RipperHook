using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 020 — Read the per-library `.assetinfo.json` sidecar.
//
// Sidecar contract (written by Pass 110): the IoStore container header
// reports a list of cooked shader-map hashes per package (material). This
// pass is the pure-input side of that contract: it inverts that list
// into `state.ShaderMapToAssets[hash] = {materials...}` so subsequent
// passes can ask "what assets own this on-disk shader-map hash?".
//
// `.assetinfo.json` shape — `{ AssetInfoVersion: 2,
// ShaderCodeToAssets: [{ ShaderMapHash, Assets[] }] }`.
//
// Standalone pass because the dictionary it produces is consumed by
// BOTH the usage-fan-out logic (Pass 050) AND the shader-map view
// (Pass 050) — splitting reads from interpretation makes each step
// trivially testable in isolation.
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
