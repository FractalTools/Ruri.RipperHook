using System;
using System.Collections.Generic;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// All DTO shapes for the FModel-hook export pipeline (Pass 010 - Pass 100).
// These types are pure data — they hold what gets serialised to
// `UnifiedShaderMetadata.json` / `.assetinfo.json` / `.stableinfo.json`
// and what's passed between the build/write passes inside the host
// process. No logic lives here.
//
// Why a single file: the DTO graph is densely cross-referenced — a split
// would create tight one-way using-cycles between every pass file.
// Keeping them together here lets each Pass file own ONLY its own
// orchestration code.

internal sealed class UnifiedShaderMetadataRoot
{
    public Dictionary<string, List<string>> PackageShaderMapHashes { get; set; } = new();
    public Dictionary<string, UnifiedMaterialMetadata> MaterialInterfaces { get; set; } = new();
    public Dictionary<string, UnifiedShaderLibraryMetadata> ShaderCodeArchives { get; set; } = new();
}

internal sealed class UnifiedShaderLibraryMetadata
{
    public string LibraryPath { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string LibraryType { get; set; } = string.Empty;
    public List<string> ShaderMapHashes { get; set; } = new();
    public List<string> ShaderHashes { get; set; } = new();
    public List<UnifiedShaderMapArchiveEntry> ShaderMapEntries { get; set; } = new();
    public List<UnifiedShaderArchiveEntry> ShaderEntries { get; set; } = new();
    public List<uint> ShaderIndices { get; set; } = new();
}

internal sealed class ShaderAssetInfoEquivalent
{
    public int AssetInfoVersion { get; set; } = 2;
    public List<ShaderAssetInfoEntry> ShaderCodeToAssets { get; set; } = new();
}

internal sealed class ShaderAssetInfoEntry
{
    public string ShaderMapHash { get; set; } = string.Empty;
    public List<string> Assets { get; set; } = new();
}

internal sealed class ShaderStableInfoEquivalent
{
    public string LibraryPath { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string LibraryType { get; set; } = string.Empty;
    public List<ShaderStableInfoEntry> ShaderMaps { get; set; } = new();
}

internal sealed class ShaderStableInfoEntry
{
    public string ShaderMapHash { get; set; } = string.Empty;
    public List<string> Assets { get; set; } = new();
    public List<string> ShaderHashes { get; set; } = new();
    public List<byte> Frequencies { get; set; } = new();
    public List<StableShaderRecord> Shaders { get; set; } = new();
    public List<string> Types { get; set; } = new();
    public List<string> VertexFactoryTypes { get; set; } = new();
    public List<string> ShaderTypeHashes { get; set; } = new();
    public List<string> UniformBufferParameterStructHashes { get; set; } = new();
}

internal sealed class StableShaderRecord
{
    public int ArchiveShaderIndex { get; set; } = -1;
    public int ResourceIndex { get; set; } = -1;
    public string ShaderHash { get; set; } = string.Empty;
    public byte Frequency { get; set; }
    public string ShaderTypeHash { get; set; } = string.Empty;
    public string ShaderTypeName { get; set; } = string.Empty;
    public string VertexFactoryTypeHash { get; set; } = string.Empty;
    public string VertexFactoryTypeName { get; set; } = string.Empty;
    public int PermutationId { get; set; } = -1;
    public string PipelineTypeHash { get; set; } = string.Empty;
    public string PipelineTypeName { get; set; } = string.Empty;
    public string ContainerKey { get; set; } = string.Empty;
}

internal sealed class UnifiedShaderMapArchiveEntry
{
    public uint ShaderIndicesOffset { get; set; }
    public uint NumShaders { get; set; }
    public uint FirstPreloadIndex { get; set; }
    public uint NumPreloadEntries { get; set; }
}

internal sealed class UnifiedShaderArchiveEntry
{
    public ulong Offset { get; set; }
    public uint Size { get; set; }
    public uint UncompressedSize { get; set; }
    public byte Frequency { get; set; }
}

internal sealed class UnifiedMaterialMetadata
{
    public string MaterialPath { get; set; } = string.Empty;
    public List<UnifiedShaderMapMetadata> LoadedShaderMaps { get; set; } = new();
}

internal sealed class UnifiedShaderMapMetadata
{
    public string? ShaderPlatform { get; set; }
    public string? CookedShaderMapIdHash { get; set; }
    public string? ShaderContentHash { get; set; }
    public UnifiedPointerTable? ShaderMapPointerTable { get; set; }
    public UnifiedFrozenArchive? MemoryImageResult { get; set; }
    public UnifiedShaderContent? MaterialShaderMapContent { get; set; }
}

internal sealed class UnifiedPointerTable
{
    public List<UnifiedHashName> Types { get; set; } = new();
    public List<UnifiedHashName> VertexFactoryTypes { get; set; } = new();
    public List<UnifiedTypeDependency> TypeDependencies { get; set; } = new();
}

internal sealed class UnifiedHashName
{
    public string Hash { get; set; } = string.Empty;
    public string? Name { get; set; }
}

internal sealed class UnifiedTypeDependency
{
    public string Name { get; set; } = string.Empty;
    public uint SavedLayoutSize { get; set; }
    public string SavedLayoutHash { get; set; } = string.Empty;
}

internal sealed class UnifiedFrozenArchive
{
    public string FrozenObjectBase64 { get; set; } = string.Empty;
    public List<UnifiedFrozenName> ScriptNames { get; set; } = new();
    public List<UnifiedFrozenName> MinimalNames { get; set; } = new();
    public List<UnifiedFrozenVTable> VTables { get; set; } = new();
}

internal sealed class UnifiedFrozenName
{
    public string Name { get; set; } = string.Empty;
    public List<int> Patches { get; set; } = new();
}

internal sealed class UnifiedFrozenVTable
{
    public string TypeNameHash { get; set; } = string.Empty;
    public List<UnifiedFrozenVTablePatch> Patches { get; set; } = new();
}

internal sealed class UnifiedFrozenVTablePatch
{
    public int Offset { get; set; }
    public int VTableOffset { get; set; }
}

internal sealed class UnifiedShaderContent
{
    public UnifiedUniformExpressionSet? UniformExpressionSet { get; set; }
    public List<string> ShaderTypeHashes { get; set; } = new();
    public List<int> ShaderPermutations { get; set; } = new();
    public List<UnifiedShader> Shaders { get; set; } = new();
    public List<UnifiedShaderPipeline> ShaderPipelines { get; set; } = new();
    public List<UnifiedOrderedMeshShaderMap> OrderedMeshShaderMaps { get; set; } = new();
}

internal sealed class UnifiedShaderPipeline
{
    public string TypeHash { get; set; } = string.Empty;
    public List<UnifiedShader> Shaders { get; set; } = new();
    public List<int> PermutationIds { get; set; } = new();
}

internal sealed class UnifiedUniformExpressionSet
{
    public List<UnifiedMaterialUniformPreshaderHeader> UniformPreshaders { get; set; } = new();
    public List<UnifiedMaterialUniformPreshaderField> UniformPreshaderFields { get; set; } = new();
    public List<UnifiedMaterialNumericParameter> UniformNumericParameters { get; set; } = new();
    public List<List<UnifiedMaterialTextureParameter>> UniformTextureParameters { get; set; } = new();
    public List<UnifiedMaterialExternalTextureParameter> UniformExternalTextureParameters { get; set; } = new();
    public List<UnifiedMaterialTextureCollectionParameter> UniformTextureCollectionParameters { get; set; } = new();
    public List<string> ParameterCollections { get; set; } = new();
    public uint UniformPreshaderBufferSize { get; set; }
    public UnifiedUniformBufferLayoutInitializer? UniformBufferLayoutInitializer { get; set; }
    public UnifiedMaterialPreshaderData? UniformPreshaderData { get; set; }
}

internal sealed class UnifiedMaterialUniformPreshaderHeader
{
    public uint OpcodeOffset { get; set; }
    public uint OpcodeSize { get; set; }
    public uint? FieldIndex { get; set; }
    public uint? NumFields { get; set; }
    public uint? BufferOffset { get; set; }
    public string? ComponentType { get; set; }
    public byte? NumComponents { get; set; }
    public string? Type { get; set; }
}

internal sealed class UnifiedMaterialUniformPreshaderField
{
    public uint BufferOffset { get; set; }
    public uint ComponentIndex { get; set; }
    public string Type { get; set; } = string.Empty;
}

internal sealed class UnifiedMaterialNumericParameter
{
    public string ParameterName { get; set; } = string.Empty;
    public string Association { get; set; } = string.Empty;
    public int Index { get; set; }
    public string ParameterType { get; set; } = string.Empty;
    public uint DefaultValueOffset { get; set; }
    public object? Value { get; set; }
}

internal sealed class UnifiedMaterialTextureParameter
{
    public string ParameterName { get; set; } = string.Empty;
    public string Association { get; set; } = string.Empty;
    public int Index { get; set; }
    public int TextureIndex { get; set; }
    public string SamplerSource { get; set; } = string.Empty;
    public byte VirtualTextureLayerIndex { get; set; }
}

internal sealed class UnifiedMaterialExternalTextureParameter
{
    public string ParameterName { get; set; } = string.Empty;
    public string ExternalTextureGuid { get; set; } = string.Empty;
    public int SourceTextureIndex { get; set; }
}

internal sealed class UnifiedMaterialTextureCollectionParameter
{
    public int TextureCollectionIndex { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public string Association { get; set; } = string.Empty;
    public int Index { get; set; }
    public bool IsVirtualCollection { get; set; }
}

internal sealed class UnifiedMaterialPreshaderData
{
    public List<string> Names { get; set; } = new();
    public List<uint> NamesOffset { get; set; } = new();
    public List<UnifiedPreshaderStructType> StructTypes { get; set; } = new();
    public List<string> StructComponentTypes { get; set; } = new();
    public string Data { get; set; } = string.Empty;
    public bool IsPreshader2 { get; set; }
}

internal sealed class UnifiedUniformBufferLayoutInitializer
{
    public string Name { get; set; } = string.Empty;
    public List<UnifiedUniformBufferResource> Resources { get; set; } = new();
    public List<UnifiedUniformBufferResource> GraphResources { get; set; } = new();
    public List<UnifiedUniformBufferResource> GraphTextures { get; set; } = new();
    public List<UnifiedUniformBufferResource> GraphBuffers { get; set; } = new();
    public List<UnifiedUniformBufferResource> GraphUniformBuffers { get; set; } = new();
    public List<UnifiedUniformBufferResource> UniformBuffers { get; set; } = new();
    public uint Hash { get; set; }
    public uint ConstantBufferSize { get; set; }
    public ushort RenderTargetsOffset { get; set; }
    public byte StaticSlot { get; set; }
    public string BindingFlags { get; set; } = string.Empty;
    public bool HasNonGraphOutputs { get; set; }
    public bool NoEmulatedUniformBuffer { get; set; }
    public bool UniformView { get; set; }
}

internal sealed class UnifiedUniformBufferResource
{
    public ushort MemberOffset { get; set; }
    public string MemberType { get; set; } = string.Empty;
}

internal sealed class UnifiedPreshaderStructType
{
    public string Hash { get; set; } = string.Empty;
    public int ComponentTypeIndex { get; set; }
    public int NumComponents { get; set; }
}

internal sealed class UnifiedLinearColor
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }
}

internal sealed class UnifiedVector4
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double W { get; set; }
}

internal sealed class UnifiedOrderedMeshShaderMap
{
    public UnifiedHashName? VertexFactoryType { get; set; }
    public List<UnifiedHashName> ShaderTypes { get; set; } = new();
    public List<int> ShaderPermutations { get; set; } = new();
    public List<UnifiedShader> Shaders { get; set; } = new();
}

internal sealed class UnifiedShader
{
    public int ResourceIndex { get; set; }
    public uint NumInstructions { get; set; }
    public uint SortKey { get; set; }
    public string TypeHash { get; set; } = string.Empty;
    public string VertexFactoryTypeHash { get; set; } = string.Empty;
    public List<string> UniformBufferParameterStructHashes { get; set; } = new();
    public List<UnifiedHashName> UniformBufferParameterStructs { get; set; } = new();
    public List<ushort> UniformBufferParameterBaseIndices { get; set; } = new();
    public UnifiedShaderBindings? Bindings { get; set; }
    public UnifiedShaderParameterMapInfo? ParameterMapInfo { get; set; }
}

internal sealed class UnifiedShaderBindings
{
    public List<UnifiedBindingParameter> Parameters { get; set; } = new();
    public List<UnifiedResourceBindingParameter> ResourceParameters { get; set; } = new();
    public List<UnifiedBindlessResourceParameter> BindlessResourceParameters { get; set; } = new();
    public List<UnifiedParameterStructReference> GraphUniformBuffers { get; set; } = new();
    public List<UnifiedParameterStructReference> ParameterReferences { get; set; } = new();
    public uint StructureLayoutHash { get; set; }
    public ushort RootParameterBufferIndex { get; set; }
}

internal sealed class UnifiedBindingParameter
{
    public ushort BufferIndex { get; set; }
    public ushort BaseIndex { get; set; }
    public ushort ByteOffset { get; set; }
    public ushort ByteSize { get; set; }
}

internal sealed class UnifiedResourceBindingParameter
{
    public ushort ByteOffset { get; set; }
    public byte BaseIndex { get; set; }
    public string BaseType { get; set; } = string.Empty;
}

internal sealed class UnifiedBindlessResourceParameter
{
    public ushort ByteOffset { get; set; }
    public ushort GlobalConstantOffset { get; set; }
    public string BaseType { get; set; } = string.Empty;
}

internal sealed class UnifiedParameterStructReference
{
    public ushort BufferIndex { get; set; }
    public ushort ByteOffset { get; set; }
}

internal sealed class UnifiedShaderParameterMapInfo
{
    public List<UnifiedShaderParameterInfo> UniformBuffers { get; set; } = new();
    public List<UnifiedShaderResourceParameterInfo> TextureSamplers { get; set; } = new();
    public List<UnifiedShaderResourceParameterInfo> SRVs { get; set; } = new();
    public List<UnifiedShaderLooseParameterBufferInfo> LooseParameterBuffers { get; set; } = new();
    public string Hash { get; set; } = string.Empty;
}

internal class UnifiedShaderParameterInfo
{
    public ushort BaseIndex { get; set; }
    public ushort Size { get; set; }
}

internal sealed class UnifiedShaderResourceParameterInfo : UnifiedShaderParameterInfo
{
    public byte BufferIndex { get; set; }
    public byte Type { get; set; }
}

internal sealed class UnifiedShaderLooseParameterBufferInfo
{
    public ushort BaseIndex { get; set; }
    public ushort Size { get; set; }
    public List<UnifiedShaderParameterInfo> Parameters { get; set; } = new();
}

internal sealed class StableShaderTruthAggregate
{
    public HashSet<string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> VertexFactoryTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ShaderTypeHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UniformBufferParameterStructHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(UnifiedShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.TypeHash))
        {
            Types.Add(shader.TypeHash);
            ShaderTypeHashes.Add(shader.TypeHash);
        }

        if (!string.IsNullOrWhiteSpace(shader.VertexFactoryTypeHash) && !string.Equals(shader.VertexFactoryTypeHash, "0000000000000000", StringComparison.OrdinalIgnoreCase))
        {
            VertexFactoryTypes.Add(shader.VertexFactoryTypeHash);
        }

        foreach (string hash in shader.UniformBufferParameterStructHashes)
        {
            if (!string.IsNullOrWhiteSpace(hash))
            {
                UniformBufferParameterStructHashes.Add(hash);
            }
        }
    }
}
