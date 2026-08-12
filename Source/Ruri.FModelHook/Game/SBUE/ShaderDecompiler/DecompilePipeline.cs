using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ShaderDecompilerEngine = Ruri.ShaderTools.ShaderDecompiler;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

public static class DecompilePipeline
{
    public static DecompileSummary Run(LibraryDecompileOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.LibraryPath)) throw new ArgumentException("LibraryPath is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("OutputDirectory is required.", nameof(options));
        if (!File.Exists(options.LibraryPath)) throw new FileNotFoundException("UE shader library not found.", options.LibraryPath);

        PipelineState state = new(options);

        string engineUbDir = options.EngineUbMetadataDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "EngineUbMetadata");

        try
        {
            using (new TimingCookie(state, "Pass 110: Read .ushaderlib"))           Pass110_ReadShaderLibrary.DoPass(state);
            using (new TimingCookie(state, "Pass 120: Load .assetinfo.json"))       Pass120_LoadAssetInfoSidecar.DoPass(state);
            using (new TimingCookie(state, "Pass 130: Load .stableinfo.json"))      Pass130_LoadStableInfoSidecar.DoPass(state);
            using (new TimingCookie(state, "Pass 140: Load UnifiedShaderMetadata")) Pass140_LoadUnifiedMetadataIndex.DoPass(state);
            using (new TimingCookie(state, "Pass 145: Load engine-UB metadata"))
            {
                bool tryBaseFallback = Ruri.ShaderTools.ShaderDecompilerSettingsAccess.Current.TryMatchBaseEngineVersion;

                MaterialConstantBufferReader.PreshaderVersion = DetectPreshaderVersion(state.GameVersionEnum, state.Log);
                state.EngineUbRegistry = EngineUbMetadataRegistry.LoadForGame(
                    engineUbDir,
                    string.IsNullOrEmpty(state.GameVersionEnum) ? null : state.GameVersionEnum,
                    tryBaseFallback,
                    state.Log, state.LogError);

                state.ShaderTypeSeedRegistry = ShaderTypeSeedRegistry.LoadForGame(
                    engineUbDir,
                    string.IsNullOrEmpty(state.GameVersionEnum) ? null : state.GameVersionEnum,
                    tryBaseFallback,
                    state.Log, state.LogError);

                state.VertexFactoryTypeNameIndex = HashNameIndex.LoadForGame(
                    engineUbDir, "_VertexFactoryType",
                    string.IsNullOrEmpty(state.GameVersionEnum) ? null : state.GameVersionEnum,
                    tryBaseFallback,
                    state.Log, state.LogError);
                state.PipelineTypeNameIndex = HashNameIndex.LoadForGame(
                    engineUbDir, "_ShaderPipelineType",
                    string.IsNullOrEmpty(state.GameVersionEnum) ? null : state.GameVersionEnum,
                    tryBaseFallback,
                    state.Log, state.LogError);
            }
            using (new TimingCookie(state, "Pass 146: Backfill VF/Pipeline names"))    Pass146_BackfillContainerNames.DoPass(state);
            using (new TimingCookie(state, "Pass 150: Build shader-map view"))      Pass150_BuildShaderMapView.DoPass(state);
            using (new TimingCookie(state, "Pass 160: Load symbol sources"))        Pass160_LoadSymbolSources.DoPass(state);
            using (new TimingCookie(state, "Pass 165: Load shader ParameterMapInfo")) Pass165_LoadShaderParameterMapInfo.DoPass(state);
            using (new TimingCookie(state, "Pass 170: Build shaderlab Properties")) Pass170_BuildShaderLabProperties.DoPass(state);
            using (new TimingCookie(state, "Pass 175: Build render-state block"))   Pass175_BuildRenderStateBlock.DoPass(state);
            using (new TimingCookie(state, "Pass 180: Prepare shader binaries"))    Pass180_PrepareShaderBinaries.DoPass(state);

            using (new TimingCookie(state, "Pass 190+200: Decompile + emit per map"))
            {
                string outputDir = string.IsNullOrEmpty(state.OutputDirectory)
                    ? Path.GetFullPath(state.Options.OutputDirectory)
                    : state.OutputDirectory;
                using ShaderDecompilerEngine engine = new(outputDir);
                foreach (ShaderMapInfo map in state.ShaderMaps.OrderBy(static m => m.PrimaryName, StringComparer.OrdinalIgnoreCase))
                {
                    Pass190_RunEngineDecompile.DoPassForOneMap(state, engine, map);
                    Pass200_EmitShaderLabFiles.DoPassForOneMap(state, map);
                }
                state.Log($"    Library {Path.GetFileName(state.Options.LibraryPath)}: shader-maps={state.ShaderMaps.Count} decompiled={state.Decompiled} skipped={state.Skipped} failed={state.Failed}.");
            }

            return new DecompileSummary(
                state.Library?.ShaderEntries.Length ?? 0,
                state.Decompiled,
                state.Skipped,
                state.Failed);
        }
        finally
        {
            state.Library?.Dispose();
        }
    }

    private static UeMaterialPreshaderVersion DetectPreshaderVersion(string? gameVersionEnum, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(gameVersionEnum)) return UeMaterialPreshaderVersion.Ue51;

        string? baseUe = null;
        if (gameVersionEnum!.StartsWith("GAME_UE5_", StringComparison.Ordinal))
        {
            baseUe = gameVersionEnum;
        }
        else if (EngineUbMetadataRegistry.TryDeriveBaseUeFromEGameForShaderTypes(gameVersionEnum, out string derived))
        {
            baseUe = derived;
        }
        if (string.IsNullOrEmpty(baseUe)) return UeMaterialPreshaderVersion.Ue51;

        const string prefix = "GAME_UE5_";
        if (!baseUe!.StartsWith(prefix, StringComparison.Ordinal)) return UeMaterialPreshaderVersion.Ue51;
        if (!int.TryParse(baseUe!.AsSpan(prefix.Length), out int minor)) return UeMaterialPreshaderVersion.Ue51;
        UeMaterialPreshaderVersion picked =
            minor >= 7 ? UeMaterialPreshaderVersion.Ue57 :
            minor >= 4 ? UeMaterialPreshaderVersion.Ue54 :
                         UeMaterialPreshaderVersion.Ue51;
        log?.Invoke($"    Pass145: preshader-opcode layout = {picked} (derived from {gameVersionEnum}{(baseUe == gameVersionEnum ? "" : $" → {baseUe}")})");
        return picked;
    }

    private readonly struct TimingCookie : IDisposable
    {
        private readonly PipelineState _state;
        private readonly string _label;
        private readonly Stopwatch _stopwatch;

        public TimingCookie(PipelineState state, string label)
        {
            _state = state;
            _label = label;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _state.Log($"  {_label} — {_stopwatch.ElapsedMilliseconds} ms");
        }
    }
}
