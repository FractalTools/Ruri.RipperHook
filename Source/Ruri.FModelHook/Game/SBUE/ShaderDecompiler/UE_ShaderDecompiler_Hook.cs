using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using FModel.ViewModels;
using FModel.Settings;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.FileProvider.Vfs;
using Ruri.Hook.Core;
using CUE4Parse.FileProvider;
using Ruri.FModelHook.Attributes;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    [FModelHook(GameType.UE_ShaderDecompiler)]
    public class UE_ShaderDecompiler_Hook : RuriHook
    {
        // One ExportPipelineState lives for the lifetime of the FModel
        // session so the cumulative cross-library work (Pass 020 material
        // scan, Pass 040 IoStore hash extraction, Pass 090 once-only
        // unified-metadata write) only happens on the first library hit
        // and reuses its results for subsequent libraries.
        private static readonly ExportPipelineState _exportState = new()
        {
            Log = HookLogger.Log,
            LogError = HookLogger.LogFailure,
        };
        private static readonly object _exportStateLock = new();

        // Use RetargetMethod to safely inject C# logic before the original method and fall through (IsReturn = false)
        // Positional args: Type source, string methodName, bool isBefore, bool isReturn
        [RetargetMethod(typeof(CUE4ParseViewModel), "ExportData", true, false)]
        public static void ExportData_Hook(CUE4ParseViewModel self, GameFile entry, bool updateUi)
        {
            // Enable ReadShaderMaps on the provider to ensure UMaterial deserializes the InlineShaderMap
            if (self.Provider is AbstractFileProvider abstractProvider)
            {
                if (!abstractProvider.ReadShaderMaps)
                {
                    abstractProvider.ReadShaderMaps = true;
                }
            }

            if (entry == null) return;

            // Only trigger on Shader Bytecode Library export
            if (entry.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase))
            {
                string exportBasePath = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? entry.PathWithoutExtension : entry.NameWithoutExtension).Replace('\\', '/');
                bool exportedLibrary = false;

                // 1. Export Shader Library (.ushaderlib) — Pass 010.
                var libraryBytes = Pass010_SaveShaderArchive.SaveShaderLibrary(entry);
                if (libraryBytes != null)
                {
                    string path = exportBasePath + ".ushaderlib";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, libraryBytes);
                        HookLogger.LogSuccess($"[+] Exported ShaderLibrary: {path}");
                        exportedLibrary = true;
                    }
                    catch (Exception ex)
                    {
                        HookLogger.LogFailure($"Failed to save .ushaderlib: {ex.Message}");
                    }
                }

                // 2. Run the export pipeline (Pass 020 -> Pass 090) for
                //    this library. Cross-library state on `_exportState`
                //    persists across hook fires so cached passes only run
                //    once total.
                if (exportedLibrary)
                {
                    try
                    {
                        lock (_exportStateLock)
                        {
                            _exportState.Vm = self;
                            _exportState.Entry = entry;
                            _exportState.ExportBasePath = exportBasePath;
                            ExportPipeline.Run(_exportState);
                        }
                    }
                    catch (Exception ex)
                    {
                        HookLogger.LogFailure($"[UE_ShaderDecompiler] Export pipeline failed: {ex.Message}");
                    }
                }

                // 3. Decompile in-process (mirrors the Unity flow in
                // ShaderRuriDecompileExporter). The unified metadata is
                // exported once and reused; if it isn't on disk yet we
                // fall back to sidecar-only resolution.
                if (exportedLibrary)
                {
                    try
                    {
                        DecompileLibraryInProcess(self, exportBasePath);
                    }
                    catch (Exception ex)
                    {
                        HookLogger.LogFailure($"[UE_ShaderDecompiler] In-process decompile crashed: {ex.Message}");
                    }
                }
            }
        }

        private static void DecompileLibraryInProcess(CUE4ParseViewModel vm, string exportBasePath)
        {
            string libraryPath = exportBasePath + ".ushaderlib";
            if (!File.Exists(libraryPath))
            {
                return;
            }

            string projectName = vm.Provider?.ProjectName ?? "UnknownProject";
            string unifiedMetadataPath = Path.Combine(UserSettings.Default.RawDataDirectory, projectName, "UnifiedShaderMetadata.json");
            string outputDir = Path.Combine(Path.GetDirectoryName(exportBasePath)!, "Decompiled", Path.GetFileName(exportBasePath));

            DecompileSummary summary = DecompilePipeline.Run(new LibraryDecompileOptions
            {
                LibraryPath = libraryPath,
                OutputDirectory = outputDir,
                UnifiedMetadataPath = File.Exists(unifiedMetadataPath) ? unifiedMetadataPath : null,
                RecreateOutputDirectory = true,
                Log = HookLogger.Log,
                LogError = HookLogger.LogFailure,
            });

            HookLogger.LogSuccess($"[UE_ShaderDecompiler] Decompiled {summary.Decompiled}/{summary.TotalShaders} shaders -> {outputDir}");
        }
    }
}
