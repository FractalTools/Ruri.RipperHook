using System;
using System.Collections.Generic;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

public sealed class LibraryDecompileOptions
{
    public string LibraryPath { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string? UnifiedMetadataPath { get; init; }
    public string? MaterialFilter { get; init; }
    public IReadOnlyCollection<int>? ShaderIndexFilter { get; init; }
    public uint ShaderModel { get; init; } = 51;
    public bool RecreateOutputDirectory { get; init; } = true;
    public bool DumpFailures { get; init; } = true;
    public bool SplitVariantsToHlslFiles { get; init; }
    public string? EngineUbMetadataDirectory { get; init; }
    public Action<string>? Log { get; init; }
    public Action<string>? LogError { get; init; }
}

public sealed record DecompileSummary(int TotalShaders, int Decompiled, int Skipped, int Failed);

internal sealed class PipelineState
{
    public LibraryDecompileOptions Options { get; }
    public Action<string> Log { get; }
    public Action<string> LogError { get; }

    public ShaderLibrary? Library { get; set; }

    public Dictionary<string, HashSet<string>> ShaderMapToAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, HashSet<byte>>> ShaderHashToAssetsByFreq { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> HashToMaterialsFromUnified { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<int, HashSet<string>> UsageByShaderIndex { get; } = new();
    public Dictionary<int, string> NameByShaderIndex { get; } = new();
    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; } = new();
    public Dictionary<string, Dictionary<int, ShaderContainerInfo>> ContainersByMapAndIndex { get; set; } = new();
    public List<ShaderMapInfo> ShaderMaps { get; } = new();

    public UnifiedMaterialReader? UnifiedMaterialReader { get; set; }
    public MaterialJsonSymbolReader? MaterialJsonSymbolReader { get; set; }

    public EngineUbMetadataRegistry EngineUbRegistry { get; set; } = EngineUbMetadataRegistry.Empty;

    public ShaderTypeSeedRegistry ShaderTypeSeedRegistry { get; set; } = ShaderTypeSeedRegistry.Empty;

    public HashNameIndex VertexFactoryTypeNameIndex { get; set; } = HashNameIndex.Empty;
    public HashNameIndex PipelineTypeNameIndex { get; set; } = HashNameIndex.Empty;

    public Dictionary<int, System.Text.Json.JsonElement> ShaderParameterMapInfoByArchiveIndex { get; } = new();

    public string GameVersionEnum { get; set; } = string.Empty;

    public Dictionary<int, ShaderPrep> ShaderPrepByIndex { get; } = new();

    public Dictionary<int, DecompileResult> DecompileResultByIndex { get; } = new();

    public int Decompiled;
    public int Skipped;
    public int Failed;
    public string FailuresRoot { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;

    public PipelineState(LibraryDecompileOptions options)
    {
        Options = options;
        Log = options.Log ?? (_ => { });
        LogError = options.LogError ?? (_ => { });
    }
}

internal sealed class ShaderContainerInfo
{
    public string ContainerKey { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string ShaderMapHash { get; init; } = string.Empty;
    public string ShaderTypeHash { get; init; } = string.Empty;
    public string ShaderTypeName { get; set; } = string.Empty;
    public string VertexFactoryTypeHash { get; init; } = string.Empty;
    public string VertexFactoryTypeName { get; set; } = string.Empty;
    public string PipelineTypeHash { get; init; } = string.Empty;
    public string PipelineTypeName { get; set; } = string.Empty;
    public int PermutationId { get; init; }
    public int ResourceIndex { get; init; }
    public byte Frequency { get; init; }
    public string ShaderHash { get; init; } = string.Empty;
}

internal sealed class ShaderMapInfo
{
    public int ShaderMapIndex { get; init; }
    public string ShaderMapHash { get; init; } = string.Empty;
    public List<string> Assets { get; init; } = new();
    public string PrimaryAsset { get; init; } = string.Empty;
    public string PrimaryName { get; init; } = string.Empty;
    public List<ShaderMapMember> Members { get; init; } = new();
    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; init; } = new();
    public string PropertiesBlock { get; set; } = string.Empty;

    public List<string> MaterialTextureOrder { get; set; } = new();

    public List<int> MaterialTextureBuckets { get; set; } = new();

    public Dictionary<string, string> MaterialCbufferValues { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> MaterialCbufferOffsets { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> MaterialCbufferPrograms { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> MaterialCbufferParams { get; set; } = new(StringComparer.Ordinal);
    public string SubShaderTags { get; set; } = string.Empty;
    public string PassCommands { get; set; } = string.Empty;
}

internal sealed class ShaderMapMember
{
    public int RelativeIndex { get; init; }    public int ArchiveShaderIndex { get; init; }}

internal sealed class ShaderPrep
{
    public required int ShaderIndex { get; init; }
    public required string ContainerKey { get; init; }
    public required string MaterialName { get; init; }
    public required string VariantSuffix { get; init; }
    public required string TypeSuffix { get; init; }
    public required byte[] StrippedCode { get; init; }
    public required DecompileOptions EngineOptions { get; init; }
    public required string ProvisionalStem { get; init; }
    public required SerializedProgramData Metadata { get; init; }
    public ShaderContainerInfo? ContainerInfo { get; init; }
    public HashSet<string>? UsedBy { get; init; }
}
