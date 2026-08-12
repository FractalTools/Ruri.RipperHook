using System;
using System.IO;
using CUE4Parse.FileProvider.Objects;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class ShaderArchiveExporter
{
    public static bool ProcessArchive(ExportPipelineState state, GameFile entry, string exportBasePath, bool splitVariants, bool skipDecompile = false, string? materialFilter = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        string libraryPath = exportBasePath + ".ushaderlib";

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

        if (skipDecompile)
        {
            state.Log($"[ShaderArchiveExporter] Export-only: skipped decompile for {Path.GetFileName(exportBasePath)}.");
            return true;
        }
        try
        {
            DecompileLibraryInProcess(state, exportBasePath, splitVariants, materialFilter);
        }
        catch (Exception ex)
        {
            state.LogError($"[ShaderArchiveExporter] In-process decompile crashed for {entry.Path}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
        }

        return true;
    }

    private static void DecompileLibraryInProcess(ExportPipelineState state, string exportBasePath, bool splitVariants, string? materialFilter)
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
            MaterialFilter = materialFilter,
            RecreateOutputDirectory = string.IsNullOrWhiteSpace(materialFilter),
            SplitVariantsToHlslFiles = splitVariants,
            Log = state.Log,
            LogError = state.LogError,
        });

        state.Log($"[ShaderArchiveExporter] Decompiled {summary.Decompiled}/{summary.TotalShaders} shaders -> {outputDir}");
    }
}
