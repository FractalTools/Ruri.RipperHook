using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Material;
using Ruri.ShaderTools;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class ShaderBinaries
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_seedHitsByClass = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unknownShaderTypeHashes = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unmatchedClassNames = new(StringComparer.Ordinal);

    private static void ReconcileMaterialTextureBindings(ShaderSourceState state, int shaderIndex, SerializedProgramData metadata)
    {
        bool hasPmi = state.ShaderParameterMapInfoByArchiveIndex.TryGetValue(shaderIndex, out FShaderParameterMapInfo? pmi);
        if (s_textureBindDiagLogged.Count < 12 && s_textureBindDiagLogged.TryAdd(shaderIndex.ToString(), true))
        {
            string props = hasPmi
                ? $"UniformBuffers[{pmi!.UniformBuffers?.Length ?? -1}],TextureSamplers[{pmi.TextureSamplers?.Length ?? -1}],SRVs[{pmi.SRVs?.Length ?? -1}],LooseParameterBuffers[{pmi.LooseParameterBuffers?.Length ?? -1}]"
                : "(none)";
            state.Log($"    [texbind-diag] shader={shaderIndex} uesTextures={metadata.TextureParameters.Count} pmi={hasPmi} props={props}");
        }
        if (metadata.TextureParameters.Count == 0) return;
        if (!hasPmi) return;

        var slots = new List<int>();
        foreach (FShaderParameterInfo[]? bindings in new[] { pmi!.TextureSamplers, pmi.SRVs })
        {
            foreach (FShaderParameterInfo entry in bindings ?? [])
            {
                if (entry is FShaderResourceParameterInfo { Type: EShaderParameterType.Sampler or EShaderParameterType.BindlessSampler }) continue;
                int slot = entry.BaseIndex;
                if (!slots.Contains(slot)) slots.Add(slot);
            }
        }
        if (slots.Count == 0) return;
        slots.Sort();

        if (slots.Count != metadata.TextureParameters.Count)
        {
            if (s_textureBindMismatchLogged.TryAdd(metadata.DebugName ?? shaderIndex.ToString(), true))
            {
                state.Log($"    [texbind] {metadata.DebugName}: UES 贴图 {metadata.TextureParameters.Count} 个 vs cook 资源槽 {slots.Count} 个 — 数量不等,保持匿名(拒绝按位错标)。");
            }
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            metadata.TextureParameters[i].Index = slots[i];
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_textureBindMismatchLogged = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_textureBindDiagLogged = new();

    private static ConstantBufferParameter? TryReconcileGlobalsCB(EngineUbMetadata seed, FShaderParameterMapInfo parameterMapInfo)
    {
        if (parameterMapInfo.LooseParameterBuffers is not { Length: > 0 } loose)
        {
            return null;
        }

        FShaderLooseParameterBufferInfo first = loose[0];
        if (first.Parameters is not { } parameters)
        {
            return null;
        }

        int seedCount = seed.ConstantBuffer!.VectorParameters.Length;
        int cookCount = parameters.Length;
        int pairCount = Math.Min(seedCount, cookCount);
        if (pairCount == 0) return null;

        VectorParameter[] reconciled = new VectorParameter[cookCount];
        int i = 0;
        foreach (FShaderLooseParameterInfo p in parameters)
        {
            int baseIdx = p.BaseIndex;
            int sizeBytes = p.Size;
            if (baseIdx < 0 || sizeBytes <= 0) return null;

            int rowCount = Math.Clamp(sizeBytes / 4, 1, 4);
            if (i < pairCount)
            {
                VectorParameter src = seed.ConstantBuffer.VectorParameters[i];
                reconciled[i] = new VectorParameter
                {
                    Name = src.Name,
                    NameIndex = -1,
                    Type = src.Type,
                    Index = baseIdx,
                    ArraySize = src.ArraySize,
                    IsMatrix = false,
                    RowCount = (byte)rowCount,
                    ColumnCount = 1,
                };
            }
            else
            {
                reconciled[i] = new VectorParameter
                {
                    Name = $"_loose_at_c{baseIdx / 16}",
                    NameIndex = -1,
                    Type = ShaderParamType.Float,
                    Index = baseIdx,
                    ArraySize = 0,
                    IsMatrix = false,
                    RowCount = (byte)rowCount,
                    ColumnCount = 1,
                };
            }
            i++;
        }

        int totalSize = first.Size > 0 ? first.Size : seed.ConstantBuffer.Size;
        return new ConstantBufferParameter
        {
            Name = "$Globals",
            NameIndex = -1,
            VectorParameters = reconciled,
            MatrixParameters = Array.Empty<MatrixParameter>(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = totalSize,
            IsPartialCB = false,
        };
    }

    public static void Build(ShaderSourceState state)
    {
        s_seedHitsByClass.Clear();
        s_unknownShaderTypeHashes.Clear();
        s_unmatchedClassNames.Clear();

        Directory.CreateDirectory(state.OutputDirectory);

        ShaderLibrary lib = state.Library;
        int wanted = 0;
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            MaterialSymbolSource? symbols = MaterialSymbols.Of(map);
            if (symbols is null && map.UniformExpressions is not null)
            {
                state.LogError($"Shader map {map.ShaderMapHash} ({map.PrimaryAsset}) states an expression set but no symbols came of it - material CB will be unnamed.");
            }
            foreach (ShaderMapMember member in map.Members)
            {
                int i = member.ArchiveShaderIndex;
                if (state.ShaderPrepByIndex.ContainsKey(i)) continue;
                wanted++;
                byte[]? raw = lib.GetShaderCode(i);
                if (raw == null) { state.Skipped++; continue; }
                try
                {
                    state.ShaderPrepByIndex[i] = PrepareSingleShader(state, i, raw, symbols);
                }
                catch (Exception ex)
                {
                    state.Failed++;
                    state.LogError($"Shader {i}: prep exception: {ex.Message}");
                }
            }
        }

        state.Log($"    PrepareShaderBinaries: prepped {state.ShaderPrepByIndex.Count}/{wanted} binaries.");

        if (state.ShaderTypeSeedRegistry.HashToNameCount > 0)
        {
            int unknown = s_unknownShaderTypeHashes.Count;
            int unmatched = s_unmatchedClassNames.Count;
            int matched = s_seedHitsByClass.Count;
            state.Log($"    ShaderType seed coverage: matched-classes={matched} unmatched-class-with-name={unmatched} unknown-hashes={unknown}");
            int limit = 5;
            foreach (string h in s_unknownShaderTypeHashes.Keys)
            {
                if (limit-- <= 0) break;
                state.Log($"      unknown-hash={h} (generator's IMPLEMENT_*_SHADER_TYPE scan missed this class)");
            }
            int unmatchedLimit = 50;
            foreach (string n in s_unmatchedClassNames.Keys)
            {
                if (unmatchedLimit-- <= 0) { state.Log($"      ... ({unmatched - 50} more unmatched-class-with-name not shown)"); break; }
                state.Log($"      unmatched-with-name={n}");
            }
        }
    }

    private static ShaderPrep PrepareSingleShader(ShaderSourceState state, int shaderIndex, byte[] raw, MaterialSymbolSource? symbols)
    {
        ShaderContainerInfo? container = state.ContainerByShaderIndex.TryGetValue(shaderIndex, out ShaderContainerInfo? mappedContainer)
            ? mappedContainer
            : null;
        string containerKey = container?.ContainerKey ?? $"Ungrouped_{shaderIndex:D6}";
        string materialName = SanitizeFileStem(container?.MaterialName ?? ResolveFinalName(state, shaderIndex));
        string variantSuffix = BuildVariantSuffix(shaderIndex, container);

        string provisionalStem = $"{containerKey}_{materialName}_{variantSuffix}";
        string failureDumpDir = Path.Combine(state.FailuresRoot, provisionalStem);

        byte[] strippedCode = UnrealShaderParser.Parse(raw, out ShaderBinaryFormat detectedFormat, out UnrealShaderParser.UnrealMetadata? unrealMetadata);

        state.UsageByShaderIndex.TryGetValue(shaderIndex, out HashSet<string>? usedBy);
        MaterialSymbolSource? bestSource = symbols is null ? null : symbols with
        {
            Metadata = Clone(symbols.Metadata),
        };

        SerializedProgramData metadata = SubProgramMetadataReader.Read(unrealMetadata, bestSource, state.EngineUbRegistry, state.Log);

        if (container != null
            && !string.IsNullOrWhiteSpace(container.ShaderTypeHash)
            && state.ShaderTypeSeedRegistry.HashToNameCount > 0)
        {
            string? resolvedName = state.ShaderTypeSeedRegistry.ResolveTypeName(container.ShaderTypeHash);
            if (resolvedName == null)
            {
                s_unknownShaderTypeHashes.TryAdd(container.ShaderTypeHash, true);
            }
            else
            {
                if (state.ShaderTypeSeedRegistry.TryLookupWithFallback(
                        container.ShaderTypeHash, container.ShaderTypeName,
                        out EngineUbMetadata _, out string _))
                {
                }
                else
                {
                    s_unmatchedClassNames.TryAdd(resolvedName, true);
                }
            }
        }

        if (container != null
            && !string.IsNullOrWhiteSpace(container.ShaderTypeHash)
            && state.ShaderTypeSeedRegistry.FileCount > 0
            && state.ShaderTypeSeedRegistry.TryLookupWithFallback(
                container.ShaderTypeHash, container.ShaderTypeName,
                out EngineUbMetadata typeSeed, out string matchKind))
        {
            string key = $"{container.ShaderTypeName}=>{typeSeed.Name}";
            if (s_seedHitsByClass.TryAdd(key, true))
            {
                int loose = typeSeed.ConstantBuffer?.VectorParameters?.Length ?? 0;
                int tex = (typeSeed.Textures?.Count ?? 0) + (typeSeed.Samplers?.Count ?? 0);
                int buf = (typeSeed.Buffers?.Count ?? 0) + (typeSeed.UAVs?.Count ?? 0);
                state.Log($"[ShaderTypeSeed-hit] cookName={container.ShaderTypeName} via={matchKind} seedClass={typeSeed.Name} loose-params={loose} resources={tex + buf}");
            }

            if (typeSeed.ConstantBuffer != null
                && typeSeed.ConstantBuffer.VectorParameters != null
                && typeSeed.ConstantBuffer.VectorParameters.Length > 0
                && state.ShaderParameterMapInfoByArchiveIndex.TryGetValue(shaderIndex, out FShaderParameterMapInfo? pmi))
            {
                ConstantBufferParameter? globalsCb = TryReconcileGlobalsCB(typeSeed, pmi!);
                if (globalsCb != null)
                {
                    metadata.ConstantBufferParameters.Add(globalsCb);
                }
            }
        }

        ReconcileMaterialTextureBindings(state, shaderIndex, metadata);

        uint perShaderModel = state.Request.ShaderModel;
        bool optionallyMarkedSm6 = unrealMetadata?.IsSm6Shader == true;
        if (optionallyMarkedSm6 || detectedFormat == ShaderBinaryFormat.Dxil)
        {
            if (perShaderModel < 67) perShaderModel = 67;
        }

        EngineDecompileOptions engineOptions = new()
        {
            Format = detectedFormat,
            Symbols = metadata,
            ShaderModel = perShaderModel,
            SymbolEnricher = static (spv, symbols) => MaterialTextureNameInferrer.InferAndAppend(spv, symbols),
            DebugDumpDirectory = state.Request.DumpFailures ? failureDumpDir : null,
            DebugDumpStem = state.Request.DumpFailures ? (bestSource != null ? "with-symbols" : "no-symbols") : null,
        };

        return new ShaderPrep
        {
            ShaderIndex = shaderIndex,
            ContainerKey = containerKey,
            MaterialName = materialName,
            VariantSuffix = variantSuffix,
            StrippedCode = strippedCode,
            EngineOptions = engineOptions,
            ProvisionalStem = provisionalStem,
            Metadata = metadata,
            ContainerInfo = container,
            UsedBy = usedBy,
        };
    }

    /// <summary>A per-shader copy, because the decompiler fills the symbols it is handed.</summary>
    private static SerializedProgramData Clone(SerializedProgramData source) => new()
    {
        ConstantBufferParameters = new List<ConstantBufferParameter>(source.ConstantBufferParameters),
        BufferBindingParameters = new List<BufferBindingParameter>(source.BufferBindingParameters),
        TextureParameters = new List<TextureParameter>(source.TextureParameters),
        SamplerParameters = new List<SamplerParameter>(source.SamplerParameters),
        UAVParameters = new List<UAVParameter>(source.UAVParameters),
        DescriptorSetParameters = new List<DescriptorSetParameter>(source.DescriptorSetParameters),
        EntryPoint = source.EntryPoint,
        DebugName = source.DebugName,
        UsedMaterials = new List<string>(source.UsedMaterials),
    };

    private static string ResolveFinalName(ShaderSourceState state, int shaderIndex)
    {
        if (state.NameByShaderIndex.TryGetValue(shaderIndex, out string? mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }
        if (state.UsageByShaderIndex.TryGetValue(shaderIndex, out HashSet<string>? materials) && materials.Count > 0)
        {
            string first = materials.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase).First();
            string fileName = Path.GetFileNameWithoutExtension(first);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return "Shader";
    }

    private static string BuildVariantSuffix(int shaderIndex, ShaderContainerInfo? container)
    {
        if (container == null)
        {
            return $"idx{shaderIndex:D6}";
        }

        string perm = container.PermutationId >= 0 ? $"perm{container.PermutationId}" : "permNA";
        string res = container.ResourceIndex >= 0 ? $"res{container.ResourceIndex}" : "resNA";
        return $"{perm}_{res}_idx{shaderIndex:D6}";
    }

    private static string SanitizeFileStem(string value)
    {
        return string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }
}
