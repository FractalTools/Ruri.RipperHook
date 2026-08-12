using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal sealed class ExportPipelineState
{
    public AbstractVfsFileProvider Provider { get; set; } = null!;
    public GameFile Entry { get; set; } = null!;
    public string ExportBasePath { get; set; } = string.Empty;

    public string ProjectOutputRoot { get; set; } = string.Empty;

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
