using System;
using System.Diagnostics;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Orchestrator for the FModel-hook side. Pass 010 (SaveShaderArchive)
// runs from the hook itself before this — it produces the .ushaderlib
// bytes that downstream sidecars reference. Pass 030 (ResolveHashedNames)
// is a stateless utility consumed by Pass 060, not a sequencing step.
//
// Pass 020 + Pass 040 build cumulative cross-library state (the material
// graph + IoStore shader-map-hash table) which Pass 050-080 use to
// emit per-library sidecars. Pass 090 writes the once-per-session
// UnifiedShaderMetadata.json. Caching gates on ExportPipelineState
// prevent the expensive cross-library passes from rerunning.
//
// Export pipeline (FModel-hook side; needs vm.Provider):
//   Pass 010  Save IoStore archive as flat FSerializedShaderArchive  (runs from hook)
//             also stashes the archive's shader-map hashes on state for Pass020 scoping
//   Pass 040  Extract IoStore shader-map hashes -> Root.PackageShaderMapHashes (cached)
//             RUNS BEFORE 020 because 020 uses its package->hash index to scope
//             the scan to materials that actually reference the current archive
//   Pass 020  Scan material packages whose hashes intersect the current archive
//             -> Root.MaterialInterfaces (cumulative cache across hook fires)
//   Pass 030  FHashedName resolver utility                            (used by Pass 060)
//   Pass 050  Build per-library archive view -> Root.ShaderCodeArchives[lib]
//   Pass 060  Build stable shader records -> state.AssetInfo + state.StableInfo
//   Pass 070  Write `<base>.assetinfo.json` from state.AssetInfo
//   Pass 080  Write `<base>.stableinfo.json` from state.StableInfo
//   Pass 090  Write `UnifiedShaderMetadata.json`                     (once total)
internal static class ExportPipeline
{
    public static void Run(ExportPipelineState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        using (new TimingCookie(state, "Pass 040: Extract IoStore shader-map hashes")) Pass040_ExtractIoStoreShaderMapHashes.DoPass(state);
        using (new TimingCookie(state, "Pass 020: Scan material packages"))      Pass020_ScanMaterialPackages.DoPass(state);
        using (new TimingCookie(state, "Pass 050: Build shader-library metadata")) Pass050_BuildShaderLibraryMetadata.DoPass(state);
        using (new TimingCookie(state, "Pass 060: Build stable shader records"))   Pass060_BuildStableShaderRecords.DoPass(state);
        using (new TimingCookie(state, "Pass 070: Write .assetinfo.json sidecar")) Pass070_WriteAssetInfoSidecar.DoPass(state);
        using (new TimingCookie(state, "Pass 080: Write .stableinfo.json sidecar")) Pass080_WriteStableInfoSidecar.DoPass(state);
        using (new TimingCookie(state, "Pass 090: Write UnifiedShaderMetadata.json")) Pass090_WriteUnifiedMetadataJson.DoPass(state);
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
