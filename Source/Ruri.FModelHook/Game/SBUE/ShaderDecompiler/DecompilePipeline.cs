using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ShaderDecompilerEngine = Ruri.ShaderTools.ShaderDecompiler;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Orchestrator. Pass numbers reflect EXECUTION ORDER end-to-end —
// Export must run first because Decompile reads the sidecars Export
// produces. 10-increments leave room to insert without renumbering.
//
// Each pass is self-contained: it fills typed slots on PipelineState
// and the orchestrator just sequences them. Passes never call into
// another pass's helpers; if a piece of logic is shared by multiple
// passes it lives as a state-attached data type (UnifiedMaterialReader,
// MaterialUniformBufferLayout, etc.).
//
// Export pipeline (FModel-hook side; needs vm.Provider) — orchestrated
// by ExportPipeline.Run, see ExportPipeline.cs. Pass digits == execution
// order. The `HashedNamesResolver` static utility (no Pass number) is
// consumed on demand by Pass 050; it has no execution slot so it doesn't
// take one of the numeric labels.
//   Pass 010  Save IoStore archive as flat FSerializedShaderArchive (from hook)
//   Pass 020  Extract IoStore shader-map hashes (cached)
//   Pass 030  Scan material packages -> Root.MaterialInterfaces (cached)
//   Pass 035  Extract Niagara FShaderMap.ResourceHash bridge (cached) —
//             independent ID space from material side, required for
//             archives containing Niagara-only shader-maps
//   Pass 040  Per-library archive metadata view
//   Pass 050  Build per-shader-map stable records
//   Pass 060  Write `<base>.assetinfo.json`
//   Pass 070  Write `<base>.stableinfo.json`
//   Pass 080  Write `UnifiedShaderMetadata.json` (rewritten every archive)
//
// Decompile pipeline (CLI-runnable; reads .ushaderlib + JSON sidecars):
//   Pass 110  Read .ushaderlib bytes into ShaderLibrary
//   Pass 120  Load .assetinfo.json     -> ShaderMapToAssets
//   Pass 130  Load .stableinfo.json    -> hash-fanout + per-shader containers
//   Pass 140  Load UnifiedShaderMetadata.json -> hash-to-materials index
//   Pass 150  Compose 120-140 into per-shader-map view + usage / name maps
//   Pass 160  Spin up per-material symbol-source readers
//   Pass 170  Build shaderlab `Properties { ... }` from each map's UES
//   Pass 175  Build SubShader Tags + per-Pass render-state lines from each
//             map's RenderState UProperty bag (BlendMode/TwoSided/etc.)
//   Pass 180  Phase 1 — strip UE wrapper, build SerializedProgramData +
//             EngineDecompileOptions per shader binary -> ShaderPrepByIndex.
//   Pass 190  Phase 2 — drive ShaderDecompilerEngine.Decompile across
//             every prepped binary -> DecompileResultByIndex.
//   Pass 200  Phase 3 — walk shader-maps in alphabetical order, render
//             per-map .shader files with variants under `#if defined(...)`
//             blocks.
//
// Pass 190 + Pass 200 are INTERLEAVED per-shader-map by the orchestrator:
// for each map, decompile its pending binaries (Pass190.DoPassForOneMap)
// then emit its `.shader` file (Pass200.DoPassForOneMap). This restores
// the streaming behavior the user wanted — completed `.shader` files
// land progressively as work advances rather than in a single burst at
// the end. The cache in `state.DecompileResultByIndex` makes shared
// binaries decompile once even when N maps own them.
//
// Future inserts (e.g. material-asset attribute extraction for
// SubShader Tags / RenderQueue / ShadingModel) take the next free
// 10-step slot without renumbering anything else.
public static class DecompilePipeline
{
    public static DecompileSummary Run(LibraryDecompileOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.LibraryPath)) throw new ArgumentException("LibraryPath is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("OutputDirectory is required.", nameof(options));
        if (!File.Exists(options.LibraryPath)) throw new FileNotFoundException("UE shader library not found.", options.LibraryPath);

        PipelineState state = new(options);

        // The engine-UB seed directory; loading is deferred until Pass 145
        // (after Pass 140 has populated `state.GameVersionEnum` from
        // UnifiedShaderMetadata.json so we can pick the right
        // `EngineUbMetadata/<EGame>/` subfolder).
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
                // Honour the user-facing toggle: when the game-specific folder
                // is missing, fall back to the base UE folder (e.g. GAME_UE5_4
                // for GAME_InfinityNikki). Default-on; only matters when the
                // game name DOESN'T already start with GAME_UE — in that case
                // the specific folder IS the base, no further fallback exists.
                bool tryBaseFallback = Ruri.ShaderTools.ShaderDecompilerSettingsAccess.Current.TryMatchBaseEngineVersion;
                state.EngineUbRegistry = EngineUbMetadataRegistry.LoadForGame(
                    engineUbDir,
                    string.IsNullOrEmpty(state.GameVersionEnum) ? null : state.GameVersionEnum,
                    tryBaseFallback,
                    state.Log, state.LogError);

                // Same priority pattern for the ShaderType seed registry: it
                // serves `FShaderType::HashedName` → loose-parameter name
                // catalogue lookups (the `$Globals` cbuffer + direct texture
                // bindings whose names are dropped at cook). Reuses the
                // engineUbDir so the user only has one symbol pack to manage.
                state.ShaderTypeSeedRegistry = ShaderTypeSeedRegistry.LoadForGame(
                    engineUbDir,
                    string.IsNullOrEmpty(state.GameVersionEnum) ? null : state.GameVersionEnum,
                    tryBaseFallback,
                    state.Log, state.LogError);
            }
            using (new TimingCookie(state, "Pass 150: Build shader-map view"))      Pass150_BuildShaderMapView.DoPass(state);
            using (new TimingCookie(state, "Pass 160: Load symbol sources"))        Pass160_LoadSymbolSources.DoPass(state);
            using (new TimingCookie(state, "Pass 170: Build shaderlab Properties")) Pass170_BuildShaderLabProperties.DoPass(state);
            using (new TimingCookie(state, "Pass 175: Build render-state block"))   Pass175_BuildRenderStateBlock.DoPass(state);
            using (new TimingCookie(state, "Pass 180: Prepare shader binaries"))    Pass180_PrepareShaderBinaries.DoPass(state);

            // Pass 190 + Pass 200 INTERLEAVED per shader-map for streaming.
            // The engine instance lives across all maps so its one-time setup
            // is amortised. Cache via state.DecompileResultByIndex makes
            // shared binaries decompile only on the first owning map's pass.
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
            // Release the .ushaderlib FileStream that ShaderLibrary holds
            // open for streaming code-body reads. Without this, the file
            // stays locked until GC kicks in — bad for batch CLI runs that
            // process N archives back-to-back and may want to read/move
            // the .ushaderlib afterwards.
            state.Library?.Dispose();
        }
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
