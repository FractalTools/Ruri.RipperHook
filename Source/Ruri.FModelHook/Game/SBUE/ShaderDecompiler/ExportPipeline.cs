using System;
using System.Diagnostics;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Orchestrator for the FModel-hook side. Pass numbers reflect actual
// execution order (digits == sequence). Pass 010 (SaveShaderArchive)
// runs from the hook itself before this — it produces the .ushaderlib
// bytes that downstream sidecars reference. `HashedNamesResolver` is a
// stateless utility consumed by Pass 050, not a sequencing step (no
// number).
//
// Pass 020 + Pass 030 build cumulative cross-library state (IoStore
// shader-map-hash table + material graph) which Pass 040-070 use to
// emit per-library sidecars. Pass 080 writes the cumulative
// UnifiedShaderMetadata.json after every archive (was once-per-session
// before — that gate was the root cause of "UnknownMaterial" outputs
// for any archive after the first). Caching gates on ExportPipelineState
// prevent the expensive cross-library passes from rerunning.
//
// Export pipeline (FModel-hook side; needs vm.Provider):
//   Pass 010  Save IoStore archive as flat FSerializedShaderArchive  (runs from hook BEFORE Run)
//             also stashes the archive's shader-map hashes on state for Pass 030 scoping
//   Pass 020  Extract IoStore shader-map hashes -> Root.PackageShaderMapHashes (cached)
//             First inside Run because Pass 030 uses its package->hash index
//             to scope the scan to materials that reference the current archive
//   Pass 030  Scan material packages whose hashes intersect the current archive
//             -> Root.MaterialInterfaces (cumulative cache across hook fires)
//   ----      HashedNamesResolver — utility, no number, called by Pass 050
//   Pass 040  Build per-library archive view -> Root.ShaderCodeArchives[lib]
//   Pass 050  Build stable shader records -> state.AssetInfo + state.StableInfo
//   Pass 060  Write `<base>.assetinfo.json` from state.AssetInfo
//   Pass 070  Write `<base>.stableinfo.json` from state.StableInfo
//   Pass 080  Write `UnifiedShaderMetadata.json`                     (every archive, idempotent)
internal static class ExportPipeline
{
    public static void Run(ExportPipelineState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        using (new TimingCookie(state, "Pass 020: Extract IoStore shader-map hashes")) Pass020_ExtractIoStoreShaderMapHashes.DoPass(state);
        using (new TimingCookie(state, "Pass 030: Scan material packages"))            Pass030_ScanMaterialPackages.DoPass(state);
        using (new TimingCookie(state, "Pass 040: Build shader-library metadata"))     Pass040_BuildShaderLibraryMetadata.DoPass(state);
        using (new TimingCookie(state, "Pass 050: Build stable shader records"))       Pass050_BuildStableShaderRecords.DoPass(state);
        using (new TimingCookie(state, "Pass 060: Write .assetinfo.json sidecar"))     Pass060_WriteAssetInfoSidecar.DoPass(state);
        using (new TimingCookie(state, "Pass 070: Write .stableinfo.json sidecar"))    Pass070_WriteStableInfoSidecar.DoPass(state);
        using (new TimingCookie(state, "Pass 080: Write UnifiedShaderMetadata.json"))  Pass080_WriteUnifiedMetadataJson.DoPass(state);
    }

    private readonly struct TimingCookie : IDisposable
    {
        private readonly ExportPipelineState _state;
        private readonly string _label;
        private readonly Stopwatch _stopwatch;

        public TimingCookie(ExportPipelineState state, string label)
        {
            _state = state;
            _label = label;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _state.Log($"  {_label} - {_stopwatch.ElapsedMilliseconds} ms");
        }
    }
}
