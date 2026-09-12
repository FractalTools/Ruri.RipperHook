using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ruri.ShaderTools;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass180_PrepareShaderBinaries
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_seedHitsByClass = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unknownShaderTypeHashes = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_unmatchedClassNames = new(StringComparer.Ordinal);

    private static void ReconcileMaterialTextureBindings(PipelineState state, int shaderIndex, SerializedProgramData metadata)
    {
        bool hasPmi = state.ShaderParameterMapInfoByArchiveIndex.TryGetValue(shaderIndex, out System.Text.Json.JsonElement pmi);
        if (s_textureBindDiagLogged.Count < 12 && s_textureBindDiagLogged.TryAdd(shaderIndex.ToString(), true))
        {
            string props = hasPmi && pmi.ValueKind == System.Text.Json.JsonValueKind.Object
                ? string.Join(",", pmi.EnumerateObject().Select(p => $"{p.Name}[{(p.Value.ValueKind == System.Text.Json.JsonValueKind.Array ? p.Value.GetArrayLength() : -1)}]"))
                : "(none)";
            state.Log($"    [texbind-diag] shader={shaderIndex} uesTextures={metadata.TextureParameters.Count} pmi={hasPmi} props={props}");
        }
        if (metadata.TextureParameters.Count == 0) return;
        if (!hasPmi) return;

        var slots = new List<int>();
        foreach (string arrayName in new[] { "TextureSamplers", "SRVs" })
        {
            if (!pmi.TryGetProperty(arrayName, out System.Text.Json.JsonElement arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (System.Text.Json.JsonElement entry in arr.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("BaseIndex", out System.Text.Json.JsonElement bi)) continue;
                if (!bi.TryGetInt32(out int slot)) continue;
                if (entry.TryGetProperty("Type", out System.Text.Json.JsonElement ty)
                    && ty.ValueKind == System.Text.Json.JsonValueKind.String
                    && ty.GetString() is string typeName
                    && typeName.IndexOf("Sampler", StringComparison.OrdinalIgnoreCase) >= 0) continue;
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

    private static ConstantBufferParameter? TryReconcileGlobalsCB(EngineUbMetadata seed, System.Text.Json.JsonElement parameterMapInfo)
    {
        if (!parameterMapInfo.TryGetProperty("LooseParameterBuffers", out System.Text.Json.JsonElement loose)
            || loose.ValueKind != System.Text.Json.JsonValueKind.Array
            || loose.GetArrayLength() == 0)
        {
            return null;
        }

        System.Text.Json.JsonElement first = loose[0];
        if (!first.TryGetProperty("Parameters", out System.Text.Json.JsonElement parameters)
            || parameters.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        int seedCount = seed.ConstantBuffer!.VectorParameters.Length;
        int cookCount = parameters.GetArrayLength();
        int pairCount = Math.Min(seedCount, cookCount);
        if (pairCount == 0) return null;

        VectorParameter[] reconciled = new VectorParameter[cookCount];
        int i = 0;
        foreach (System.Text.Json.JsonElement p in parameters.EnumerateArray())
        {
            int baseIdx = p.TryGetProperty("BaseIndex", out System.Text.Json.JsonElement b) && b.ValueKind == System.Text.Json.JsonValueKind.Number
                ? b.GetInt32() : -1;
            int sizeBytes = p.TryGetProperty("Size", out System.Text.Json.JsonElement sz) && sz.ValueKind == System.Text.Json.JsonValueKind.Number
                ? sz.GetInt32() : 0;
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

        int totalSize = first.TryGetProperty("Size", out System.Text.Json.JsonElement totSz) && totSz.ValueKind == System.Text.Json.JsonValueKind.Number
            ? totSz.GetInt32()
            : seed.ConstantBuffer.Size;
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

    public static void DoPass(PipelineState state)
    {
        s_seedHitsByClass.Clear();
        s_unknownShaderTypeHashes.Clear();
        s_unmatchedClassNames.Clear();
        if (state.Library is null) throw new InvalidOperationException("Pass110 must run before Pass180.");

        string outputDir = Path.GetFullPath(state.Options.OutputDirectory);
        bool filtered = (state.Options.ShaderIndexFilter is { Count: > 0 })
                     || !string.IsNullOrWhiteSpace(state.Options.MaterialFilter);

        if (state.Options.RecreateOutputDirectory && !filtered && Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, true);
        }
        Directory.CreateDirectory(outputDir);
        state.OutputDirectory = outputDir;
        state.FailuresRoot = Path.Combine(outputDir, "_failures");

        ShaderLibrary lib = state.Library;
        HashSet<int> wantedIndices = new();
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            foreach (ShaderMapMember member in map.Members)
            {
                wantedIndices.Add(member.ArchiveShaderIndex);
            }
        }
        if (state.Options.ShaderIndexFilter is { Count: > 0 })
        {
            wantedIndices.IntersectWith(state.Options.ShaderIndexFilter);
        }

        foreach (int i in wantedIndices.OrderBy(static x => x))
        {
            byte[]? raw = lib.GetShaderCode(i);
            if (raw == null) { state.Skipped++; continue; }
            try
            {
                ShaderPrep prep = PrepareSingleShader(state, i, raw);
                state.ShaderPrepByIndex[i] = prep;
            }
            catch (Exception ex)
            {
                state.Failed++;
                state.LogError($"Shader {i}: prep exception: {ex.Message}");
            }
        }

        state.Log($"    PrepareShaderBinaries: prepped {state.ShaderPrepByIndex.Count}/{wantedIndices.Count} binaries.");

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

    private static ShaderPrep PrepareSingleShader(PipelineState state, int shaderIndex, byte[] raw)
    {
        ShaderCodeEntry entry = state.Library!.ShaderEntries[shaderIndex];
        string typeSuffix = ShaderFrequency.ToString(entry.Frequency);
        ShaderContainerInfo? container = state.ContainerByShaderIndex.TryGetValue(shaderIndex, out ShaderContainerInfo? mappedContainer)
            ? mappedContainer
            : null;
        string containerKey = container?.ContainerKey ?? $"Ungrouped_{typeSuffix}_{shaderIndex:D6}";
        string materialName = SanitizeFileStem(container?.MaterialName ?? ResolveFinalName(state, shaderIndex));
        string variantSuffix = BuildVariantSuffix(shaderIndex, container);

        string provisionalStem = $"{containerKey}_{materialName}_{variantSuffix}";
        string failureDumpDir = Path.Combine(state.FailuresRoot, provisionalStem);

        byte[] strippedCode = UnrealShaderParser.Parse(raw, out ShaderBinaryFormat detectedFormat, out UnrealShaderParser.UnrealMetadata? unrealMetadata);

        bool hadUsage = state.UsageByShaderIndex.TryGetValue(shaderIndex, out HashSet<string>? usedBy) && usedBy.Count > 0;
        MaterialSymbolSource? bestSource = hadUsage
            ? ResolveBestSymbolSource(state, usedBy!, entry.Frequency, container?.ShaderMapHash)
            : null;

        if (hadUsage && bestSource == null)
        {
            string firstMat = usedBy!.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase).First();
            state.LogError($"Shader {shaderIndex}: usage has {usedBy!.Count} material(s) (first: {firstMat}) but symbol reader returned null - material CB will be unnamed.");
        }

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
                && state.ShaderParameterMapInfoByArchiveIndex.TryGetValue(shaderIndex, out System.Text.Json.JsonElement pmi))
            {
                ConstantBufferParameter? globalsCb = TryReconcileGlobalsCB(typeSeed, pmi);
                if (globalsCb != null)
                {
                    metadata.ConstantBufferParameters.Add(globalsCb);
                }
            }
        }

        ReconcileMaterialTextureBindings(state, shaderIndex, metadata);

        uint perShaderModel = state.Options.ShaderModel;
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
            DebugDumpDirectory = state.Options.DumpFailures ? failureDumpDir : null,
            DebugDumpStem = state.Options.DumpFailures ? (bestSource != null ? "with-symbols" : "no-symbols") : null,
        };

        return new ShaderPrep
        {
            ShaderIndex = shaderIndex,
            ContainerKey = containerKey,
            MaterialName = materialName,
            VariantSuffix = variantSuffix,
            TypeSuffix = typeSuffix,
            StrippedCode = strippedCode,
            EngineOptions = engineOptions,
            ProvisionalStem = provisionalStem,
            Metadata = metadata,
            ContainerInfo = container,
            UsedBy = hadUsage ? usedBy : null,
        };
    }

    private static MaterialSymbolSource? ResolveBestSymbolSource(PipelineState state, HashSet<string> usedBy, byte frequency, string? shaderMapHash)
    {
        string shaderPlatform = frequency switch
        {
            0 or 1 or 2 or 3 or 4 or 5 => "SP_PCD3D_SM5",
            _ => string.Empty,
        };

        foreach (string material in usedBy)
        {
            MaterialSymbolSource? candidate = state.UnifiedMaterialReader?.GetSource(material, shaderPlatform, shaderMapHash)
                                            ?? state.MaterialJsonSymbolReader?.GetSource(material, shaderPlatform);
            if (candidate != null && state.MaterialJsonSymbolReader != null)
            {
                MaterialSymbolSource? jsonCandidate = state.MaterialJsonSymbolReader.GetSource(material, shaderPlatform);
                if (jsonCandidate != null && !ReferenceEquals(jsonCandidate, candidate))
                {
                    foreach (ConstantBufferParameter cb in jsonCandidate.Metadata.ConstantBufferParameters)
                    {
                        if (cb.Name.StartsWith("MaterialCollection", StringComparison.Ordinal)
                            && !candidate.Metadata.ConstantBufferParameters.Any(existing => string.Equals(existing.Name, cb.Name, StringComparison.Ordinal)))
                        {
                            candidate.Metadata.ConstantBufferParameters.Add(cb);
                        }
                    }
                }
            }
            if (candidate != null)
            {
                SerializedProgramData clone = new()
                {
                    ConstantBufferParameters = new List<ConstantBufferParameter>(candidate.Metadata.ConstantBufferParameters),
                    BufferBindingParameters = new List<BufferBindingParameter>(candidate.Metadata.BufferBindingParameters),
                    TextureParameters = new List<TextureParameter>(candidate.Metadata.TextureParameters),
                    SamplerParameters = new List<SamplerParameter>(candidate.Metadata.SamplerParameters),
                    UAVParameters = new List<UAVParameter>(candidate.Metadata.UAVParameters),
                    DescriptorSetParameters = new List<DescriptorSetParameter>(candidate.Metadata.DescriptorSetParameters),
                    EntryPoint = candidate.Metadata.EntryPoint,
                    DebugName = candidate.Metadata.DebugName,
                    UsedMaterials = new List<string>(candidate.Metadata.UsedMaterials),
                };
                return candidate with { Metadata = clone };
            }
        }
        return null;
    }

    private static string ResolveFinalName(PipelineState state, int shaderIndex)
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

internal static class ShaderFrequency
{
    public static string ToString(byte frequency) => frequency switch
    {
        0 => "VS", 1 => "HS", 2 => "DS", 3 => "PS", 4 => "GS", 5 => "CS",
        6 => "RG", 7 => "RM", 8 => "RH", 9 => "RC",
        10 => "MS", 11 => "AS",
        _ => $"Freq{frequency}",
    };
}
