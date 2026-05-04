using System.IO;
using Newtonsoft.Json;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 090 — Write `<RawDataDirectory>/<projectName>/UnifiedShaderMetadata.json`
// once per FModel session. Pulls the cumulative
// `state.Root` populated by Passes 020/040/050 across every library
// hit so far; that's why the cache flag (`UnifiedMetadataWritten`)
// gates this and not the upstream cross-library passes — those need
// to keep ACCUMULATING data on each hit, but the JSON is only emitted
// once so consumers see a stable, complete file.
//
// Skips when there's nothing to write (no materials, no IoStore hashes,
// no archives) — same guard the original `ExportAll` had.
internal static class Pass090_WriteUnifiedMetadataJson
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.UnifiedMetadataWritten) return;

        var output = state.Root;
        if (output.MaterialInterfaces.Count == 0
            && output.PackageShaderMapHashes.Count == 0
            && output.ShaderCodeArchives.Count == 0)
        {
            HookLogger.LogWarning("[Pass090_WriteUnifiedMetadataJson] No verified shader metadata found to export.");
            return;
        }

        var provider = state.Vm?.Provider;
        if (provider == null) return;

        string projectName = provider.ProjectName ?? "UnknownProject";
        string outputPath = Path.Combine(FModel.Settings.UserSettings.Default.RawDataDirectory, projectName, "UnifiedShaderMetadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(output, Formatting.Indented));

        state.UnifiedMetadataWritten = true;
        HookLogger.LogSuccess($"[Pass090_WriteUnifiedMetadataJson] Exported unified metadata for {output.MaterialInterfaces.Count} materials, {output.PackageShaderMapHashes.Count} package->shader-map associations.");
    }
}
