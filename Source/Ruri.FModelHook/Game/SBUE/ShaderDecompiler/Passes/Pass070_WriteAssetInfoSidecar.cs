using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 070 — Write the per-library `<ExportBasePath>.assetinfo.json` from
// the `state.AssetInfo` DTO that Pass 060 just composed. This is the
// shader-map-hash → assets[] sidecar consumed downstream by Pass 120
// (LoadAssetInfoSidecar) on the decompile side.
//
// Skipped silently when AssetInfo is null — Pass 060 may have decided
// the library has nothing to link (e.g. global shader archive with no
// material side at all).
internal static class Pass070_WriteAssetInfoSidecar
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.AssetInfo == null) return;
        if (string.IsNullOrWhiteSpace(state.ExportBasePath)) return;

        string path = state.ExportBasePath + ".assetinfo.json";
        File.WriteAllText(path, JsonConvert.SerializeObject(state.AssetInfo, Formatting.Indented));
        state.Log($"    Wrote {Path.GetFileName(path)}: {state.AssetInfo.ShaderCodeToAssets.Count} shader-map(s).");
    }
}
