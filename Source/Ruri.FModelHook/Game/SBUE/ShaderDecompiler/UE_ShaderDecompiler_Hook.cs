using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
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
using AdonisUI.Controls;
using AdonisMessageBox = AdonisUI.Controls.MessageBox;
using AdonisMessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using AdonisMessageBoxResult = AdonisUI.Controls.MessageBoxResult;

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

        // One-shot per session: warn the user the first time they trigger a
        // shader export without mappings loaded, then remember their choice
        // so a 200-shader-archive bulk export doesn't pop up 200 dialogs.
        // 0 = ungated, 1 = user accepted "yes, continue without mappings",
        // 2 = user rejected (suppress all subsequent shader exports).
        private static volatile int _mappingsWarningChoice;

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
                // Mappings gate. Without a .usmap loaded, CUE4Parse can still
                // load most assets but UMaterial property serialisation falls
                // back to legacy schema-less reading: the FUniformExpressionSet
                // tree we lean on (UniformNumericParameters / UniformTextureParameters /
                // UniformBufferLayoutInitializer) reads as opaque structs and
                // every author-facing parameter name disappears. The decompile
                // still runs but the output collapses to anonymous Material_Tn /
                // Material_<TypedSlot> placeholders, with zero of the per-material
                // parameter names and shaderlab Property entries the user sees
                // when mappings ARE loaded. Pop a warning so they can either
                // load a .usmap first or knowingly accept the symbol-stripped run.
                if (!ConfirmMappingsOrAbort(self))
                {
                    HookLogger.Log("[UE_ShaderDecompiler] Skipped: user cancelled (no mappings loaded).");
                    return;
                }

                string exportBasePath = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? entry.PathWithoutExtension : entry.NameWithoutExtension).Replace('\\', '/');
                bool exportedLibrary = false;

                // 1. Export Shader Library (.ushaderlib) — Pass 010.
                // Pass state so Pass010 can stash the archive's shader-map
                // hash set on it; Pass020 reads that to scope the scan.
                var libraryBytes = Pass010_SaveShaderArchive.SaveShaderLibrary(entry, _exportState);
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

        // Returns true when export should proceed, false when the user
        // cancelled. Behaviour:
        //   * Mappings loaded         -> ungated (cache _mappingsWarningChoice = 1).
        //   * Mappings missing, first call this session -> dispatch a yes/no
        //     dialog on the UI thread. Cache the answer.
        //   * Mappings missing, subsequent calls -> obey the cached answer.
        // The dispatcher detour is necessary because ExportData_Hook can fire
        // from the FModel worker thread when ExportData is invoked from a
        // batch loop; AdonisUI's MessageBox must run on the dispatcher.
        private static bool ConfirmMappingsOrAbort(CUE4ParseViewModel vm)
        {
            if (vm?.Provider?.MappingsContainer != null)
            {
                _mappingsWarningChoice = 1;
                return true;
            }

            int cached = _mappingsWarningChoice;
            if (cached == 1) return true;
            if (cached == 2) return false;

            const string warningText =
                "Shader Decompiler: no mappings (.usmap) currently loaded.\n\n" +
                "Without a type-tree mappings file, CUE4Parse cannot resolve material UProperty schemas. " +
                "Every per-material symbol (UniformNumericParameters, UniformTextureParameters, ParameterInfo names, " +
                "UniformBufferLayoutInitializer.Resources) reads as an opaque struct, and the resulting .shader files " +
                "lose all author-facing parameter names and shaderlab Property entries.\n\n" +
                "Recommended: cancel, load a .usmap via Settings -> General -> Local Mapping File, then re-run.\n\n" +
                "Continue export anyway? (Output will use anonymous Material_Tn / Material_<TypedSlot> placeholders.)";

            bool proceed = false;
            try
            {
                if (Application.Current?.Dispatcher != null)
                {
                    proceed = Application.Current.Dispatcher.Invoke(() =>
                    {
                        var model = new MessageBoxModel
                        {
                            Text = warningText,
                            Caption = "Mappings missing",
                            Icon = AdonisMessageBoxImage.Warning,
                            Buttons = MessageBoxButtons.YesNo(),
                            IsSoundEnabled = false,
                        };
                        AdonisMessageBox.Show(model);
                        return model.Result == AdonisMessageBoxResult.Yes;
                    });
                }
                else
                {
                    // Headless / no dispatcher (CLI run) — preserve legacy
                    // behaviour and let the export proceed; the user explicitly
                    // wired up a non-UI run loop.
                    proceed = true;
                }
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[UE_ShaderDecompiler] Mappings prompt failed: {ex.Message}");
                proceed = true;
            }

            _mappingsWarningChoice = proceed ? 1 : 2;
            return proceed;
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
