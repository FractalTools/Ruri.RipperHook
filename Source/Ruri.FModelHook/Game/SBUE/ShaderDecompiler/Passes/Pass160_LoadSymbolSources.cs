using System.IO;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 160 — wire up symbol-source services. The readers themselves live
// outside the pass so they can be reused by later passes as state-attached
// services rather than pass-local helper blobs.
internal static class Pass160_LoadSymbolSources
{
    public static void DoPass(PipelineState state)
    {
        string? unifiedPath = state.Options.UnifiedMetadataPath;
        if (!string.IsNullOrEmpty(unifiedPath))
        {
            string exportRoot = Path.GetDirectoryName(unifiedPath) ?? string.Empty;
            if (Directory.Exists(exportRoot))
            {
                state.MaterialJsonSymbolReader = new MaterialJsonSymbolReader(exportRoot);
            }

            if (File.Exists(unifiedPath))
            {
                state.UnifiedMaterialReader = UnifiedMaterialReader.LoadFromFile(unifiedPath);
            }
        }

        state.Log($"    Symbol sources: unified={(state.UnifiedMaterialReader != null ? "yes" : "no")}, per-material-json={(state.MaterialJsonSymbolReader != null ? "yes" : "no")}.");
    }
}
