using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 200 — Phase 3 of the decompile pipeline: walk shader-maps in
// alphabetical order, build per-map UeShaderLabContainerMetadata from
// the prepped binaries + cached decompile results, and render each map
// to a single `.shader` file under `state.OutputDirectory`.
//
// SINGLE Pass per shader-map. Variants stay inside it as
// `#if defined(VARIANT_<keyword>)` blocks. UE shader-maps don't have a
// Unity-LIGHTMODE-style splitting axis at the cooked level — distinct
// ShaderType+VF+Permutation tuples are just cells of the same
// multi-compile matrix, so splitting them into separate Pass blocks
// would mislead any downstream Unity-style tooling into thinking they're
// separate render passes.
//
// All emission helpers are inlined here:
//   - WriteContainerShaderFile          renders the .shader text
//   - BuildVariantKeyword / BuildPermutationKeyword
//   - TryGetStagePragma / StageSortKey / ToUnityStageName
//   - WriteVariantHlslFile / SplitLines
//   - BuildShaderMapMetadata / BuildShaderMapStem
//   - ResolvePerMapContainer / FinalizeForMap / WriteShaderMapOutputs
//   - UeShaderLabProgramData / UeShaderLabContainerMetadata / ContainerOutputEntry
//   - SanitizeFileStem / BuildVariantSuffix
internal static class Pass200_EmitShaderLabFiles
{
    private sealed class ContainerOutputEntry
    {
        public required ShaderPrep Prep { get; init; }
        public required DecompileResult Result { get; init; }
        public required string BasePath { get; init; }
        public required string SourceExtension { get; init; }
    }

    public static void DoPass(PipelineState state)
    {
        if (state.ShaderMaps.Count == 0)
        {
            state.Log("    EmitShaderLabFiles: no shader-maps, skipping.");
            return;
        }

        foreach (ShaderMapInfo map in state.ShaderMaps.OrderBy(m => m.PrimaryName, StringComparer.OrdinalIgnoreCase))
        {
            EmitShaderMap(state, map);
        }

        state.Log($"    Library {Path.GetFileName(state.Options.LibraryPath)}: shader-maps={state.ShaderMaps.Count} decompiled={state.Decompiled} skipped={state.Skipped} failed={state.Failed}.");
    }

    // Streaming entry — emit one map's `.shader` file. Used by the
    // DecompilePipeline orchestrator when interleaving Pass 190 + Pass 200
    // per-map so files appear progressively rather than in one big burst
    // at the end. Pass 190 must have populated `state.DecompileResultByIndex`
    // for this map's binaries before this is called.
    public static void DoPassForOneMap(PipelineState state, ShaderMapInfo map)
        => EmitShaderMap(state, map);

    private static void EmitShaderMap(PipelineState state, ShaderMapInfo map)
    {
        List<ContainerOutputEntry> outputs = new(map.Members.Count);
        foreach (ShaderMapMember member in map.Members)
        {
            if (!state.ShaderPrepByIndex.TryGetValue(member.ArchiveShaderIndex, out ShaderPrep? prep)) continue;
            if (!state.DecompileResultByIndex.TryGetValue(member.ArchiveShaderIndex, out DecompileResult? result)) continue;

            ContainerOutputEntry? output = FinalizeForMap(state, map, member, prep, result);
            if (output != null)
            {
                outputs.Add(output);
            }
        }

        if (outputs.Count == 0)
        {
            state.Skipped++;
            return;
        }

        WriteShaderMapOutputs(state, map, outputs);
    }

    private static ContainerOutputEntry? FinalizeForMap(
        PipelineState state,
        ShaderMapInfo map,
        ShaderMapMember member,
        ShaderPrep prep,
        DecompileResult? result)
    {
        if (result == null)
        {
            state.Failed++;
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}): batch worker returned no result.");
            return null;
        }

        if (!result.Success)
        {
            state.Failed++;
            string firstLine = result.ErrorMessage?.Split('\n', 2)[0]?.Trim() ?? "<no message>";
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}) [stage={result.FailedStage ?? "unknown"}]: {firstLine}");
            return new ContainerOutputEntry
            {
                Prep = prep,
                Result = result,
                BasePath = Path.Combine(state.OutputDirectory, BuildShaderMapStem(map)),
                SourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension,
            };
        }

        if (result.FinalMetadata != null)
        {
            // Per-map UsedMaterials honesty: every emission of a shared
            // binary lists ONLY the assets of the shader-map this emission
            // belongs to, not the union across all maps that share the
            // binary.
            result.FinalMetadata.UsedMaterials = new List<string>(map.Assets);
        }

        state.Decompiled++;
        return new ContainerOutputEntry
        {
            Prep = prep,
            Result = result,
            BasePath = Path.Combine(state.OutputDirectory, BuildShaderMapStem(map)),
            SourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension,
        };
    }

    private static string BuildShaderMapStem(ShaderMapInfo map)
    {
        string mapShort = map.ShaderMapHash.Length >= 12 ? map.ShaderMapHash[..12] : map.ShaderMapHash;
        return SanitizeFileStem($"SM{mapShort}_{map.PrimaryName}");
    }

    private static void WriteShaderMapOutputs(PipelineState state, ShaderMapInfo map, List<ContainerOutputEntry> outputs)
    {
        string containerStem = BuildShaderMapStem(map);
        string containerBasePath = Path.Combine(state.OutputDirectory, containerStem);

        UeShaderLabContainerMetadata metadata = BuildShaderMapMetadata(state, map, outputs);

        // Per-stage split decision. A stage gets distributed to per-variant
        // .hlsl files only when (a) the user opted into split mode AND
        // (b) the stage actually has more than one variant — a single-variant
        // stage stays inline because there's no chain to slim down.
        // The set of stages-to-split also tells us which variant files
        // to write next to the .shader.
        HashSet<string> splittableStages = ComputeSplittableStages(metadata.Programs, state.Options.SplitVariantsToHlslFiles);

        if (splittableStages.Count > 0)
        {
            Directory.CreateDirectory(containerBasePath); // sibling folder named after the .shader stem
            foreach (UeShaderLabProgramData program in metadata.Programs)
            {
                if (!splittableStages.Contains(program.Stage)) continue;
                string keyword = BuildVariantKeyword(program);
                string hlslPath = Path.Combine(containerBasePath, keyword + ".hlsl");
                File.WriteAllText(hlslPath, WriteVariantHlslFile(metadata, program, keyword));
            }
        }

        File.WriteAllText(containerBasePath + ".shader", WriteContainerShaderFile(metadata, containerStem, splittableStages));
    }

    // Returns the set of stage names whose programs should be emitted as
    // sibling .hlsl files (with `#include` references in the .shader). A
    // stage qualifies only when split mode is on AND it has >1 variant —
    // single-variant stages stay inline regardless of the flag because
    // distribution adds files without simplifying the .shader text.
    private static HashSet<string> ComputeSplittableStages(List<UeShaderLabProgramData> programs, bool splitEnabled)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (!splitEnabled) return result;
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (UeShaderLabProgramData program in programs)
        {
            counts[program.Stage] = counts.GetValueOrDefault(program.Stage) + 1;
        }
        foreach (var kvp in counts)
        {
            if (kvp.Value > 1) result.Add(kvp.Key);
        }
        return result;
    }

    // Per-variant HLSL body file. Header carries every identifying datum so the
    // file stands alone away from the .shader distributor. Body is the raw
    // decompiled SourceCode (or the GLSL fallback / failure note).
    private static string WriteVariantHlslFile(UeShaderLabContainerMetadata metadata, UeShaderLabProgramData program, string keyword)
    {
        StringBuilder sb = new();
        sb.AppendLine("// =============================================================");
        sb.AppendLine($"// Variant: {keyword}");
        sb.AppendLine($"// Shader: {metadata.Name}");
        sb.AppendLine($"// ContainerKey: {metadata.ContainerKey}");
        sb.AppendLine($"// Stage: {program.Stage}");
        sb.AppendLine($"// ShaderIndex: {program.ShaderIndex}");
        sb.AppendLine($"// ResourceIndex: {program.ResourceIndex}");
        sb.AppendLine($"// PermutationId: {program.PermutationId}");
        if (!string.IsNullOrWhiteSpace(program.ShaderHash)) sb.AppendLine($"// ShaderHash: {program.ShaderHash}");
        if (!string.IsNullOrWhiteSpace(program.ShaderTypeName)) sb.AppendLine($"// ShaderType: {program.ShaderTypeName}");
        if (!string.IsNullOrWhiteSpace(program.VertexFactoryTypeName)) sb.AppendLine($"// VertexFactoryType: {program.VertexFactoryTypeName}");
        if (!string.IsNullOrWhiteSpace(program.PipelineTypeName)) sb.AppendLine($"// PipelineType: {program.PipelineTypeName}");
        sb.AppendLine("// =============================================================");
        sb.AppendLine();

        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            foreach (string line in SplitLines(program.SourceCode!))
            {
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        sb.AppendLine("// Decompile failed.");
        if (!string.IsNullOrWhiteSpace(program.ErrorMessage))
        {
            foreach (string line in SplitLines(program.ErrorMessage!))
            {
                sb.Append("// ");
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    private static UeShaderLabContainerMetadata BuildShaderMapMetadata(PipelineState state, ShaderMapInfo map, List<ContainerOutputEntry> outputs)
    {
        return new UeShaderLabContainerMetadata
        {
            Name = map.PrimaryName,
            ContainerKey = $"SM{(map.ShaderMapHash.Length >= 12 ? map.ShaderMapHash[..12] : map.ShaderMapHash)}",
            MaterialName = map.PrimaryName,
            UsedMaterials = new List<string>(map.Assets),
            // Properties block is pre-rendered by Pass 170 from the
            // primary asset's UniformExpressionSet. Empty when the
            // pipeline ran without a UnifiedShaderMetadata.json.
            PropertiesBlock = map.PropertiesBlock,
            // Render-state blocks are pre-rendered by Pass 175 from the
            // primary asset's RenderState UProperty bag.
            SubShaderTags = map.SubShaderTags,
            PassCommands = map.PassCommands,
            Programs = outputs
                .OrderBy(static o => StageSortKey(ToUnityStageName(o.Prep.TypeSuffix)))
                .ThenBy(static o => o.Prep.ShaderIndex)
                .Select(output =>
                {
                    // Per-map authoritative view first, fallback to the
                    // last-write-wins prep ContainerInfo.
                    ShaderContainerInfo? perMap = ResolvePerMapContainer(state, map, output.Prep.ShaderIndex);
                    ShaderContainerInfo? container = perMap ?? output.Prep.ContainerInfo;
                    return new UeShaderLabProgramData
                    {
                        Stage = ToUnityStageName(output.Prep.TypeSuffix),
                        ShaderIndex = output.Prep.ShaderIndex,
                        ResourceIndex = container?.ResourceIndex ?? -1,
                        PermutationId = container?.PermutationId ?? -1,
                        PipelineTypeHash = container?.PipelineTypeHash ?? string.Empty,
                        PipelineTypeName = container?.PipelineTypeName ?? string.Empty,
                        ShaderTypeHash = container?.ShaderTypeHash ?? string.Empty,
                        ShaderTypeName = container?.ShaderTypeName ?? string.Empty,
                        VertexFactoryTypeHash = container?.VertexFactoryTypeHash ?? string.Empty,
                        VertexFactoryTypeName = container?.VertexFactoryTypeName ?? string.Empty,
                        ShaderMapHash = map.ShaderMapHash,
                        ShaderHash = container?.ShaderHash ?? string.Empty,
                        SourceLanguage = output.Result.SourceLanguage,
                        SourceFileExtension = output.Result.SourceFileExtension,
                        Success = output.Result.Success,
                        SourceCode = output.Result.SourceCode,
                        ErrorMessage = output.Result.ErrorMessage,
                        SymbolMetadata = output.Result.FinalMetadata,
                    };
                })
                .ToList()
        };
    }

    private static ShaderContainerInfo? ResolvePerMapContainer(PipelineState state, ShaderMapInfo map, int archiveShaderIndex)
    {
        if (state.ContainersByMapAndIndex.TryGetValue(map.ShaderMapHash, out Dictionary<int, ShaderContainerInfo>? perMap)
            && perMap.TryGetValue(archiveShaderIndex, out ShaderContainerInfo? info))
        {
            return info;
        }
        return null;
    }

    private static string WriteContainerShaderFile(UeShaderLabContainerMetadata metadata, string variantFolderStem, HashSet<string> splittableStages)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Shader \"{metadata.Name}\" {{");
        sb.AppendLine($"    // UE ContainerKey: {metadata.ContainerKey}");
        sb.AppendLine($"    // Material: {metadata.MaterialName}");
        if (metadata.UsedMaterials.Count > 0)
        {
            sb.AppendLine("    // UsedMaterials:");
            foreach (string material in metadata.UsedMaterials)
            {
                sb.AppendLine($"    //   {material}");
            }
        }
        // Shaderlab Properties — sourced from FUniformExpressionSet, the
        // same member-set the cooked Material cbuffer is built from.
        // Renders BEFORE SubShader per shaderlab convention.
        if (!string.IsNullOrEmpty(metadata.PropertiesBlock))
        {
            foreach (string line in metadata.PropertiesBlock.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) sb.AppendLine();
                else sb.AppendLine("    " + trimmed);
            }
        }
        sb.AppendLine("    SubShader {");
        // SubShader-level Tags block — RenderType / Queue / annotation tags
        // built from the material's BlendMode + MaterialDomain by Pass175.
        if (!string.IsNullOrEmpty(metadata.SubShaderTags))
        {
            foreach (string line in metadata.SubShaderTags.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) sb.AppendLine();
                else sb.AppendLine("        " + trimmed);
            }
        }
        // SINGLE Pass per shader-map. Variants stay inside it as
        // #if defined(VARIANT_<keyword>) blocks. UE shader-maps don't have
        // a Unity-LIGHTMODE-style splitting axis at the cooked level —
        // distinct ShaderType+VF+Permutation tuples are just cells of the
        // same multi-compile matrix, so splitting them into separate Pass
        // blocks would mislead any downstream Unity-style tooling into
        // thinking they're separate render passes.
        if (metadata.Programs.Count > 0)
        {
            List<UeShaderLabProgramData> passPrograms = metadata.Programs
                .OrderBy(static p => StageSortKey(p.Stage))
                .ThenBy(static p => p.ShaderIndex)
                .ToList();
            sb.AppendLine("        Pass {");
            // No Pass `Name "..."` line: UE shader-maps don't carry a
            // canonical pass name (a map fans out to many ShaderType *
            // VertexFactory * Permutation tuples), and substituting the
            // material name would be misleading boilerplate. Real Unity
            // Pass names come from the LightMode tag downstream tooling
            // already keys off — that's the right axis here, not a single
            // string per shader-map.
            if (!string.IsNullOrWhiteSpace(metadata.ContainerKey)) sb.AppendLine($"            // ContainerKey: {metadata.ContainerKey}");
            // Per-Pass render-state commands (Cull / Blend / ZWrite / ZTest /
            // ColorMask / AlphaToMask) — built from material UProperties by
            // Pass175. Empty when every command would have been the shaderlab
            // default for an opaque material.
            if (!string.IsNullOrEmpty(metadata.PassCommands))
            {
                foreach (string line in metadata.PassCommands.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine("            " + trimmed);
                }
            }
            // Surface the unique ShaderType / VF set this shader-map
            // contains so a reader can see the variant matrix at a glance
            // without scrolling through every `#if` block.
            foreach (string typeName in passPrograms.Select(p => p.ShaderTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.AppendLine($"            // ShaderType: {typeName}");
            }
            foreach (string vfName in passPrograms.Select(p => p.VertexFactoryTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.AppendLine($"            // VertexFactoryType: {vfName}");
            }
            foreach (string pipeline in passPrograms.Select(p => p.PipelineTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            {
                sb.AppendLine($"            // PipelineType: {pipeline}");
            }
            if (!string.IsNullOrWhiteSpace(passPrograms[0].ShaderMapHash)) sb.AppendLine($"            // ShaderMapHash: {passPrograms[0].ShaderMapHash}");

            // Pick a language tag that reflects what's actually in the
            // body. spirv-cross may have fallen back to GLSL for shaders
            // whose cbuffer layout (e.g. UE bindless `uint _m0[N]` with
            // ArrayStride 4) can't be expressed in HLSL packoffset rules,
            // or whose stage uses raytracing/mesh builtins HLSL doesn't
            // model. When *any* program in this pass landed as GLSL we
            // emit `GLSLPROGRAM` so a downstream consumer doesn't try to
            // compile the GLSL body as HLSL. Mixed-language passes (rare
            // in practice — same pass typically uses one toolchain end
            // to end) take the lowest-common-denominator GLSL tag.
            bool anyGlsl = passPrograms.Any(p => string.Equals(p.SourceLanguage, "glsl", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine(anyGlsl ? "            GLSLPROGRAM" : "            HLSLPROGRAM");

            // ONE #pragma per stage type — Distinct() because passPrograms can
            // contain many variants of the same stage (different permutations)
            // but we only declare each stage entry point once for the shaderlab
            // pass.
            foreach (string pragma in passPrograms
                         .Select(p => TryGetStagePragma(p.Stage, out string pr) ? pr : string.Empty)
                         .Where(static p => !string.IsNullOrEmpty(p))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(static p => p, StringComparer.Ordinal))
            {
                sb.AppendLine($"            {pragma} main");
            }

            // multi_compile_local declares EVERY variant keyword this pass
            // can take, including the synthetic VARIANT_<id> ones we
            // generate when no real permutation/type info is available.
            // Without this Unity-style declaration, downstream tooling that
            // walks the .shader file as a Unity asset can't enumerate the
            // pass's variant matrix.
            List<string> variantKeywords = passPrograms
                .Select(p => BuildVariantKeyword(p))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static k => k, StringComparer.Ordinal)
                .ToList();
            if (variantKeywords.Count > 1)
            {
                sb.AppendLine($"            #pragma multi_compile_local {string.Join(' ', variantKeywords)}");
            }

            foreach (string permutationKeyword in BuildPassPermutationKeywords(passPrograms))
            {
                sb.AppendLine($"            #pragma multi_compile_local __ {permutationKeyword}");
            }
            sb.AppendLine();

            foreach (IGrouping<string, UeShaderLabProgramData> stageGroup in passPrograms
                         .GroupBy(static p => p.Stage, StringComparer.Ordinal)
                         .OrderBy(static g => StageSortKey(g.Key)))
            {
                List<UeShaderLabProgramData> stagePrograms = stageGroup
                    .OrderBy(static p => p.PermutationId)
                    .ThenBy(static p => p.ShaderIndex)
                    .ToList();

                // Stage delimiter is a comment block, not a `#if defined(SHADER_STAGE_*)`
                // wrapper. The per-stage HLSL bodies under this banner each have their
                // own globals/types/entry function and won't compile concatenated, but
                // ShaderLab readers consume this file as documentation rather than feed
                // it to a compiler — comments make the structure obvious without
                // implying the file is a single compilable translation unit.
                sb.AppendLine($"            // ============================================================");
                sb.AppendLine($"            // Stage: {stageGroup.Key}");
                sb.AppendLine($"            // ============================================================");

                bool stageSplit = splittableStages.Contains(stageGroup.Key);

                if (stagePrograms.Count == 1)
                {
                    // Single-variant stage: no `#if defined(VARIANT_*)` wrapper —
                    // there's nothing to disambiguate, so the chain would just
                    // be dead boilerplate. Body goes inline (or via a single
                    // `#include` if the user explicitly opted into split mode
                    // for this stage; with the > 1 gate in ComputeSplittableStages
                    // the include path won't actually fire here).
                    UeShaderLabProgramData only = stagePrograms[0];
                    EmitProgramBlock(sb, only, variantFolderStem, splitInclude: stageSplit);
                }
                else
                {
                    // Multi-variant stage: walk the chain. BuildVariantKeyword
                    // always appends ShaderHash (or ShaderIndex) as a final
                    // disambiguator so two binaries that share (ShaderType,VF,Perm)
                    // still get distinct keywords — without that tail every branch
                    // in the chain would carry the same condition and the chain
                    // would be a malformed `#if/#elif/...` with no real branching.
                    bool wroteFirst = false;
                    foreach (UeShaderLabProgramData program in stagePrograms)
                    {
                        string keyword = BuildVariantKeyword(program);
                        sb.AppendLine($"            {(wroteFirst ? "#elif" : "#if")} defined({keyword})");
                        wroteFirst = true;
                        EmitProgramBlock(sb, program, variantFolderStem, splitInclude: stageSplit);
                    }

                    if (wroteFirst)
                    {
                        sb.AppendLine("            #endif");
                    }
                }
                sb.AppendLine();
            }
            // Match the close tag to whatever opening tag we picked above.
            sb.AppendLine(anyGlsl ? "            ENDGLSL" : "            ENDHLSL");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static bool TryGetStagePragma(string stage, out string pragma)
    {
        pragma = stage switch
        {
            "Vertex" => "#pragma vertex",
            "Fragment" => "#pragma fragment",
            "Geometry" => "#pragma geometry",
            "Hull" => "#pragma hull",
            "Domain" => "#pragma domain",
            "Compute" => "#pragma kernel",
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(pragma);
    }

    private static string BuildPassGroupKey(UeShaderLabProgramData program)
    {
        string pipeline = string.IsNullOrWhiteSpace(program.PipelineTypeHash) ? "NOPIPE" : program.PipelineTypeHash;
        string vf = string.IsNullOrWhiteSpace(program.VertexFactoryTypeHash) ? "NOVF" : program.VertexFactoryTypeHash;
        string type = string.IsNullOrWhiteSpace(program.ShaderTypeHash) ? "NOTYPE" : program.ShaderTypeHash;
        return $"P{pipeline}_V{vf}_S{type}";
    }

    // Inline-or-include emission for one program body. When `splitInclude`
    // is true the body lives in a sibling `<variantFolderStem>/<keyword>.hlsl`
    // and we emit a single `#include` line; otherwise the body is inlined
    // verbatim under a `// Stage: ...` header comment that mirrors the
    // metadata the per-variant file would have carried.
    private static void EmitProgramBlock(StringBuilder sb, UeShaderLabProgramData program, string variantFolderStem, bool splitInclude)
    {
        if (splitInclude)
        {
            string keyword = BuildVariantKeyword(program);
            string includePath = $"{variantFolderStem}/{keyword}.hlsl";
            sb.AppendLine($"            #include \"{includePath}\"");
            return;
        }

        sb.AppendLine($"            // Stage: {program.Stage}");
        sb.AppendLine($"            // ShaderIndex: {program.ShaderIndex}");
        sb.AppendLine($"            // ResourceIndex: {program.ResourceIndex}");
        sb.AppendLine($"            // PermutationId: {program.PermutationId}");
        if (!string.IsNullOrWhiteSpace(program.ShaderHash)) sb.AppendLine($"            // ShaderHash: {program.ShaderHash}");

        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            foreach (string line in SplitLines(program.SourceCode!))
            {
                sb.Append("            ");
                sb.AppendLine(line);
            }
            return;
        }

        sb.AppendLine("            // Decompile failed.");
        if (!string.IsNullOrWhiteSpace(program.ErrorMessage))
        {
            foreach (string line in SplitLines(program.ErrorMessage!))
            {
                sb.Append("            // ");
                sb.AppendLine(line);
            }
        }
    }

    private static List<string> BuildPassPermutationKeywords(List<UeShaderLabProgramData> programs)
    {
        return programs
            .Where(static p => p.PermutationId >= 0)
            .Select(static p => BuildPermutationKeyword(p.PermutationId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildPermutationKeyword(int permutationId) => $"PERM_{permutationId}";

    // Variant keyword that uniquely identifies a single shader binary
    // within its (Stage,Pass) group.
    //
    // Always appends a final disambiguator — ShaderHash short prefix when
    // available, otherwise ShaderIndex — so two binaries with the same
    // (ShaderType, VertexFactory, PermutationId) still produce distinct
    // keywords. Without that tail, multiple cooked variants of the same
    // shader-type collapse onto the same keyword and the surrounding
    // `#if/#elif` chain becomes malformed (every branch with identical
    // condition).
    private static string BuildVariantKeyword(UeShaderLabProgramData program)
    {
        StringBuilder sb = new();
        sb.Append("VARIANT");

        if (!string.IsNullOrWhiteSpace(program.ShaderTypeName))
        {
            sb.Append('_').Append(SanitizeIdent(program.ShaderTypeName));
        }
        if (!string.IsNullOrWhiteSpace(program.VertexFactoryTypeName))
        {
            sb.Append('_').Append(SanitizeIdent(program.VertexFactoryTypeName));
        }
        if (program.PermutationId >= 0)
        {
            sb.Append("_PERM_").Append(program.PermutationId);
        }

        if (!string.IsNullOrWhiteSpace(program.ShaderHash))
        {
            string shortHash = program.ShaderHash.Length >= 8 ? program.ShaderHash[..8] : program.ShaderHash;
            sb.Append('_').Append(shortHash);
        }
        else
        {
            sb.Append("_IDX").Append(program.ShaderIndex.ToString("D6"));
        }

        return sb.ToString();
    }

    // Replace HLSL-illegal characters with underscores so the resulting
    // string is safe to use as a `#pragma multi_compile_local` keyword
    // and as a `#if defined(...)` operand. UE shader-type names contain
    // template arguments (`<>`), namespace separators (`::`), and commas
    // for multi-arg templates — all forbidden in C-style identifiers.
    private static string SanitizeIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
        }
        // Collapse runs of underscores for readability.
        StringBuilder collapsed = new(sb.Length);
        bool prevUnderscore = false;
        foreach (char c in sb.ToString())
        {
            if (c == '_')
            {
                if (!prevUnderscore) collapsed.Append('_');
                prevUnderscore = true;
            }
            else
            {
                collapsed.Append(c);
                prevUnderscore = false;
            }
        }
        return collapsed.ToString().Trim('_');
    }

    private static int StageSortKey(string stage) => stage switch
    {
        "Vertex" => 0,
        "Hull" => 1,
        "Domain" => 2,
        "Geometry" => 3,
        "Fragment" => 4,
        "Compute" => 5,
        _ => 100,
    };

    private static string ToUnityStageName(string typeSuffix) => typeSuffix switch
    {
        "VS" => "Vertex",
        "PS" => "Fragment",
        "GS" => "Geometry",
        "HS" => "Hull",
        "DS" => "Domain",
        "CS" => "Compute",
        _ => typeSuffix,
    };

    private static IEnumerable<string> SplitLines(string text)
    {
        using StringReader reader = new(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    private static string SanitizeFileStem(string value)
    {
        return string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class UeShaderLabContainerMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string ContainerKey { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public List<string> UsedMaterials { get; set; } = new();
        public List<UeShaderLabProgramData> Programs { get; set; } = new();
        // Pre-rendered shaderlab `Properties { ... }` block, sourced from
        // the primary asset's UniformExpressionSet. Empty when no asset
        // metadata is available (e.g. global archive entries that have no
        // material side at all).
        public string PropertiesBlock { get; set; } = string.Empty;
        // Pre-rendered SubShader Tags block (Pass175 output, from RenderState
        // UProperties). Empty when no material backing.
        public string SubShaderTags { get; set; } = string.Empty;
        // Pre-rendered per-Pass shaderlab commands (Cull/Blend/ZWrite/...).
        // One command per line, no leading whitespace; the emitter indents.
        public string PassCommands { get; set; } = string.Empty;
    }

    private sealed class UeShaderLabProgramData
    {
        public string Stage { get; set; } = string.Empty;
        public int ShaderIndex { get; set; }
        public int ResourceIndex { get; set; }
        public int PermutationId { get; set; }
        public string PipelineTypeHash { get; set; } = string.Empty;
        public string PipelineTypeName { get; set; } = string.Empty;
        public string ShaderTypeHash { get; set; } = string.Empty;
        public string ShaderTypeName { get; set; } = string.Empty;
        public string VertexFactoryTypeHash { get; set; } = string.Empty;
        public string VertexFactoryTypeName { get; set; } = string.Empty;
        public string ShaderMapHash { get; set; } = string.Empty;
        public string ShaderHash { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string SourceLanguage { get; set; } = "hlsl";
        public string SourceFileExtension { get; set; } = ".hlsl";
        public string? SourceCode { get; set; }
        public string? ErrorMessage { get; set; }
        public SerializedProgramData? SymbolMetadata { get; set; }
    }
}
