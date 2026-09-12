using System.IO;

namespace Ruri.FModelHook.ShaderDecompiler;

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
