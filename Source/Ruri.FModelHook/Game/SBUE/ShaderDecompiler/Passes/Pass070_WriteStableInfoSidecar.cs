using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass070_WriteStableInfoSidecar
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
