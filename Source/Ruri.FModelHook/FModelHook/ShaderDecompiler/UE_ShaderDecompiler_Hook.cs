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

namespace Ruri.FModelHook.ShaderDecompiler
{
    [FModelHook(GameType.UE_ShaderDecompiler)]
    public class UE_ShaderDecompiler_Hook : RuriHook
    {
        private static readonly ExportPipelineState _exportState = new()
        {
            Log = HookLogger.Log,
            LogError = HookLogger.LogFailure,
        };
        private static readonly object _exportStateLock = new();

        private static volatile int _mappingsWarningChoice;

        public static bool SplitVariantsToHlslFiles
        {
            get => Ruri.ShaderTools.ShaderDecompilerSettingsAccess.Current.SplitVariantsToHlslFiles;
            set
            {
                var current = Ruri.ShaderTools.ShaderDecompilerSettingsAccess.Current;
                if (current.SplitVariantsToHlslFiles == value) return;
                Ruri.ShaderTools.ShaderDecompilerSettingsAccess.Replace(new Ruri.ShaderTools.ShaderDecompilerSettings
                {
                    SplitVariantsToHlslFiles = value,
                    WarnIfNoMappings = current.WarnIfNoMappings,
                    TryMatchBaseEngineVersion = current.TryMatchBaseEngineVersion,
                });
            }
        }

        [RetargetMethod(typeof(CUE4ParseViewModel), "ExportData", true, false)]
        public static void ExportData_Hook(CUE4ParseViewModel self, GameFile entry, bool updateUi)
        {
            if (self.Provider is AbstractFileProvider abstractProvider)
            {
                if (!abstractProvider.ReadShaderMaps)
                {
                    abstractProvider.ReadShaderMaps = true;
                }
            }

            if (entry == null) return;

            if (entry.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase))
            {
                if (!ConfirmMappingsOrAbort(self))
                {
                    HookLogger.Log("[UE_ShaderDecompiler] Skipped: user cancelled (no mappings loaded).");
                    return;
                }

                string exportBasePath = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? entry.PathWithoutExtension : entry.NameWithoutExtension).Replace('\\', '/');

                try
                {
                    lock (_exportStateLock)
                    {
                        _exportState.Provider = self.Provider;
                        _exportState.ProjectOutputRoot = Path.Combine(
                            UserSettings.Default.RawDataDirectory,
                            self.Provider?.ProjectName ?? "UnknownProject");
                        ShaderArchiveExporter.ProcessArchive(_exportState, entry, exportBasePath, SplitVariantsToHlslFiles);
                    }
                }
                catch (Exception ex)
                {
                    HookLogger.LogFailure($"[UE_ShaderDecompiler] Shader archive export failed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
                }
            }
        }

        private static bool ConfirmMappingsOrAbort(CUE4ParseViewModel vm)
        {
            if (vm?.Provider?.MappingsContainer != null)
            {
                _mappingsWarningChoice = 1;
                return true;
            }

            if (!Ruri.ShaderTools.ShaderDecompilerSettingsAccess.Current.WarnIfNoMappings)
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
    }
}
