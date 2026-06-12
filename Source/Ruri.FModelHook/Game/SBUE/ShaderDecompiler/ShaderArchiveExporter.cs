using System;
using System.IO;
using CUE4Parse.FileProvider.Objects;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Per-archive orchestration shared by BOTH drivers:
//   * the interactive FModel `CUE4ParseViewModel.ExportData` hook
//     (UE_ShaderDecompiler_Hook), and
//   * the headless CLI mount (HeadlessShaderExportRunner) which builds a
//     CUE4Parse `DefaultFileProvider` directly — no FModel WPF host.
//
// One call processes one `.ushaderbytecode` entry end-to-end:
//   Pass 010  save the flat `.ushaderlib`
//   Pass 020-080  run the export pipeline (sidecars + cumulative
//                 UnifiedShaderMetadata.json on the shared ExportPipelineState)
//   Pass 110-200  decompile the just-written library in-process
//
// The cumulative cross-library state (IoStore hash index, material cache,
// Niagara bridge) lives on the caller-owned `ExportPipelineState`, so the
// driver is responsible for creating ONE state per session and serialising
// calls (the state is not thread-safe). Extracting this here keeps the two
// drivers byte-identical instead of drifting copy-paste.
internal static class ShaderArchiveExporter
{
    // Process a single shader-bytecode archive. `exportBasePath` is the
    // output path WITHOUT extension (the `.ushaderlib` + `.assetinfo.json` +
    // `.stableinfo.json` sidecars are derived from it). Returns true when the
    // library was exported (decompile failures are logged but don't flip the
    // result — the library + sidecars are still useful on their own).
    public static bool ProcessArchive(ExportPipelineState state, GameFile entry, string exportBasePath, bool splitVariants, bool skipDecompile = false)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        string libraryPath = exportBasePath + ".ushaderlib";

        // 1. Pass 010 — save the flat FSerializedShaderArchive. Also stashes
        //    this archive's shader-map hash set on the state for Pass 030's
        //    scoped material scan.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
            if (!Pass010_SaveShaderArchive.SaveShaderLibrary(entry, libraryPath, state))
            {
                state.LogError($"[ShaderArchiveExporter] Pass010 could not serialize {entry.Path} as a shader archive.");
                return false;
            }
            state.Log($"[+] Exported ShaderLibrary: {libraryPath}");
        }
        catch (Exception ex)
        {
            state.LogError($"[ShaderArchiveExporter] Failed to save .ushaderlib for {entry.Path}: {ex.Message}");
            try { if (File.Exists(libraryPath)) File.Delete(libraryPath); } catch { }
            return false;
        }

        // 2. Pass 020-080 — export pipeline. Cumulative state on `state`
        //    persists across archives so the expensive passes run once.
        state.Entry = entry;
        state.ExportBasePath = exportBasePath;
        try
        {
            ExportPipeline.Run(state);
        }
        catch (Exception ex)
        {
            state.LogError($"[ShaderArchiveExporter] Export pipeline failed for {entry.Path}: {ex.Message}");
        }

        // 3. Pass 110-200 — decompile the library we just wrote, using the
        //    cumulative UnifiedShaderMetadata.json for material-ball symbols.
        //    Skippable: --export-only builds the cache + sidecars + .ushaderlib
        //    without the (potentially multi-hour, 261k-shader on the master)
        //    decompile, so a later `--decompile-only` can iterate against the
        //    fully-populated unified file.
        if (skipDecompile)
        {
            state.Log($"[ShaderArchiveExporter] Export-only: skipped decompile for {Path.GetFileName(exportBasePath)}.");
            return true;
        }
        try
        {
            DecompileLibraryInProcess(state, exportBasePath, splitVariants);
        }
        catch (Exception ex)
        {
            state.LogError($"[ShaderArchiveExporter] In-process decompile crashed for {entry.Path}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
        }

        return true;
    }

    private static void DecompileLibraryInProcess(ExportPipelineState state, string exportBasePath, bool splitVariants)
    {
        string libraryPath = exportBasePath + ".ushaderlib";
        if (!File.Exists(libraryPath)) return;

        string unifiedMetadataPath = Path.Combine(state.ProjectOutputRoot, "UnifiedShaderMetadata.json");
        string outputDir = Path.Combine(Path.GetDirectoryName(exportBasePath)!, "Decompiled", Path.GetFileName(exportBasePath));

        DecompileSummary summary = DecompilePipeline.Run(new LibraryDecompileOptions
        {
            LibraryPath = libraryPath,
            OutputDirectory = outputDir,
            UnifiedMetadataPath = File.Exists(unifiedMetadataPath) ? unifiedMetadataPath : null,
            RecreateOutputDirectory = true,
            SplitVariantsToHlslFiles = splitVariants,
            Log = state.Log,
            LogError = state.LogError,
        });

        state.Log($"[ShaderArchiveExporter] Decompiled {summary.Decompiled}/{summary.TotalShaders} shaders -> {outputDir}");
    }
}
