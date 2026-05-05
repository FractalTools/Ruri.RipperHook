using System.IO;
using Newtonsoft.Json;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 080 — Write `<RawDataDirectory>/<projectName>/UnifiedShaderMetadata.json`
// after EVERY archive export. The export pipeline accumulates data on
// `state.Root` across consecutive `ExportData_Hook` fires (one per
// `.ushaderbytecode`), and downstream consumers (the in-process
// decompile path running RIGHT after each archive's pass) need an
// up-to-date file at THAT moment.
//
// The previous "write once per session" gate (`UnifiedMetadataWritten`)
// caused materials added by archive N+1 to never make it into the JSON
// the decompile uses — which is the root cause of every "UnknownMaterial"
// shader emitted out of any archive other than the FIRST one exported in
// a given FModel session. Pass 080 is now idempotent: cheap rewrite (atomic
// move via Replace) is preferable to a stale file.
//
// Skips when there's nothing to write (no materials, no IoStore hashes,
// no archives) — same guard the original `ExportAll` had.
internal static class Pass080_WriteUnifiedMetadataJson
{
    public static void DoPass(ExportPipelineState state)
    {
        var output = state.Root;
        if (output.MaterialInterfaces.Count == 0
            && output.PackageShaderMapHashes.Count == 0
            && output.ShaderCodeArchives.Count == 0)
        {
            HookLogger.LogWarning("[Pass080_WriteUnifiedMetadataJson] No verified shader metadata found to export.");
            return;
        }

        var provider = state.Vm?.Provider;
        if (provider == null) return;

        string projectName = provider.ProjectName ?? "UnknownProject";
        string outputPath = Path.Combine(FModel.Settings.UserSettings.Default.RawDataDirectory, projectName, "UnifiedShaderMetadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Write to a sibling temp file first then atomic-replace so a
        // crashed write never leaves a half-written JSON for the next
        // run to fail on (UnifiedMaterialReader.LoadFromFile silently
        // returns null on invalid JSON, which would manifest as a
        // mysterious total-symbol-loss). Replace handles the case where
        // outputPath doesn't exist yet by falling back to a plain Move.
        string tempPath = outputPath + ".tmp";
        File.WriteAllText(tempPath, JsonConvert.SerializeObject(output, Formatting.Indented));
        if (File.Exists(outputPath))
        {
            File.Replace(tempPath, outputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, outputPath);
        }

        state.UnifiedMetadataWritten = true;
        HookLogger.LogSuccess($"[Pass080_WriteUnifiedMetadataJson] Wrote unified metadata: {output.MaterialInterfaces.Count} materials, {output.PackageShaderMapHashes.Count} package->shader-map associations, {output.ShaderCodeArchives.Count} archives.");
    }
}
