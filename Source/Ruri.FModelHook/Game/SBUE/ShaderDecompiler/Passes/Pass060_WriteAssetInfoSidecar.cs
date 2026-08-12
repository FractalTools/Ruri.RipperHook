using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass060_WriteAssetInfoSidecar
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
