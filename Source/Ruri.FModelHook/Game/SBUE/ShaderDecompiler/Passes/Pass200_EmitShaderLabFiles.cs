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
            byte freq = state.Library != null && member.ArchiveShaderIndex >= 0 && member.ArchiveShaderIndex < state.Library.ShaderEntries.Length
                ? state.Library.ShaderEntries[member.ArchiveShaderIndex].Frequency : (byte)255;
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}) [stage={result.FailedStage} freq={ShaderFrequency.ToString(freq)}]: {firstLine}");
            return new ContainerOutputEntry
            {
                Prep = prep,
                Result = result,
                BasePath = Path.Combine(state.OutputDirectory, BuildShaderMapStem(map)),
                SourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension,
            };
        }

        if (result.FinalSymbols != null)
        {
            // Per-map UsedMaterials honesty: every emission of a shared
            // binary lists ONLY the assets of the shader-map this emission
            // belongs to, not the union across all maps that share the
            // binary.
            result.FinalSymbols.UsedMaterials = new List<string>(map.Assets);
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
            string source = RenameAnonymousGlobals(program.SourceCode!, program.ShaderTypeName, program.ShaderHash, program.SymbolMetadata);
            foreach (string line in SplitLines(source))
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
            MaterialTextureOrder = new List<string>(map.MaterialTextureOrder),
            MaterialCbufferValues = new Dictionary<string, string>(map.MaterialCbufferValues, StringComparer.Ordinal),
            MaterialCbufferOffsets = new Dictionary<string, int>(map.MaterialCbufferOffsets, StringComparer.Ordinal),
            MaterialCbufferPrograms = new Dictionary<string, string>(map.MaterialCbufferPrograms, StringComparer.Ordinal),
            MaterialCbufferParams = new Dictionary<string, string>(map.MaterialCbufferParams, StringComparer.Ordinal),
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
                        SymbolMetadata = output.Result.FinalSymbols,
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

    /// <summary>
    /// 写 <c>// MaterialCbufferValues:</c> 头块 —— 每个 <c>Material</c> cbuffer 成员**实算出来的值**。
    ///
    /// 为什么 Properties 块不够:Properties 导的是**作者参数**的缺省值,而 cbuffer 成员是
    /// preshader 的**求值结果**。两者只有在"成员 = 某个参数"的恒等情形才相等;凡是派生表达式
    /// (成员 = 若干参数的运算)就对不上,而成员名看不出是哪一种 —— 消费侧按名字查参数值必然
    /// 取错,且无从察觉。名字还原不出来的成员(<c>Material_Unmapped_at_&lt;offset&gt;</c>)更是完全没辙。
    /// 这张表把真值直接给出来,消费侧优先用它、查不到再回落到名字路径。
    /// </summary>
    private static void WriteMaterialCbufferValues(StringBuilder sb, UeShaderLabContainerMetadata metadata)
    {
        if (metadata.MaterialCbufferValues.Count == 0) return;
        if (metadata.MaterialCbufferParams.Count > 0)
        {
            // 上面那些值是用**这套参数**算出来的。消费侧先拿它跑一遍程序、复现得出同样的值,
            // 才说明两边的求值语义逐条对齐了,这时改用自己实例的参数重算才是安全的。
            sb.AppendLine("    // MaterialCbufferParams:");
            foreach (KeyValuePair<string, string> kv in metadata.MaterialCbufferParams.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"    //   \"{kv.Key}\" = {kv.Value}");
            }
        }

        sb.AppendLine("    // MaterialCbufferValues:");
        foreach (KeyValuePair<string, string> kv in metadata.MaterialCbufferValues.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            // 带上 (寄存器, 分量):preshader 段按 UE 原样声明成 float4 数组之后,
            // 消费侧是按下标填数组的,名字只作可读性,定位靠这两个数。
            // 值之后再挂一段 `:= <程序>` —— 值是"按这份 UES 的参数缺省算出的一个数",
            // 程序才是算法。消费侧渲染的材质实例改了参数时,得拿程序 + 自己的参数重算,
            // 否则只能从有损的成员名反推算式(实测头发的 `1 - Retouch Tex Intensity`
            // 就是这么反推失败、停在缺省值 0,把整头头发压暗 5 倍的)。
            string program = metadata.MaterialCbufferPrograms.TryGetValue(kv.Key, out string? prog) && !string.IsNullOrEmpty(prog)
                ? $" := {prog}"
                : string.Empty;

            if (metadata.MaterialCbufferOffsets.TryGetValue(kv.Key, out int off))
            {
                sb.AppendLine($"    //   [{off / 16}][{off % 16 / 4}] {kv.Key} = {kv.Value}{program}");
            }
            else
            {
                sb.AppendLine($"    //   {kv.Key} = {kv.Value}{program}");
            }
        }
    }

    /// <summary>
    /// 取这张 shader map 的**代表材质**那份实算 cbuffer 值。cbuffer 布局的作用域就是
    /// shader map(母材质 + 静态开关集),同一张 map 下所有材质实例共用同一套成员,
    /// 所以取任一已算出的 UsedMaterials 条目即可 —— 按 PrimaryName 优先,取不到再顺着找。
    /// </summary>
    private static Dictionary<string, string> LookupCbufferValues(ShaderMapInfo map)
    {
        var table = MaterialConstantBufferReader.EvaluatedCbufferValues;
        foreach (string candidate in new[] { map.PrimaryName }.Concat(map.Assets))
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            foreach (KeyValuePair<string, Dictionary<string, string>> entry in table)
            {
                if (entry.Key.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.EndsWith(entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, string>(entry.Value, StringComparer.Ordinal);
                }
            }
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
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
        if (metadata.MaterialTextureOrder.Count > 0)
        {
            // 完整声明序(所有 UES 贴图桶展平)。Properties 只有 Standard2D,消费端靠这张表
            // 才能无歧义地把匿名贴图槽对回参数名。
            sb.AppendLine("    // MaterialTextureOrder:");
            for (int i = 0; i < metadata.MaterialTextureOrder.Count; i++)
            {
                sb.AppendLine($"    //   [{i}] {metadata.MaterialTextureOrder[i]}");
            }
        }
        WriteMaterialCbufferValues(sb, metadata);
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

            // The decompiled HLSL uses SM5.1+ syntax (register(spaceN),
            // templated ByteAddressBuffer.Load<T>(), etc.) that Unity's
            // default FXC path rejects. `use_dxc` routes the program through
            // the modern compiler stack; `target 5.0` is Unity's max for DX11
            // and together they cover the surface SPIRV-Cross emits. Skipped
            // for GLSL passes — those go to a different downstream pipeline.
            if (!anyGlsl)
            {
                sb.AppendLine("            #pragma target 5.0");
                sb.AppendLine("            #pragma use_dxc");
            }

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

            // multi_compile_local would force Unity to generate a cross-product
            // variant matrix. With our setup each stage has its own keyword set
            // and the combinations where neither stage's `main` is defined fail
            // to compile. v0 of Unity output uses single-variant mode (see
            // EmitStageBlockSingleVariant) so the multi_compile pragmas drop;
            // variant coverage is traded for a clean compile pass.

            sb.AppendLine();

            foreach (IGrouping<string, UeShaderLabProgramData> stageGroup in passPrograms
                         .GroupBy(static p => p.Stage, StringComparer.Ordinal)
                         .OrderBy(static g => StageSortKey(g.Key)))
            {
                List<UeShaderLabProgramData> stagePrograms = stageGroup
                    .OrderBy(static p => p.PermutationId)
                    .ThenBy(static p => p.ShaderIndex)
                    .ToList();

                sb.AppendLine($"            // ============================================================");
                sb.AppendLine($"            // Stage: {stageGroup.Key}");
                sb.AppendLine($"            // ============================================================");

                // Wrap each stage's body in `#ifdef SHADER_STAGE_*` so the
                // VS-only / PS-only declarations (entry `main`, SPIRV_Cross_Input
                // structs, statics, cbuffers) don't collide when Unity compiles
                // each stage as its own translation unit. SHADER_STAGE_VERTEX /
                // _FRAGMENT / etc. are Unity macros set per stage compile, so the
                // preprocessor naturally strips the other stage's declarations.
                string? stageMacro = GetShaderStageMacro(stageGroup.Key);
                if (stageMacro != null)
                {
                    sb.AppendLine($"            #ifdef {stageMacro}");
                }

                bool stageSplit = splittableStages.Contains(stageGroup.Key);

                // Single-variant emit: take only the first sub-program per
                // stage. Multi-variant emission needs a per-Pass vertex+fragment
                // pairing (or per-stage keyword sets) and is deferred until the
                // basic emit produces compilable output.
                UeShaderLabProgramData primary = stagePrograms[0];
                if (stagePrograms.Count > 1)
                {
                    sb.AppendLine($"            // Note: {stagePrograms.Count - 1} additional variant(s) elided (single-variant emit mode).");
                }
                EmitProgramBlock(sb, primary, variantFolderStem, splitInclude: stageSplit);

                if (stageMacro != null)
                {
                    sb.AppendLine($"            #endif");
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
            string renamed = RenameAnonymousGlobals(program.SourceCode!, program.ShaderTypeName, program.ShaderHash, program.SymbolMetadata);
            string adapted = AdaptHlslForUnity(renamed);
            foreach (string line in SplitLines(adapted))
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

    private static string? GetShaderStageMacro(string stage) => stage switch
    {
        "Vertex" => "SHADER_STAGE_VERTEX",
        "Fragment" => "SHADER_STAGE_FRAGMENT",
        "Geometry" => "SHADER_STAGE_GEOMETRY",
        "Hull" => "SHADER_STAGE_HULL",
        "Domain" => "SHADER_STAGE_DOMAIN",
        "RayTracing" => "SHADER_STAGE_RAY_TRACING",
        _ => null,
    };

    // Adapts spirv-cross emitted HLSL so Unity's ShaderLab pipeline accepts it
    // without further hand-edits:
    //   * Texture bindings `Material_<X>` → `_<X>` so the Properties
    //     declaration (Unity uses `_X` convention) auto-binds to the HLSL var.
    //   * Sampler bindings `Material_<X>Sampler` → `sampler_<X>` so Unity's
    //     "must match a texture or contain inline mode names" heuristic
    //     accepts them.
    //   * Aliased `ByteAddressBuffer T<N>_<M>` at the SAME slot as `T<N>`
    //     — spirv-cross emits both names when two SSA values touch the same
    //     descriptor; collapse the alias declaration and rewrite call sites.
    //
    // Anchored on the texture/sampler type token so cbuffer scalar members
    // sharing the `Material_<X>` prefix don't get rewritten.
    private static readonly System.Text.RegularExpressions.Regex MaterialSamplerDeclRegex =
        new(@"\bMaterial_(?<n>[A-Za-z0-9_]+)Sampler\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MaterialTextureDeclRegex =
        new(@"(?<t>Texture(?:2D|2DArray|Cube|CubeArray|3D)(?:<[^>]+>)?)\s+Material_(?<n>[A-Za-z0-9_]+)\s*:\s*register",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AliasedByteAddressDeclRegex =
        new(@"^\s*ByteAddressBuffer\s+T(?<n>\d+)_\d+\s*:\s*register\(t\k<n>[^\)]*\);\s*\r?\n",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex AliasedByteAddressRefRegex =
        new(@"\bT(\d+)_\d+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SamplerStateDeclRegex =
        new(@"\bSamplerState\s+(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*register", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string AdaptHlslForUnity(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;

        // Stage 1: textures + their paired samplers under the Material_<X>
        // convention. The sampler rename happens FIRST so the texture-paired
        // names (`sampler_<X>`) survive the generic sampler fixup in stage 2.
        body = MaterialSamplerDeclRegex.Replace(body, "sampler_${n}");

        HashSet<string> renamedTextures = new(StringComparer.Ordinal);
        body = MaterialTextureDeclRegex.Replace(body, m =>
        {
            renamedTextures.Add(m.Groups["n"].Value);
            return $"{m.Groups["t"].Value} _{m.Groups["n"].Value} : register";
        });
        foreach (string name in renamedTextures)
        {
            string from = "Material_" + name;
            string to = "_" + name;
            body = System.Text.RegularExpressions.Regex.Replace(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(from)}\b", to);
        }

        // Stage 2: rename any remaining SamplerState declaration that isn't
        // already in Unity-recognized form. Unity accepts two sampler shapes:
        //   * paired-with-texture: `sampler<TextureName>` (we handled the
        //     Material_ case above)
        //   * inline-mode: contains `Point|Linear|Trilinear` + `Clamp|Repeat|...`
        // Anything else (e.g. SPIRV-Cross's `View_Sampler39`) gets rejected
        // outright. We rewrite the leftover declarations to a name that
        // preserves the original token for greppability AND contains the
        // inline-mode tokens Unity wants, so the binding is accepted and
        // gets the linear/clamp default. This loses the original sampler
        // filtering mode in the worst case — recoverable later via
        // metadata once UE View_*Sampler binding-state is plumbed through.
        HashSet<string> renamedSamplers = new(StringComparer.Ordinal);
        body = SamplerStateDeclRegex.Replace(body, m =>
        {
            string name = m.Groups["n"].Value;
            if (name.StartsWith("sampler_", StringComparison.Ordinal) || ContainsInlineSamplerMode(name))
            {
                return m.Value;
            }
            renamedSamplers.Add(name);
            return $"SamplerState sampler{name}_LinearClamp : register";
        });
        foreach (string name in renamedSamplers)
        {
            string to = $"sampler{name}_LinearClamp";
            body = System.Text.RegularExpressions.Regex.Replace(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b(?!\s*:\s*register)", to);
        }

        body = AliasedByteAddressDeclRegex.Replace(body, string.Empty);
        body = AliasedByteAddressRefRegex.Replace(body, "T$1");

        return body;
    }

    private static bool ContainsInlineSamplerMode(string name)
    {
        // Unity's inline-sampler heuristic looks for filter + wrap tokens in
        // the identifier. We keep the check conservative — only accept names
        // that contain BOTH a filter and a wrap mode so we don't pass a name
        // that Unity will still reject downstream.
        bool hasFilter = name.Contains("Point", StringComparison.Ordinal)
                         || name.Contains("Linear", StringComparison.Ordinal)
                         || name.Contains("Trilinear", StringComparison.Ordinal);
        bool hasWrap = name.Contains("Clamp", StringComparison.Ordinal)
                       || name.Contains("Repeat", StringComparison.Ordinal)
                       || name.Contains("Mirror", StringComparison.Ordinal);
        return hasFilter && hasWrap;
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
    // Variant filename / `#if defined(...)` keyword. Format:
    //   <Stage>_<ShaderTypeShort?>_<VFShort?>_PERM<id>?_<ShortHash|IDXn>
    //
    // Always leads with the shader stage (VS/PS/HS/DS/GS/CS/...) so the
    // filename is self-describing at a glance — `PS_TBasePassPSFNoLightMap_FLocalVF_PERM0_AB12CDEF.hlsl`
    // rather than the previous opaque `VARIANT_IDX001634.hlsl`.
    // ShaderType and VertexFactoryType are compressed to their leading
    // identifier (template args stripped) so the filename stays under
    // typical OS path-length limits even for deeply-templated UE shader
    // types like `TBasePassPS<FNoLightMapPolicy, false, GBL_Default>`.
    private static string BuildVariantKeyword(UeShaderLabProgramData program)
    {
        StringBuilder sb = new();
        // Stage always comes first — this is the user-facing "what is
        // this shader" anchor (VS/PS/HS/...). Falls back to "VARIANT"
        // when stage is somehow missing (defensive — shouldn't happen
        // in practice).
        sb.Append(string.IsNullOrWhiteSpace(program.Stage) ? "VARIANT" : program.Stage);

        if (!string.IsNullOrWhiteSpace(program.ShaderTypeName))
        {
            sb.Append('_').Append(CompressTemplateIdent(program.ShaderTypeName));
        }
        if (!string.IsNullOrWhiteSpace(program.VertexFactoryTypeName))
        {
            sb.Append('_').Append(CompressTemplateIdent(program.VertexFactoryTypeName));
        }
        if (program.PermutationId >= 0)
        {
            sb.Append("_PERM").Append(program.PermutationId);
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

    // Compresses a templated C++ type identifier into a short, file-safe form.
    //   TBasePassPS<FNoLightMapPolicy, false, GBL_Default>
    //     -> TBasePassPSFNoLightMapPolicy
    // Keeps the first template arg (it's the policy/permutation discriminator
    // in 99% of UE shader types) but drops the rest so filenames stay
    // readable on Windows (260-char path limit on default install). Falls
    // back to plain SanitizeIdent when there are no template brackets.
    private static string CompressTemplateIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int lt = raw.IndexOf('<');
        if (lt < 0) return SanitizeIdent(raw);
        string head = raw.Substring(0, lt);
        // Take the first template arg (up to the first comma at depth 1).
        int depth = 0;
        int firstArgEnd = raw.Length;
        for (int i = lt; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 1) { firstArgEnd = i; break; }
        }
        string firstArg = (firstArgEnd > lt + 1) ? raw.Substring(lt + 1, firstArgEnd - lt - 1).Trim() : string.Empty;
        return SanitizeIdent(string.IsNullOrEmpty(firstArg) ? head : (head + "_" + firstArg));
    }

    // Rewrite the SPIRV-Cross default `_Globals_m0[N]` flat-array form
    // into a class-tagged name so the user can see at a glance which
    // shader's loose parameters are at play. The flat-array form remains
    // an array (no individual member naming — that would require
    // restructuring the cbuffer block, which only the rewriter can do
    // safely) but the IDENTIFIER acquires class context.
    //
    // Example transform when `ShaderTypeName="FLumenCardVS"`:
    //   cbuffer type_Globals : register(b0, space0)
    //   {
    //       float4 _Globals_m0[2] : packoffset(c0);
    //   };
    //   ...
    //   _Globals_m0[1u].y    →    _looseFLumenCardVS[1u].y
    //
    // No-op when ShaderTypeName is empty (we have nothing better than the
    // SPIRV-Cross default) or when `_Globals_m0` isn't in the source
    // (already named via seed reconciliation OR no $Globals cbuffer at
    // all). This pass is intentionally a single string-replace, never
    // touching shader structure — the rewriter remains the only piece
    // that mutates SPIR-V.
    /// <summary>
    /// 按 UE 的材质贴图**声明序**给匿名槽命名(见调用点注释)。已具名的 <c>Material_*</c> 槽当锚点校验;
    /// 任一锚点对不上就整体放弃(宁可无名,不可错名)。
    /// </summary>
    private static void ApplyMaterialTextureOrder(
        string source,
        List<(string Ident, string HlslType, string UbmtKind, string SlotPrefix, string SlotIdx)> anons,
        Dictionary<int, string> rename,
        HashSet<int> claimed,
        SerializedProgramData? symbolMetadata)
    {
        if (symbolMetadata == null || symbolMetadata.TextureParameters.Count == 0) return;

        // 本 shader 的全部贴图声明(含已具名的),按 t 槽升序。
        var declarations = new List<(string Name, int Slot, bool IsAnon, int AnonIndex)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            source, @"(?m)^\s*Texture(?:2D|2DArray|Cube|CubeArray|3D|1D)\s*(?:<[^>]*>)?\s+(\w+)\s*:\s*register\(t(\d+)"))
        {
            string ident = m.Groups[1].Value;
            int slot = int.Parse(m.Groups[2].Value);
            int anonIndex = anons.FindIndex(a => a.Ident == ident);
            declarations.Add((ident, slot, anonIndex >= 0, anonIndex));
        }
        if (declarations.Count == 0) return;
        declarations.Sort((a, b) => a.Slot.CompareTo(b.Slot));

        // 材质贴图 = 不是引擎侧 `View_*`/`Scene_*` 的那些。引擎槽与材质槽交错出现,故先滤掉。
        var materialSlots = declarations
            .Where(d => !d.Name.StartsWith("View_", StringComparison.Ordinal)
                     && !d.Name.StartsWith("Scene_", StringComparison.Ordinal)
                     && !d.Name.StartsWith("TranslucentBasePass_", StringComparison.Ordinal)
                     && !d.Name.StartsWith("OpaqueBasePass_", StringComparison.Ordinal))
            .ToList();
        if (materialSlots.Count == 0) return;

        List<string> order = symbolMetadata.TextureParameters.Select(t => t.Name).ToList();
        if (order.Count < materialSlots.Count)
        {
            Console.Error.WriteLine($"[Pass200] material-texture order: 槽 {materialSlots.Count} 个 > UES 名表 {order.Count} 项 — 放弃按序命名(宁可无名)。");
            return;
        }

        // 锚点校验:已具名槽的名字必须等于其位置上的 UES 名。
        for (int i = 0; i < materialSlots.Count; i++)
        {
            var slot = materialSlots[i];
            if (slot.IsAnon) continue;
            string expected = "Material_" + SanitizeIdent(order[i]);
            if (!string.Equals(slot.Name, expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"[Pass200] material-texture order: 锚点不符(t{slot.Slot} 已具名 '{slot.Name}',按序应为 '{expected}')" +
                    " — 声明序假设不成立,放弃按序命名。");
                return;
            }
        }

        for (int i = 0; i < materialSlots.Count; i++)
        {
            var slot = materialSlots[i];
            if (!slot.IsAnon || claimed.Contains(slot.AnonIndex)) continue;
            rename[slot.AnonIndex] = "Material_" + SanitizeIdent(order[i]);
            claimed.Add(slot.AnonIndex);
        }
    }

    private static string RenameAnonymousGlobals(string source, string shaderTypeName, string shaderHash, SerializedProgramData? symbolMetadata)
    {
        if (string.IsNullOrWhiteSpace(source)) return source;
        // Discriminator priority:
        //   ShaderTypeName — real C++ class name from the seed registry.
        // NO hash fallback: a shader-hash discriminator carries no semantic
        // information (it identifies a permutation, which is already in the
        // filename). When the ShaderType doesn't resolve (Material-class
        // shaders, custom game shader types missing from the engine seed
        // registry), `_Globals_m0` is LEFT AS-IS. The cbuffer block is
        // already named `type_Globals` and spirv-cross only emits one such
        // member per shader, so the canonical default token stays uniquely
        // identifiable per-file without being polluted with hash noise.
        string discriminator = string.IsNullOrWhiteSpace(shaderTypeName)
            ? string.Empty
            : SanitizeIdent(shaderTypeName);

        string result = source;

        // 1. Rename the SPIRV-Cross default $Globals member when the
        //    runtime didn't successfully reconcile it AND we have a real
        //    class discriminator. Plain string-replace is safe — the
        //    SPIRV-Cross convention emits a single token `_Globals_m0` per
        //    shader with no name collisions.
        if (!string.IsNullOrEmpty(discriminator) && result.Contains("_Globals_m0", StringComparison.Ordinal))
        {
            result = result.Replace("_Globals_m0", $"_loose_{discriminator}", StringComparison.Ordinal);
        }

        // 2. Rename anonymous `T<N>` texture bindings and `U<N>` UAV
        //    bindings that the SRT decoder failed to symbolise. These
        //    are real bindings the cooked shader exposes at register
        //    t<N>/u<N> — engine-side resources (volumetric lightmaps,
        //    BasePass globals, landscape continuous-LOD tables, etc.)
        //    whose owning UB isn't in the runtime's seed-name index.
        //    Without this fallback they stay as the SPIRV-Cross default
        //    identifiers `T0/T1/U0/U1/...` — opaque, indistinguishable
        //    across shaders that reuse the same numeric slot.
        //
        //    The transform converts both the declaration and every usage
        //    into `<class>_<original>` (e.g. `MainGrid_L2_T5`). Detection
        //    uses a regex anchored on the canonical declaration form
        //    (`^<Type> <prefix><N> : register(<x><N>`) so unrelated
        //    identifiers that happen to look like `T<digits>` or
        //    `U<digits>` are left alone; references are then renamed
        //    via word-boundary replace.
        if (result.Contains(" : register(", StringComparison.Ordinal))
        {
            // PASS 1: scan for all anonymous declarations and gather them.
            // Each entry: (ident, hlslType, ubmtKind, slotPrefix, slotIdx).
            List<(string Ident, string HlslType, string UbmtKind, string SlotPrefix, string SlotIdx)> anons = new();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                result,
                @"^([A-Za-z][A-Za-z0-9_]*(?:<[^>]+>)?)\s+([TU]\d+|_\d+)\s*:\s*register\(([tusb])(\d+)",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                string hlslType = m.Groups[1].Value.Trim();
                string ident = m.Groups[2].Value;
                string slotPrefix = m.Groups[3].Value;
                string slotIdx = m.Groups[4].Value;
                string ubmtKind = ClassifyUbmtFromHlslType(hlslType, slotPrefix);
                anons.Add((ident, hlslType, ubmtKind, slotPrefix, slotIdx));
            }

            // PASS 2: figure out which engine UBs this shader uses AS
            // CBUFFERS. Only `cbuffer type_<UB> :` declarations qualify —
            // those are per-shader-bound, and the cook emits resources in
            // source-declaration order, so prefix-subset rename is safe
            // (the first N anonymous slots correspond to the first N
            // resources of the right type in the UB's declaration).
            //
            // STATIC UBs (OpaqueBasePass, Nanite, etc.) that the shader
            // uses WITHOUT declaring as a cbuffer are intentionally
            // EXCLUDED from this set — their textures are bound via
            // global static slots in a non-prefix subset, so prefix-subset
            // would assign wrong names. Those slots are routed through
            // ApplyUsagePatternMatches (PASS 3.5) instead, which reads the
            // shader code's Sample call patterns to identify specific
            // bindings (DBufferA/B/C, etc.) from source truth.
            HashSet<string> shaderUsedUbs = new(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                result,
                @"^cbuffer\s+type_([A-Za-z_][A-Za-z0-9_]*)\s*:",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                shaderUsedUbs.Add(m.Groups[1].Value.ToLowerInvariant());
            }

            // PASS 3: per (ubmtKind, hlslType, slot-contiguous-region)
            // group, try count-matching against engine UB metadata.
            // If exactly one used UB matches and its resource count ==
            // anonymous count, assign names in declaration order.
            //
            // SLOT-GAP SPLIT: the cooker allocates t-registers per-UB in
            // declaration order, so a UB's textures land at *contiguous*
            // t-slots. When a same-type group has a slot gap (e.g.
            // t10-t14 contiguous, then t21-t22 contiguous, with named
            // Material_Texture2D_N slots in between at t15-t20), those
            // two clusters belong to DIFFERENT UBs. Splitting at the gap
            // turns a 7-anon group "no UB has 7" miss into two separate
            // sub-group matches.
            Dictionary<(string, string), List<int>> anonsByType = new();
            for (int i = 0; i < anons.Count; i++)
            {
                if (string.IsNullOrEmpty(anons[i].UbmtKind)) continue;
                var key = (anons[i].UbmtKind, anons[i].HlslType);
                if (!anonsByType.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    anonsByType[key] = list;
                }
                list.Add(i);
            }
            Dictionary<int, string> rename = new();
            HashSet<int> claimedByOrdered = new();
            foreach (KeyValuePair<(string UbmtKind, string HlslType), List<int>> grp in anonsByType)
            {
                // Sort anons in this (UbmtKind, HlslType) group by t-slot
                // ascending, then split into contiguous runs. A run breaks
                // when the next anon's slot is more than 1 above the
                // previous anon's slot — i.e. some other binding sits
                // between them (could be a resolved named texture, or a
                // different-type anon, doesn't matter).
                List<int> bySlot = new(grp.Value);
                bySlot.Sort((a, b) => int.Parse(anons[a].SlotIdx).CompareTo(int.Parse(anons[b].SlotIdx)));
                List<List<int>> runs = new();
                List<int> currentRun = new();
                int prevSlot = int.MinValue;
                foreach (int idx in bySlot)
                {
                    int s = int.Parse(anons[idx].SlotIdx);
                    if (currentRun.Count > 0 && s > prevSlot + 1)
                    {
                        runs.Add(currentRun);
                        currentRun = new List<int>();
                    }
                    currentRun.Add(idx);
                    prevSlot = s;
                }
                if (currentRun.Count > 0) runs.Add(currentRun);

                foreach (List<int> run in runs)
                {
                    IReadOnlyList<string>? ordered = EngineTypeUniquenessIndex.TryResolveOrderedByUbContext(
                        grp.Key.Item1, grp.Key.Item2, shaderUsedUbs, run.Count, out string ownerUb);
                    if (Environment.GetEnvironmentVariable("RURI_UB_DEBUG") == "1")
                    {
                        int firstSlot = int.Parse(anons[run[0]].SlotIdx);
                        int lastSlot = int.Parse(anons[run[^1]].SlotIdx);
                        string usedUbsCsv = string.Join(",", shaderUsedUbs);
                        Console.Error.WriteLine($"[Pass200][rename] type=({grp.Key.Item1}|{grp.Key.Item2}) run=t{firstSlot}..t{lastSlot} count={run.Count} usedUbs=[{usedUbsCsv}] -> ownerUb={ownerUb} ordered={(ordered == null ? "<null>" : string.Join(",", ordered))}");
                    }
                    if (ordered == null || ordered.Count != run.Count) continue;
                    for (int i = 0; i < run.Count; i++)
                    {
                        int idx = run[i];
                        rename[idx] = $"{ownerUb}_{ordered[i]}";
                        claimedByOrdered.Add(idx);
                    }
                }
            }

            // PASS 3.5: usage-pattern based recovery for STATIC UB
            // resources the cooker binds out-of-order (DBuffer textures
            // etc.). When the shader source itself reveals what a slot is
            // used for — e.g. 3 consecutive Texture2D's sampled at the
            // screen-space UV `gl_FragCoord.xy * View_BufferSizeAndInvSize`
            // gated by `View_ShowDecalsMask` — these are unambiguously
            // OpaqueBasePass.DBufferA/B/C in that order. The pattern is
            // distinctive enough that this is source-truth, not guessing.
            ApplyUsagePatternMatches(result, anons, rename, claimedByOrdered);

            // PASS 3.7: MATERIAL textures by declaration order. `symbolMetadata.TextureParameters`
            // now carries the material's `UniformTextureParameters` flattened in UE's own
            // declaration order (MaterialTextureOrder), and DXC assigns `t` registers in that
            // same order — so the k-th material texture slot IS the k-th name.
            //
            // Verification, not faith: some slots already carry a `Material_<name>` symbol from
            // the SPIR-V naming path. Those act as ANCHORS — if the positional assignment
            // disagrees with any anchor, the ordering assumption doesn't hold for this shader and
            // we assign nothing (a wrong texture name silently swaps BaseColor for a mask).
            ApplyMaterialTextureOrder(result, anons, rename, claimedByOrdered, symbolMetadata);

            // PASS 4: for anons not claimed by count-matching, try the
            // global type-uniqueness (one engine candidate of that type
            // across the entire engine), then fall back to hash-tagged.
            //
            // AMBIGUITY GUARD (correctness red line): "unique" means the ENGINE has
            // exactly ONE resource of this (kind, hlslType) — so at most one binding
            // in this shader can BE it. Applying that single name to every unclaimed
            // anon of the type emits DUPLICATE declarations (invalid HLSL: the same
            // identifier declared at two different registers) and mislabels every
            // slot but one. Measured on the X6Game base-pass PS:
            // View_AtmosphereTransmittanceTexture at both t2 and t6,
            // View_VolumetricLightmapBrickAmbientVector ×3,
            // TranslucentBasePass_..._DirectionalLightShadowmapAtlas ×5.
            // A wrong name is worse than no name → an ambiguous group stays anonymous.
            Dictionary<(string, string), int> unclaimedByType = new();
            for (int i = 0; i < anons.Count; i++)
            {
                if (claimedByOrdered.Contains(i) || string.IsNullOrEmpty(anons[i].UbmtKind)) continue;
                (string, string) countKey = (anons[i].UbmtKind, anons[i].HlslType);
                unclaimedByType[countKey] = unclaimedByType.GetValueOrDefault(countKey) + 1;
            }

            for (int i = 0; i < anons.Count; i++)
            {
                if (claimedByOrdered.Contains(i)) continue;
                var a = anons[i];
                if (!string.IsNullOrEmpty(a.UbmtKind)
                    && unclaimedByType.GetValueOrDefault((a.UbmtKind, a.HlslType)) == 1
                    && EngineTypeUniquenessIndex.TryResolveUnique(a.UbmtKind, a.HlslType, out string ubName, out string resName))
                {
                    rename[i] = $"{ubName}_{resName}";
                    continue;
                }
                string suffix = a.Ident.StartsWith("_", StringComparison.Ordinal)
                    ? $"{a.SlotPrefix.ToUpperInvariant()}{a.SlotIdx}"
                    : a.Ident;
                // No discriminator → keep the original anonymous identifier
                // (T<N> / U<N> / _<N>). Better than an empty-prefix
                // `_<suffix>` that's harder to grep and looks broken.
                rename[i] = string.IsNullOrEmpty(discriminator) ? a.Ident : $"{discriminator}_{suffix}";
            }

            // PASS 5: apply the rename map. We have to dedupe by ORIGINAL
            // identifier (different anon entries can share an identifier
            // when SPIRV-Cross emitted dedup'd `_<id>_1` etc. — same SSA
            // id, multiple declarations). Use the first rename for each
            // identifier; word-boundary replace covers references.
            Dictionary<string, string> identToFinal = new(StringComparer.Ordinal);
            for (int i = 0; i < anons.Count; i++)
            {
                if (!identToFinal.ContainsKey(anons[i].Ident))
                {
                    identToFinal[anons[i].Ident] = rename[i];
                }
            }
            // COLLISION BACKSTOP: whatever route produced a name (ordered
            // count-match, usage pattern, global uniqueness), two DIFFERENT original
            // identifiers must never end up with the SAME final name — that emits two
            // declarations of one identifier at two registers, which is not valid HLSL
            // and means at least one of the two names is factually wrong. When it
            // happens, revert the whole colliding set to its anonymous identifiers and
            // say so loudly: an unnamed slot is honest, a mislabelled one is not.
            Dictionary<string, List<string>> finalToIdents = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in identToFinal)
            {
                if (!finalToIdents.TryGetValue(kv.Value, out List<string>? owners))
                {
                    owners = new List<string>();
                    finalToIdents[kv.Value] = owners;
                }
                owners.Add(kv.Key);
            }
            foreach (KeyValuePair<string, List<string>> kv in finalToIdents)
            {
                if (kv.Value.Count <= 1) continue;
                Console.Error.WriteLine(
                    $"[Pass200] name collision: '{kv.Key}' claimed by {kv.Value.Count} bindings " +
                    $"({string.Join(", ", kv.Value)}) — reverting them to anonymous identifiers " +
                    "(a wrong symbol is worse than none).");
                foreach (string ident in kv.Value) identToFinal[ident] = ident;
            }

            if (Environment.GetEnvironmentVariable("RURI_UB_DEBUG") == "1")
            {
                foreach (KeyValuePair<string, string> kv in identToFinal)
                {
                    Console.Error.WriteLine($"[Pass200][applyRename] '{kv.Key}' -> '{kv.Value}'");
                }
            }
            foreach (KeyValuePair<string, string> kv in identToFinal)
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + @"\b",
                    kv.Value);
            }
        }

        // 3. Rename anonymous `<CBName>_m0[` flat-array members where the
        //    cbuffer block carries a real name but its single member is
        //    still the SPIRV-Cross default `_m0`. This shows up for
        //    cbuffers the rewriter didn't restructure (e.g.
        //    `MaterialCollection0` when the project's
        //    UMaterialParameterCollection resolution failed for a
        //    specific shader). The block-name already provides context,
        //    so the member just needs a less opaque suffix —
        //    `MaterialCollection0_m0` -> `MaterialCollection0_loose`.
        //    Targeted match: only rename when the token follows a known
        //    cbuffer name AND the cbuffer was declared in this file
        //    (collected from `cbuffer type_<Name>` lines so we don't
        //    accidentally rename `Material_m0` if Material isn't a CB).
        if (result.Contains("_m0", StringComparison.Ordinal))
        {
            HashSet<string> cbufferNames = new(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                result,
                @"^cbuffer\s+type_([A-Za-z_][A-Za-z0-9_]*)\s*:",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                cbufferNames.Add(m.Groups[1].Value);
            }
            foreach (string cb in cbufferNames)
            {
                string token = $"{cb}_m0";
                if (!result.Contains(token, StringComparison.Ordinal)) continue;
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b",
                    $"{cb}_loose");
            }
        }

        return result;
    }

    // Usage-pattern recovery for anonymous textures the cooker bound from
    // STATIC UBs (where prefix-subset can't safely guess which subset of
    // a UB's many resources is in use). Reads the HLSL body, finds the
    // Sample / SampleLevel / SampleBias call sites for each anonymous
    // identifier, and matches each call's (sampler, UV expression,
    // surrounding math) against well-known UE built-in patterns.
    //
    // Strictly source-truth: a match requires a distinctive co-occurrence
    // pattern that the shader source itself reveals. No defaulting,
    // no guessing — when no pattern matches, the slot stays anonymous.
    //
    // Patterns currently recognised:
    //   - DBufferATexture / DBufferBTexture / DBufferCTexture
    //     Triple of Texture2D's sampled at the screen-space UV
    //     `(gl_FragCoord.xy * View_BufferSizeAndInvSize.zw)` gated by
    //     `View_ShowDecalsMask` and the per-primitive decal-receive bit
    //     `View_PrimitiveSceneData.Load(...) & 8u`. Engine source:
    //     `Engine/Shaders/Private/DBufferDecalShared.ush` —
    //     this triple of bindings is unique to OpaqueBasePass decal use.
    //
    //   - LandscapeParameters.NormalmapTexture
    //     Sampled at landscape UV (TEXCOORD_1.zw), result's .z/.w
    //     channels processed by `mad(*, 2.0f, -1.0f)` then
    //     `sqrt(max(1 - dot(xy, xy), 0))` — classic BC5/ATI2 packed
    //     normal reconstruction. Engine source:
    //     `Engine/Shaders/Private/LandscapeCommon.ush`.
    private static void ApplyUsagePatternMatches(
        string hlsl,
        List<(string Ident, string HlslType, string UbmtKind, string SlotPrefix, string SlotIdx)> anons,
        Dictionary<int, string> rename,
        HashSet<int> claimed)
    {
        // Build identifier -> full-statement-line map. Capture the WHOLE
        // statement (up to the next `;`) rather than trying to balance
        // parens — a non-greedy `[^;]+?\)` truncates at the first inner
        // close-paren of `float2(...)` and loses tail arguments like
        // `View_MaterialTextureMipBias` that pattern-matchers downstream
        // rely on.
        Dictionary<string, string> sampleCallByIdent = new(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            hlsl,
            @"([TU]\d+|_\d+)\.Sample(?:Level|Bias|Grad|Cmp|CmpLevelZero)?\([^;]+;"))
        {
            string id = m.Groups[1].Value;
            if (!sampleCallByIdent.ContainsKey(id))
                sampleCallByIdent[id] = m.Value;
        }

        // DBuffer triple detection. Find anons whose Sample's UV uses
        // `gl_FragCoord.* * View_BufferSizeAndInvSize.*` — those are screen-
        // space-tiled. When THREE consecutive (by t-slot) anons share that
        // UV pattern AND the shader references `View_ShowDecalsMask` AND
        // `View_PrimitiveSceneData.Load(...) & 8u`, the triple is
        // unambiguously DBufferA/B/C.
        bool decalsGate = hlsl.Contains("View_ShowDecalsMask", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(
                hlsl,
                @"View_PrimitiveSceneData\.Load<uint>\([^)]+\)\s*&\s*8u");
        if (decalsGate)
        {
            // Pair each anon (by index in `anons`) with whether its Sample
            // call uses the screen-space DBuffer UV.
            List<(int AnonIdx, int Slot)> dbufCandidates = new();
            for (int i = 0; i < anons.Count; i++)
            {
                if (claimed.Contains(i)) continue;
                var a = anons[i];
                if (a.SlotPrefix != "t") continue;
                if (!a.HlslType.StartsWith("Texture2D", StringComparison.Ordinal)) continue;
                if (!sampleCallByIdent.TryGetValue(a.Ident, out string? callSite)) continue;
                // UV form: `float2(_NNN, _NNN)` where each operand traces
                // back to `gl_FragCoord.x * View_BufferSizeAndInvSize.z`
                // and `gl_FragCoord.y * View_BufferSizeAndInvSize.w`. The
                // SPIRV-Cross emitter assigns those products to local
                // floats first, so the Sample call's args reference those
                // locals — find the assignment lines in the shader body.
                if (!IsSampleUvFromScreenSpace(hlsl, callSite)) continue;
                if (int.TryParse(a.SlotIdx, out int slot))
                    dbufCandidates.Add((i, slot));
            }
            // DBufferA/B/C are bound at THREE consecutive t-slots. Sort by
            // slot ascending and look for a run of EXACTLY 3 contiguous.
            dbufCandidates.Sort((x, y) => x.Slot.CompareTo(y.Slot));
            for (int j = 0; j + 2 < dbufCandidates.Count + 1 && j + 2 < dbufCandidates.Count; j++)
            {
                if (dbufCandidates[j + 1].Slot == dbufCandidates[j].Slot + 1
                    && dbufCandidates[j + 2].Slot == dbufCandidates[j].Slot + 2)
                {
                    string[] dbufNames = { "DBufferATexture", "DBufferBTexture", "DBufferCTexture" };
                    for (int k = 0; k < 3; k++)
                    {
                        int idx = dbufCandidates[j + k].AnonIdx;
                        rename[idx] = $"OpaqueBasePass_{dbufNames[k]}";
                        claimed.Add(idx);
                    }
                    break; // one triple per shader (DBuffer is bound once)
                }
            }
        }

        // Landscape per-component WeightmapTexture: a Texture2D whose
        // Sample result is dot-producted with one or more `Material_LayerMask_*`
        // vec4 parameters. The material's LayerMask parameters encode which
        // RGBA channel of the weightmap stores each landscape layer's
        // per-pixel weight. UE landscape rendering binds these textures
        // as loose shader parameters via `FLandscapeBatchElementParams::
        // WeightmapTextures` — they don't live in any UB metadata seed,
        // but the shader's dot(sampleRGBA, Material_LayerMask_*) calls
        // are a distinctive co-occurrence pattern.
        for (int i = 0; i < anons.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            var a = anons[i];
            if (a.SlotPrefix != "t") continue;
            if (!a.HlslType.StartsWith("Texture2D", StringComparison.Ordinal)) continue;
            if (!sampleCallByIdent.TryGetValue(a.Ident, out string? callSite)) continue;
            System.Text.RegularExpressions.Match assignMatch = System.Text.RegularExpressions.Regex.Match(
                hlsl,
                @"float4\s+(_\d+)\s*=\s*" + System.Text.RegularExpressions.Regex.Escape(callSite));
            if (!assignMatch.Success) continue;
            string local = assignMatch.Groups[1].Value;
            int searchStart = assignMatch.Index + assignMatch.Length;
            int searchEnd = Math.Min(hlsl.Length, searchStart + 2000);
            string window = hlsl.Substring(searchStart, searchEnd - searchStart);
            // Look for `dot(... <local>.<chan> ..., Material_LayerMask_*)`
            // — at minimum TWO distinct LayerMask references near this
            // local's use, to distinguish from one-off material samples.
            System.Text.RegularExpressions.MatchCollection layerMaskMatches =
                System.Text.RegularExpressions.Regex.Matches(
                    window,
                    @"Material_LayerMask_[A-Za-z0-9_]+");
            if (layerMaskMatches.Count < 2) continue;
            // Also verify the sample local appears in the dot context — to
            // exclude false positives where LayerMask is used elsewhere
            // unrelated to this texture. `[^;]` allows nested parens
            // (`dot(float4(_354.yzw, …), float4(Material_LayerMask_…))`)
            // to live inside one statement; `[^)]` would clip at the
            // inner float4's close-paren.
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                window,
                @"dot\([^;]*" + System.Text.RegularExpressions.Regex.Escape(local) + @"[^;]*Material_LayerMask")) continue;
            rename[i] = "Landscape_WeightmapTexture";
            claimed.Add(i);
        }

        // Material extra Texture2D continuation: a Texture2D whose Sample
        // call uses `View_MaterialTextureMipBias` AND has its UV computed
        // from world-tile transforms (`View_ViewTilePosition` + Material
        // or MaterialCollection scale parameters). The `MaterialTextureMipBias`
        // is the canonical UE signal for ALL Material UB texture samples —
        // engine source `Common.ush` `Texture2DSampleBias_Material` always
        // passes `View.MaterialTextureMipBias`. Combined with material-
        // driven UV math, this is unambiguously a Material UB texture
        // that the MaterialUniformBufferLayout reader missed (either the
        // material's UniformTextureParameters[0] count is short of the
        // cooker's actual binding count, or the texture lives in a
        // non-Standard2D bucket the reader doesn't enumerate).
        //
        // Numbering continues from the highest existing
        // `Material_Texture2D_N` slot in ascending t-slot order. Source-
        // truth signal: only fire when the sample uses MaterialTextureMipBias
        // — refuses to extend the sequence for unrelated anonymous slots.
        int maxMaterialN = 0;
        System.Text.RegularExpressions.MatchCollection materialMatches =
            System.Text.RegularExpressions.Regex.Matches(
                hlsl,
                @"Material_Texture2D_(\d+)\s*:\s*register");
        foreach (System.Text.RegularExpressions.Match mm in materialMatches)
        {
            if (int.TryParse(mm.Groups[1].Value, out int n) && n > maxMaterialN) maxMaterialN = n;
        }
        if (maxMaterialN > 0)
        {
            List<(int AnonIdx, int Slot)> candidates = new();
            for (int i = 0; i < anons.Count; i++)
            {
                if (claimed.Contains(i)) continue;
                var a = anons[i];
                if (a.SlotPrefix != "t") continue;
                if (!a.HlslType.StartsWith("Texture2D", StringComparison.Ordinal)) continue;
                if (!sampleCallByIdent.TryGetValue(a.Ident, out string? callSite)) continue;
                if (!callSite.Contains("View_MaterialTextureMipBias", StringComparison.Ordinal)) continue;
                if (int.TryParse(a.SlotIdx, out int slot)) candidates.Add((i, slot));
            }
            candidates.Sort((x, y) => x.Slot.CompareTo(y.Slot));
            int nextN = maxMaterialN + 1;
            foreach (var c in candidates)
            {
                rename[c.AnonIdx] = $"Material_Texture2D_{nextN}";
                claimed.Add(c.AnonIdx);
                nextN++;
            }
        }

        // LandscapeParameters.NormalmapTexture: a Texture2D whose Sample
        // result's .z/.w channels are biased by `mad(*, 2.0f, -1.0f)` and
        // followed by a sqrt(1 - dot(xy,xy)) reconstruction. The .zw
        // (alpha + blue) encoding is specific to UE's landscape
        // heightmap+normal-packed format. Look at LandscapeCommon.ush.
        for (int i = 0; i < anons.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            var a = anons[i];
            if (a.SlotPrefix != "t") continue;
            if (!a.HlslType.StartsWith("Texture2D", StringComparison.Ordinal)) continue;
            if (!sampleCallByIdent.TryGetValue(a.Ident, out string? callSite)) continue;
            // Find the local variable assigned the sample result, e.g.
            // `float4 _136 = T13.Sample(...)`. Then check whether
            // `_136.z` and `_136.w` appear inside `mad(_136.z, 2.0f, -1.0f)`
            // etc. in the next ~30 lines.
            System.Text.RegularExpressions.Match assignMatch = System.Text.RegularExpressions.Regex.Match(
                hlsl,
                @"float4\s+(_\d+)\s*=\s*" + System.Text.RegularExpressions.Regex.Escape(callSite));
            if (!assignMatch.Success) continue;
            string local = assignMatch.Groups[1].Value;
            int searchStart = assignMatch.Index + assignMatch.Length;
            int searchEnd = Math.Min(hlsl.Length, searchStart + 2000);
            string window = hlsl.Substring(searchStart, searchEnd - searchStart);
            bool zwBias = System.Text.RegularExpressions.Regex.IsMatch(
                window, @"mad\(" + System.Text.RegularExpressions.Regex.Escape(local) + @"\.z,\s*2\.0f,\s*-1\.0f\)")
                && System.Text.RegularExpressions.Regex.IsMatch(
                window, @"mad\(" + System.Text.RegularExpressions.Regex.Escape(local) + @"\.w,\s*2\.0f,\s*-1\.0f\)");
            if (!zwBias) continue;
            rename[i] = "LandscapeParameters_NormalmapTexture";
            claimed.Add(i);
        }
    }

    // Resolve whether a Sample call's UV arguments trace back to the
    // screen-space DBuffer UV pattern
    // `(gl_FragCoord.xy * View_BufferSizeAndInvSize.zw)`. The SPIRV-Cross
    // emitter materialises the multiplies into local floats; we follow
    // the chain by looking for `float _NNN = gl_FragCoord.x *
    // View_BufferSizeAndInvSize.z` (and y/.w) earlier in the body.
    private static bool IsSampleUvFromScreenSpace(string hlsl, string sampleCall)
    {
        // Extract the second argument (UV) — Sample(sampler, uv [, ...]).
        // The simplest robust extraction: pull all `_\d+` operands from
        // the Sample call args, then check that ALL of them have an
        // assignment to `gl_FragCoord.[xy] * View_BufferSizeAndInvSize.[zw]`
        // earlier in the body.
        System.Text.RegularExpressions.MatchCollection idMatches = System.Text.RegularExpressions.Regex.Matches(sampleCall, @"_\d+");
        if (idMatches.Count < 2) return false;
        int matchedUvComponents = 0;
        foreach (System.Text.RegularExpressions.Match idM in idMatches)
        {
            string id = idM.Value;
            if (System.Text.RegularExpressions.Regex.IsMatch(
                hlsl,
                @"float\s+" + System.Text.RegularExpressions.Regex.Escape(id) + @"\s*=\s*gl_FragCoord\.[xy]\s*\*\s*View_BufferSizeAndInvSize\.[zw]"))
            {
                matchedUvComponents++;
            }
        }
        return matchedUvComponents >= 2; // both u and v
    }

    // Classify an HLSL declaration type token (e.g. `Texture2D<float4>`,
    // `RWByteAddressBuffer`, `SamplerState`) into one of UE's
    // EUniformBufferBaseType enum names so the type-uniqueness lookup
    // can match against engine UB metadata.
    //
    // The slot prefix from `register(<t|u|s|b><N>)` is the tie-breaker
    // for ambiguous HLSL types — `Buffer<float4>` on a `u` register is
    // a UAV; on a `t` register it's an SRV. Returns empty string for
    // types we can't classify (caller falls back to hash-tagged rename).
    private static string ClassifyUbmtFromHlslType(string hlslType, string slotPrefix)
    {
        if (string.IsNullOrEmpty(hlslType)) return string.Empty;

        // Strip generic parameters for the head-type check.
        int lt = hlslType.IndexOf('<');
        string head = lt < 0 ? hlslType : hlslType.Substring(0, lt);

        // RW prefix => UAV regardless of head.
        if (head.StartsWith("RW", StringComparison.Ordinal)) return "UBMT_UAV";

        // Sampler types (no RW variant).
        if (head == "SamplerState" || head == "SamplerComparisonState") return "UBMT_SAMPLER";

        // Texture<Dim>: t-register => UBMT_TEXTURE
        //   (also covers TextureCube, Texture2DArray, etc.)
        if (head.StartsWith("Texture", StringComparison.Ordinal))
        {
            return slotPrefix == "u" ? "UBMT_UAV" : "UBMT_TEXTURE";
        }

        // ByteAddressBuffer / Buffer<...> / StructuredBuffer<...>: SRV
        //   on t-register, UAV on u-register.
        if (head == "ByteAddressBuffer"
            || head == "Buffer"
            || head == "StructuredBuffer"
            || head == "AppendStructuredBuffer"
            || head == "ConsumeStructuredBuffer")
        {
            return slotPrefix == "u" ? "UBMT_UAV" : "UBMT_SRV";
        }

        // Ray tracing acceleration structure (SRV).
        if (head == "RaytracingAccelerationStructure") return "UBMT_SRV";

        return string.Empty;
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

        /// <summary>
        /// 材质贴图的**完整声明序**(UES 的 UniformTextureParameters,所有桶展平:
        /// Standard2D → Array2D → Cube → Volume → …)。
        ///
        /// 为什么要单独导出:`Properties {}` 块只覆盖 Standard2D 那一桶,数组/立方体桶的参数
        /// 在里面没有对应项。消费端要把 HLSL 里的匿名贴图槽按声明序对回参数名时,只有
        /// Standard2D 一份表就会产生大量歧义(实测某半透布料 shader:9 项 Properties 对 27 个
        /// 匿名槽,对齐必然猜错,整层纱因此渲成黑)。这张表是无歧义的权威顺序。
        /// </summary>
        public List<string> MaterialTextureOrder { get; set; } = new();

        /// <summary>
        /// <c>Material</c> cbuffer 每个成员**实算出来的值**(成员名 → 逗号分隔的分量)。
        /// 源 = preshader opcode 流的数值求值(见 MaterialConstantBufferReader.EvaluatedCbufferValues)。
        /// 求不出值的成员不在表里。
        /// </summary>
        public Dictionary<string, string> MaterialCbufferValues { get; set; } = new(StringComparer.Ordinal);

        /// <summary>成员名 → cbuffer 字节偏移(见 MaterialCbufferValues 的说明)。</summary>
        public Dictionary<string, int> MaterialCbufferOffsets { get; set; } = new(StringComparer.Ordinal);

        /// <summary>成员名 → 它的**运算程序**(S 表达式)。消费侧拿它 + 自己材质实例的参数重算。</summary>
        public Dictionary<string, string> MaterialCbufferPrograms { get; set; } = new(StringComparer.Ordinal);

        /// <summary>导出侧求值时用的那套参数值(参数原名 → 4 分量),给消费侧做求值器自检。</summary>
        public Dictionary<string, string> MaterialCbufferParams { get; set; } = new(StringComparer.Ordinal);
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
