using System.Collections.Generic;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass165_LoadShaderParameterMapInfo
{
    public static void DoPass(PipelineState state)
    {
        if (state.UnifiedMaterialReader == null || state.ShaderMaps.Count == 0)
        {
            state.Log($"    Pass165: unified-reader={(state.UnifiedMaterialReader != null ? "yes" : "no")} shader-maps={state.ShaderMaps.Count} — no join.");
            return;
        }

        Dictionary<string, ShaderMapInfo> mapsByHash = new(System.StringComparer.OrdinalIgnoreCase);
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            if (!string.IsNullOrWhiteSpace(map.ShaderMapHash))
            {
                mapsByHash[map.ShaderMapHash] = map;
            }
        }

        int joined = 0, withParamMap = 0;
        foreach ((string mapHash, Dictionary<int, JsonElement> paramMapByResourceIndex) in state.UnifiedMaterialReader.EnumerateShaderMapShaders())
        {
            if (!mapsByHash.TryGetValue(mapHash, out ShaderMapInfo? map)) continue;
            foreach (ShaderMapMember member in map.Members)
            {
                if (member.ArchiveShaderIndex < 0) continue;
                joined++;
                if (paramMapByResourceIndex.TryGetValue(member.RelativeIndex, out JsonElement pmi))
                {
                    state.ShaderParameterMapInfoByArchiveIndex[member.ArchiveShaderIndex] = pmi.Clone();
                    withParamMap++;
                }
            }
        }
        state.Log($"    Pass165: joined {joined} shader entries, ParameterMapInfo populated for {withParamMap} archive indices.");
    }
}
