using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;

namespace Ruri.FModelHook.ShaderDecompiler;

internal sealed class ExportPipelineState
{
    public AbstractVfsFileProvider Provider { get; set; } = null!;
    public GameFile Entry { get; set; } = null!;
    public string ExportBasePath { get; set; } = string.Empty;

    public string ProjectOutputRoot { get; set; } = string.Empty;

    /// <summary>
    /// The material packages this run is ABOUT, or null to mean the whole install. Answering
    /// "which material compiled to this shader map" by reading every material package a title
    /// ships is the right cost when the answer is wanted for every material; it is the whole
    /// install's cost paid for nothing when a caller wants one character. A caller that already
    /// knows which materials it cares about states them here and the bridge is built over those.
    /// </summary>
    public IReadOnlyCollection<string>? MaterialScope { get; set; }

    public UnifiedShaderMetadataRoot Root { get; } = new();

    public HashSet<string> CurrentArchiveShaderMapHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, UnifiedMaterialMetadata?> LoadedMaterialCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IoStoreHashesExtracted { get; set; }
    public bool NiagaraBridgeExtracted { get; set; }
    public bool MaterialScanComplete { get; set; }
    public bool UnifiedMetadataWritten { get; set; }

    public bool MaterialCacheWarmed { get; set; }

    public ShaderAssetInfoEquivalent? AssetInfo { get; set; }
    public ShaderStableInfoEquivalent? StableInfo { get; set; }

    public Action<string> Log { get; set; } = _ => { };
    public Action<string> LogError { get; set; } = _ => { };
}
