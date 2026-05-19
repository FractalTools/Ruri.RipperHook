using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ruri.ShaderTools;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 180 — Phase 1 of the decompile pipeline: walk the wanted shader
// indices, strip the UE shader-binary wrapper, build per-binary
// SerializedProgramData metadata + EngineDecompileOptions, and stash the
// result in `state.ShaderPrepByIndex`.
//
// Per-shader prep:
//   1. Strip the UE shader-binary wrapper -> clean DXBC/DXIL bytes +
//      UnrealMetadata (FShaderResourceTable, optional UB names, etc.)
//   2. Pick the best material symbol source (UnifiedMaterialReader ->
//      MaterialJsonSymbolReader). Both are caches; first lookup pays
//      the JSON-parse cost, repeats are O(1).
//   3. Assemble final SerializedProgramData by combining:
//        - runtime symbols (FShaderCodeUniformBuffers + SRT-decoded
//          texture/sampler/UAV names, refined by the material's
//          MaterialUniformBufferLayout when available)
//        - the material's own ConstantBuffer schemas
//   4. Compose EngineDecompileOptions with `MaterialTextureNameInferrer.
//      InferAndAppend` as the MetadataEnricher so UE's OpSampledImage-pair
//      texture-name recovery plugs in at the right point (post-rewrite,
//      pre-symbol-patch) without the engine itself knowing anything
//      UE-specific.
//
// Shared readers/services live outside the pass; this file keeps only the
// prep orchestration and a tiny local stage-name helper.
internal static class Pass180_PrepareShaderBinaries
{
    public static void DoPass(PipelineState state)
    {
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
        // Build the set of shader binaries that participate in any
        // surviving shader-map. Skip everything else (it can be a global
        // shader unreferenced by the materials we actually want, or a
        // dedup'd variant outside the filter). Shader-maps drive emission,
        // not the flat archive.
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

        // Sequential prep, one PER UNIQUE SHADER BINARY. Even if a binary
        // is shared across N shader-maps, its DXBC content is identical
        // so we strip + decompile once and re-emit per map below.
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

        // Strip UE wrapper: produces clean DXBC/DXIL + UnrealMetadata.
        byte[] strippedCode = UnrealShaderParser.Parse(raw, out ShaderArchitecture detectedFormat, out UnrealShaderParser.UnrealMetadata? unrealMetadata);

        // Pick the best symbol source for this shader's material(s).
        bool hadUsage = state.UsageByShaderIndex.TryGetValue(shaderIndex, out HashSet<string>? usedBy) && usedBy.Count > 0;
        MaterialSymbolSource? bestSource = hadUsage ? ResolveBestSymbolSource(state, usedBy!, entry.Frequency) : null;

        if (hadUsage && bestSource == null)
        {
            string firstMat = usedBy!.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase).First();
            state.LogError($"Shader {shaderIndex}: usage has {usedBy!.Count} material(s) (first: {firstMat}) but symbol reader returned null - material CB will be unnamed.");
        }

        SerializedProgramData metadata = SubProgramMetadataReader.Read(unrealMetadata, bestSource, state.EngineUbRegistry, state.Log);

        // Per-shader shader-model selection. The library-level option is
        // a default that callers tune to the lowest model they expect; an
        // individual shader can request a higher model via either:
        //   * `unrealMetadata.IsSm6Shader == true` (the parser already
        //      decoded the optional-data `'6'` flag UE writes for any
        //      cooked DXC-compiled shader). Bump to 67 so spirv-cross
        //      accepts the full SM 6.x feature set (resource heap usage,
        //      `[[vk::binding]]` -> `register(...)` mapping rules,
        //      DXIL-only intrinsics like `WaveActiveSum`, sampling
        //      non-float textures via Sample/SampleLevel, payload
        //      access qualifiers, etc.).
        //   * Format == Dxil with a DXC-style container chunk. The
        //      lowest model dxil-spirv reliably produces SPV for is 6.0;
        //      bumping is harmless for SM 6.x containers (spirv-cross
        //      only uses the model to gate intrinsic emission, never to
        //      reject input).
        //
        // We bump to **67** specifically because spirv-cross gates two
        // common feature checks at SM versions higher than 60:
        //   - "Wave ops requires SM 6.0 or higher" (gated at 60)
        //   - "Sampling non-float textures is not supported in HLSL SM < 6.7"
        // 67 covers both without forcing the user to know which version
        // each shader needs. The lower-bound bump matches the highest
        // common-case feature gate observed in the X6Game cook (35/36
        // _failures in the Global archive were one of these two errors).
        //
        // Untouched when the parser said SM 5.x — we still default to
        // the caller's library option (51 / 50) so the existing UE 5.4
        // SM 5.1 fixture stays byte-identical.
        uint perShaderModel = state.Options.ShaderModel;
        bool optionallyMarkedSm6 = unrealMetadata?.IsSm6Shader == true;
        if (optionallyMarkedSm6 || detectedFormat == ShaderArchitecture.Dxil)
        {
            // Only bump UPWARDS. If the caller explicitly asked for SM
            // 6.2 / 6.6 we keep that (a higher caller intent wins).
            if (perShaderModel < 67) perShaderModel = 67;
        }

        EngineDecompileOptions engineOptions = new()
        {
            Format = detectedFormat,
            Metadata = metadata,
            ShaderModel = perShaderModel,
            MetadataEnricher = static (spv, md) => MaterialTextureNameInferrer.InferAndAppend(spv, md),
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

    private static MaterialSymbolSource? ResolveBestSymbolSource(PipelineState state, HashSet<string> usedBy, byte frequency)
    {
        string shaderPlatform = frequency switch
        {
            0 or 1 or 2 or 3 or 4 or 5 => "SP_PCD3D_SM5",
            _ => string.Empty,
        };

        foreach (string material in usedBy)
        {
            MaterialSymbolSource? candidate = state.UnifiedMaterialReader?.GetSource(material, shaderPlatform)
                                            ?? state.MaterialJsonSymbolReader?.GetSource(material, shaderPlatform);
            // UnifiedMaterialReader's source doesn't carry MaterialCollection<i>
            // cbuffers (unified metadata strips ParameterCollectionInfos to keep
            // the JSON small). If the per-material JSON IS available, pull just
            // the MaterialCollection<i> entries from there and union them into
            // the unified source's metadata. This makes the MPC recovery work
            // identically across both reader paths.
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
                // Defensive copy — caches share metadata across every shader
                // using the same material; SRT enrichment + texture-name
                // inferrer mutate it in place.
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

    // Resolution chain:
    //   1. NameByShaderIndex (display name from unified metadata).
    //   2. First entry in UsageByShaderIndex[i] -> filename component.
    //   3. "Shader" — combined with `_<typeSuffix>_<shaderIndex>` is
    //      already unique without a counter (shaderIndex is library-wide).
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
