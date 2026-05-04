using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 080 — Write the per-library `<ExportBasePath>.stableinfo.json`
// from the `state.StableInfo` DTO that Pass 060 just composed. This is
// the per-shader-map breakdown (shader hashes, frequencies, type/VF/
// permutation truth) consumed by Pass 130 (LoadStableInfoSidecar) on
// the decompile side.
//
// Skipped silently when StableInfo is null — symmetric with Pass 070's
// behaviour for the assetinfo sidecar.
internal static class Pass080_WriteStableInfoSidecar
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.StableInfo == null) return;
        if (string.IsNullOrWhiteSpace(state.ExportBasePath)) return;

        string path = state.ExportBasePath + ".stableinfo.json";
        File.WriteAllText(path, JsonConvert.SerializeObject(state.StableInfo, Formatting.Indented));
        state.Log($"    Wrote {Path.GetFileName(path)}: {state.StableInfo.ShaderMaps.Count} shader-map(s).");
    }
}
