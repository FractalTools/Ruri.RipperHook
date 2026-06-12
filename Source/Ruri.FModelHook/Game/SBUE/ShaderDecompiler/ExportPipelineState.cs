using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Mutable bag passed pass-to-pass for the export pipeline. Mirrors the
// decompile side's PipelineState in shape, but the slots reflect the
// EXPORT data flow (FModel-resident asset graph + IoStore container
// reads -> per-library JSON sidecars + a once-per-session global
// UnifiedShaderMetadata.json).
//
// The same state instance is held by the FModel hook across multiple
// `ExportData_Hook` fires so cross-library work caches across runs.
// Pass 020 (IoStore hash extraction) gates on a once-only flag because
// the provider's mounted VFS set is fixed for the session. Pass 030
// (material scan) is INCREMENTAL: each shader-archive export adds the
// materials that map to its hash set into the cache; subsequent exports
// hitting the same materials skip them.
//
// Slots (numbered in execution order; see ExportPipeline.cs for the
// orchestrator that drives them):
//   - Pass 010 sets `CurrentArchiveShaderMapHashes` for the archive being
//             exported (read by Pass 030 to scope the material scan).
//   - Pass 020 fills `Root.PackageShaderMapHashes` (gated by IoStoreHashesExtracted)
//   - Pass 030 fills `Root.MaterialInterfaces` for the packages whose
//             shader-map hashes intersect the current archive's hashes;
//             cached in `LoadedMaterialCache` across hook fires so a second
//             archive that references the same material loads it once.
//   - Pass 035 fills `Root.NiagaraShaderMapHashes` from
//             FNiagaraShaderMap.ResourceHash (independent ID space; gated
//             by NiagaraBridgeExtracted)
//   - Pass 040 fills `Root.ShaderCodeArchives[entry.PathWithoutExtension]` per-library
//   - Pass 050 fills `AssetInfo` + `StableInfo` per-library
//   - Pass 060 writes `<ExportBasePath>.assetinfo.json` from AssetInfo
//   - Pass 070 writes `<ExportBasePath>.stableinfo.json` from StableInfo
//   - Pass 080 writes `<RawDataDirectory>/<projectName>/UnifiedShaderMetadata.json`
//             (idempotent — rewritten after every archive so consumers
//              running between archives always see fresh data)
internal sealed class ExportPipelineState
{
    // Inputs — set by the driver (FModel ExportData hook OR the headless CLI
    // mount) for each library hit. The pipeline depends ONLY on the CUE4Parse
    // provider abstraction, never on FModel's WPF view-model — that is what
    // lets the same passes run under a headless `DefaultFileProvider` with no
    // GUI host.
    public AbstractVfsFileProvider Provider { get; set; } = null!;
    public GameFile Entry { get; set; } = null!;
    public string ExportBasePath { get; set; } = string.Empty;

    // Directory that receives the cross-library `UnifiedShaderMetadata.json`
    // (conventionally `<RawDataDirectory>/<ProjectName>`). Provided by the
    // driver so Pass 080 doesn't have to reach into FModel UserSettings.
    public string ProjectOutputRoot { get; set; } = string.Empty;

    // Cumulative cross-library state. Same instance lives across every
    // ExportData_Hook fire so cross-archive work (IoStore hash index +
    // already-loaded material cache) is reused.
    public UnifiedShaderMetadataRoot Root { get; } = new();

    // Per-archive scoping: the shader-map hashes contained in the
    // currently-exporting `.ushaderbytecode`. Pass 010 populates this
    // (with the FIoStoreShaderCodeArchive header's `ShaderMapHashes` /
    // `FShaderCodeArchive`'s SerializedShaders.ShaderMapHashes), Pass 030
    // intersects it against `Root.PackageShaderMapHashes` to find the
    // packages worth loading. Empty -> fallback full scan.
    public HashSet<string> CurrentArchiveShaderMapHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Materials we have already loaded + extracted across previous archive
    // exports in this session. Keyed by package PathWithoutExtension. A
    // negative cache entry (null value) means we tried and failed; skip on
    // re-encounter so we don't pay the LoadPackageObject failure twice.
    // ConcurrentDictionary because Pass 030 loads + extracts candidates in
    // parallel (full-core, like Pass 035) — the cache is written from worker
    // threads.
    public ConcurrentDictionary<string, UnifiedMaterialMetadata?> LoadedMaterialCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Once-only gates.
    public bool IoStoreHashesExtracted { get; set; }
    public bool NiagaraBridgeExtracted { get; set; }
    public bool UnifiedMetadataWritten { get; set; }

    // Black-hole cache: set once Pass 005 has tried to warm the in-memory
    // material/Niagara caches from a prior run's UnifiedShaderMetadata.json.
    // Gated so the (potentially 100MB+) deserialize happens at most once per
    // session even though Pass 005 sits at the top of every archive's run.
    public bool MaterialCacheWarmed { get; set; }

    // Per-library scratch — Pass 050 populates, Pass 060/070 consume.
    public ShaderAssetInfoEquivalent? AssetInfo { get; set; }
    public ShaderStableInfoEquivalent? StableInfo { get; set; }

    // Logging shims — defaults wired to HookLogger by the hook so passes
    // stay decoupled from the logger sink (matches the decompile side's
    // `Action<string> Log` / `Action<string> LogError` shape).
    public Action<string> Log { get; set; } = _ => { };
    public Action<string> LogError { get; set; } = _ => { };
}
