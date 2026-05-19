using System.Collections.Generic;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 165 — join cook-side per-shader `ParameterMapInfo` (real `$Globals`
// byte offsets, TextureSamplers BindIndex, etc., from the cooked binary)
// against the in-memory `ShaderMaps` view so Pass180 can reconcile names.
//
// Why a separate pass after Pass160:
//   * UnifiedMaterialReader has the raw `MaterialShaderMapContent.Shaders[]`
//     keyed by COOK shader-map hash (not archive index).
//   * Pass150's `ShaderMaps` view links shader-map hash -> Members[] each
//     with `ArchiveShaderIndex` + `RelativeIndex`.
//   * We pair them: for each (mapHash, Shaders[]) tuple in unified, find
//     the matching ShaderMapInfo, then walk Shaders[j] -> Members[j].
//     ArchiveShaderIndex.
//
// `ParameterMapInfo.LooseParameterBuffers[$Globals_idx].Parameters[]` has
// real cook-assigned byte offsets but no names; the ShaderType seed has
// names in source-declaration order but placeholder offsets. Pass180
// matches them by ORDER (or by SIZE when the seed has them) to build the
// final `$Globals` ConstantBufferParameter.
//
// Tolerant of missing pieces: shader-maps that aren't in unified (rare,
// e.g. Niagara-only archives) just skip — the rewriter falls back to its
// current behaviour (anonymous `_Globals_m0[N]` flat array).
internal static class Pass165_LoadShaderParameterMapInfo
{
    public static void DoPass(PipelineState state)
    {
        if (state.UnifiedMaterialReader == null || state.ShaderMaps.Count == 0)
        {
            state.Log($"    Pass165: unified-reader={(state.UnifiedMaterialReader != null ? "yes" : "no")} shader-maps={state.ShaderMaps.Count} — no join.");
            return;
        }

        // Index ShaderMapInfo by hash for O(N) join.
        Dictionary<string, ShaderMapInfo> mapsByHash = new(System.StringComparer.OrdinalIgnoreCase);
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            if (!string.IsNullOrWhiteSpace(map.ShaderMapHash))
            {
                mapsByHash[map.ShaderMapHash] = map;
            }
        }

        int joined = 0, withParamMap = 0;
        foreach ((string mapHash, JsonElement shaders) in state.UnifiedMaterialReader.EnumerateShaderMapShaders())
        {
            if (!mapsByHash.TryGetValue(mapHash, out ShaderMapInfo? map)) continue;
            int j = 0;
            foreach (JsonElement shader in shaders.EnumerateArray())
            {
                if (shader.ValueKind != JsonValueKind.Object) { j++; continue; }
                if (j >= map.Members.Count) break;
                int archiveIndex = map.Members[j].ArchiveShaderIndex;
                j++;
                if (archiveIndex < 0) continue;
                joined++;
                if (shader.TryGetProperty("ParameterMapInfo", out JsonElement pmi) && pmi.ValueKind == JsonValueKind.Object)
                {
                    state.ShaderParameterMapInfoByArchiveIndex[archiveIndex] = pmi.Clone();
                    withParamMap++;
                }
            }
        }
        state.Log($"    Pass165: joined {joined} shader entries, ParameterMapInfo populated for {withParamMap} archive indices.");
    }
}
