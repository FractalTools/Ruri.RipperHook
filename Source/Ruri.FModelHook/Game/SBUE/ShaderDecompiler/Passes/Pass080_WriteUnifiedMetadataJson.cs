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
            && output.NiagaraShaderMapHashes.Count == 0
            && output.ShaderCodeArchives.Count == 0)
        {
            HookLogger.LogWarning("[Pass080_WriteUnifiedMetadataJson] No verified shader metadata found to export.");
            return;
        }

        var provider = state.Provider;
        if (provider == null) return;

        // Capture the CUE4Parse EGame enum name (e.g. "GAME_UE5_1", "GAME_InfinityNikki")
        // so the decompile side can pick the matching `EngineUbMetadata/<EGame>/`
        // subfolder for engine-UB symbol seeds. Game-specific overrides
        // (forks with custom UB layouts) are auto-detected by their full
        // EGame name; the base UE major.minor folder is the fallback.
        output.GameVersionEnum = provider.Versions?.Game.ToString() ?? string.Empty;
        // Stamp the cache format so a future tool build with a different
        // extraction shape invalidates this file instead of warm-seeding from it.
        output.CacheFormatVersion = UnifiedShaderMetadataRoot.CurrentCacheFormatVersion;

        // ProjectOutputRoot is `<RawDataDirectory>/<ProjectName>`, supplied by
        // the driver (FModel hook or headless CLI). Falling back to the
        // provider's project name keeps the file landing next to the sidecars
        // even if a driver forgets to set it.
        string outputRoot = !string.IsNullOrEmpty(state.ProjectOutputRoot)
            ? state.ProjectOutputRoot
            : Path.Combine(Path.GetDirectoryName(state.ExportBasePath) ?? ".", provider.ProjectName ?? "UnknownProject");
        string outputPath = Path.Combine(outputRoot, "UnifiedShaderMetadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Write to a sibling temp file first then atomic-replace so a
        // crashed write never leaves a half-written JSON for the next
        // run to fail on (UnifiedMaterialReader.LoadFromFile silently
        // returns null on invalid JSON, which would manifest as a
        // mysterious total-symbol-loss). Replace handles the case where
        // outputPath doesn't exist yet by falling back to a plain Move.
        //
        // STREAM the JSON straight to the file via JsonTextWriter rather than
        // `JsonConvert.SerializeObject(...)`. The latter materialises the WHOLE
        // document as a single in-memory string first — on the master cook
        // (23k+ materials) that string blows past .NET's ~2GB single-string /
        // available-memory ceiling and throws "Insufficient memory", which
        // silently aborted the write and left a stale unified file (every
        // post-master shader then resolved against the wrong, tiny cache).
        // Streaming is O(1) in peak string size.
        string tempPath = outputPath + ".tmp";
        var serializer = JsonSerializer.Create(new JsonSerializerSettings { Formatting = Formatting.Indented });
        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var streamWriter = new StreamWriter(fileStream))
        using (var jsonWriter = new JsonTextWriter(streamWriter) { Formatting = Formatting.Indented })
        {
            serializer.Serialize(jsonWriter, output);
        }
        if (File.Exists(outputPath))
        {
            File.Replace(tempPath, outputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, outputPath);
        }

        state.UnifiedMetadataWritten = true;
        HookLogger.LogSuccess($"[Pass080_WriteUnifiedMetadataJson] Wrote unified metadata: {output.MaterialInterfaces.Count} materials, {output.PackageShaderMapHashes.Count} package->shader-map associations, {output.NiagaraShaderMapHashes.Count} Niagara hash bridges, {output.ShaderCodeArchives.Count} archives.");
    }
}
