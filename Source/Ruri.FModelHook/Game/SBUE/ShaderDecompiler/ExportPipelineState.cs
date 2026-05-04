using System;
using FModel.ViewModels;
using CUE4Parse.FileProvider.Objects;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Mutable bag passed pass-to-pass for the export pipeline. Mirrors the
// decompile side's PipelineState in shape, but the slots reflect the
// EXPORT data flow (FModel-resident asset graph + IoStore container
// reads -> per-library JSON sidecars + a once-per-session global
// UnifiedShaderMetadata.json).
//
// The same state instance is held by the FModel hook across multiple
// `ExportData_Hook` fires so cross-library work (material scan, IoStore
// shader-map-hash extraction) only happens once. Three boolean gates
// (`MaterialsScanned`, `IoStoreHashesExtracted`, `UnifiedMetadataWritten`)
// short-circuit the corresponding passes after their first run.
//
// Slots:
//   - Pass 020 fills `Root.MaterialInterfaces` (gated by MaterialsScanned)
//   - Pass 040 fills `Root.PackageShaderMapHashes` (gated by IoStoreHashesExtracted)
//   - Pass 050 fills `Root.ShaderCodeArchives[entry.PathWithoutExtension]` per-library
//   - Pass 060 fills `AssetInfo` + `StableInfo` per-library
//   - Pass 070 writes `<ExportBasePath>.assetinfo.json` from AssetInfo
//   - Pass 080 writes `<ExportBasePath>.stableinfo.json` from StableInfo
//   - Pass 090 writes `<RawDataDirectory>/<projectName>/UnifiedShaderMetadata.json`
//             (gated by UnifiedMetadataWritten)
internal sealed class ExportPipelineState
{
    // Inputs — replaced by the hook for each library hit.
    public CUE4ParseViewModel Vm { get; set; } = null!;
    public GameFile Entry { get; set; } = null!;
    public string ExportBasePath { get; set; } = string.Empty;

    // Cumulative cross-library state. Same instance lives across every
    // ExportData_Hook fire so the expensive material scan + IoStore
    // hash extraction only run once per FModel session.
    public UnifiedShaderMetadataRoot Root { get; } = new();

    // Once-only gates.
    public bool MaterialsScanned { get; set; }
    public bool IoStoreHashesExtracted { get; set; }
    public bool UnifiedMetadataWritten { get; set; }

    // Per-library scratch — Pass 060 populates, Pass 070/080 consume.
    public ShaderAssetInfoEquivalent? AssetInfo { get; set; }
    public ShaderStableInfoEquivalent? StableInfo { get; set; }

    // Logging shims — defaults wired to HookLogger by the hook so passes
    // stay decoupled from the logger sink (matches the decompile side's
    // `Action<string> Log` / `Action<string> LogError` shape).
    public Action<string> Log { get; set; } = _ => { };
    public Action<string> LogError { get; set; } = _ => { };
}
