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
            string source = RenameAnonymousGlobals(program.SourceCode!, program.ShaderTypeName, program.ShaderHash);
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
            string renamed = RenameAnonymousGlobals(program.SourceCode!, program.ShaderTypeName, program.ShaderHash);
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
    private static string RenameAnonymousGlobals(string source, string shaderTypeName, string shaderHash)
    {
        if (string.IsNullOrWhiteSpace(source)) return source;
        // Best discriminator (in priority order):
        //   1. ShaderTypeName - real C++ class name from the seed registry
        //   2. ShaderHash[0:8] - when the class hash didn't resolve to a name
        //      (custom game shaders not in engine source); the cook still
        //      ships a unique-per-shader SHA1 prefix that's stable across
        //      runs and matches the suffix already in the filename.
        // Anything else means we have nothing better than the SPIRV-Cross
        // default - leave the source untouched.
        string discriminator;
        if (!string.IsNullOrWhiteSpace(shaderTypeName))
        {
            discriminator = SanitizeIdent(shaderTypeName);
        }
        else if (!string.IsNullOrWhiteSpace(shaderHash) && shaderHash.Length >= 8)
        {
            discriminator = "h" + SanitizeIdent(shaderHash[..8]);
        }
        else
        {
            return source;
        }
        if (string.IsNullOrEmpty(discriminator)) return source;

        string result = source;

        // 1. Rename the SPIRV-Cross default $Globals member when the
        //    runtime didn't successfully reconcile it. Plain string-
        //    replace is safe — the SPIRV-Cross convention emits a single
        //    token `_Globals_m0` with no name collisions.
        if (result.Contains("_Globals_m0", StringComparison.Ordinal))
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
            HashSet<string> anonNames = new(StringComparison.Ordinal == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                result,
                @"^[A-Za-z][A-Za-z0-9_<>\,\s\.]*\s+([TU]\d+)\s*:\s*register\(",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                anonNames.Add(m.Groups[1].Value);
            }
            foreach (string anon in anonNames)
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(anon) + @"\b",
                    $"{discriminator}_{anon}");
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
