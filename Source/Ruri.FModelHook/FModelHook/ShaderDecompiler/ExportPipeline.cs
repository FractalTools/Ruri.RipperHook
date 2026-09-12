using System;
using System.Diagnostics;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class ExportPipeline
{
    public static void Run(ExportPipelineState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        using (new TimingCookie(state, "Pass 005: Warm material cache from disk"))     Pass005_WarmMaterialCacheFromDisk.DoPass(state);
        using (new TimingCookie(state, "Pass 020: Extract IoStore shader-map hashes")) Pass020_ExtractIoStoreShaderMapHashes.DoPass(state);
        using (new TimingCookie(state, "Pass 030: Scan material packages"))            Pass030_ScanMaterialPackages.DoPass(state);
        using (new TimingCookie(state, "Pass 035: Extract Niagara shader-map bridge")) Pass035_ExtractNiagaraShaderMapBridge.DoPass(state);
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
