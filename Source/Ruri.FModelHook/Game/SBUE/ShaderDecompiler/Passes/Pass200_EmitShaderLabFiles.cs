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
//   - TryGetStagePragma / GetStageMacro / StageSortKey / ToUnityStageName
//   - WriteProgramBlock / SplitLines
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
        File.WriteAllText(containerBasePath + ".shader", WriteContainerShaderFile(metadata));
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

    private static string WriteContainerShaderFile(UeShaderLabContainerMetadata metadata)
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
            sb.AppendLine($"            Name \"{metadata.MaterialName}\"");
            if (!string.IsNullOrWhiteSpace(metadata.ContainerKey)) sb.AppendLine($"            // ContainerKey: {metadata.ContainerKey}");
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

            sb.AppendLine("            HLSLPROGRAM");

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
                sb.AppendLine($"            #if defined({GetStageMacro(stageGroup.Key)})");

                // Each variant gets its OWN #if defined(VARIANT_KEYWORD) block,
                // even when type info is missing. The variant keyword is built
                // from whichever attributes we have (PermutationId, ShaderHash,
                // ShaderIndex), so the matrix is always disambiguatable. This
                // mirrors Unity's shaderlab where every multi_compile pivot
                // produces a distinct preprocessed variant.
                bool wroteFirst = false;
                foreach (UeShaderLabProgramData program in stagePrograms)
                {
                    string keyword = BuildVariantKeyword(program);
                    sb.AppendLine($"            {(wroteFirst ? "#elif" : "#if")} defined({keyword})");
                    wroteFirst = true;
                    WriteProgramBlock(sb, program);
                    sb.AppendLine();
                }

                if (wroteFirst)
                {
                    sb.AppendLine("            #endif");
                }
                sb.AppendLine("            #endif");
                sb.AppendLine();
            }
            sb.AppendLine("            ENDHLSL");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Pass name surfaces the most identifying shader-type fragment we have.
    // Falls back to a hash so each pass still has a unique label even when
    // type names couldn't be recovered.
    private static string BuildPassName(UeShaderLabProgramData rep)
    {
        if (!string.IsNullOrWhiteSpace(rep.ShaderTypeName)) return rep.ShaderTypeName;
        if (!string.IsNullOrWhiteSpace(rep.PipelineTypeName)) return rep.PipelineTypeName;
        string typePart = string.IsNullOrWhiteSpace(rep.ShaderTypeHash) ? "NOTYPE" : (rep.ShaderTypeHash.Length >= 8 ? rep.ShaderTypeHash[..8] : rep.ShaderTypeHash);
        string vfPart = string.IsNullOrWhiteSpace(rep.VertexFactoryTypeName)
            ? (string.IsNullOrWhiteSpace(rep.VertexFactoryTypeHash) ? string.Empty : (rep.VertexFactoryTypeHash.Length >= 8 ? rep.VertexFactoryTypeHash[..8] : rep.VertexFactoryTypeHash))
            : rep.VertexFactoryTypeName;
        return string.IsNullOrEmpty(vfPart) ? $"Pass_{typePart}" : $"Pass_{typePart}_{vfPart}";
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

    private static string GetStageMacro(string stage) => stage switch
    {
        "Vertex" => "SHADER_STAGE_VERTEX",
        "Fragment" => "SHADER_STAGE_FRAGMENT",
        "Geometry" => "SHADER_STAGE_GEOMETRY",
        "Hull" => "SHADER_STAGE_HULL",
        "Domain" => "SHADER_STAGE_DOMAIN",
        "Compute" => "SHADER_STAGE_COMPUTE",
        _ => $"SHADER_STAGE_{stage.ToUpperInvariant()}"
    };

    private static string BuildPassGroupKey(UeShaderLabProgramData program)
    {
        string pipeline = string.IsNullOrWhiteSpace(program.PipelineTypeHash) ? "NOPIPE" : program.PipelineTypeHash;
        string vf = string.IsNullOrWhiteSpace(program.VertexFactoryTypeHash) ? "NOVF" : program.VertexFactoryTypeHash;
        string type = string.IsNullOrWhiteSpace(program.ShaderTypeHash) ? "NOTYPE" : program.ShaderTypeHash;
        return $"P{pipeline}_V{vf}_S{type}";
    }

    private static void WriteProgramBlock(StringBuilder sb, UeShaderLabProgramData program)
    {
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
    // Identifier-priority precedes hash-priority because Unity-equivalent
    // shaderlab keywords are author-readable: when we know the cooked
    // ShaderType + VertexFactoryType + PermutationId, we encode them in
    // the keyword (e.g. VARIANT_FLumenCardPS_FLocalVertexFactory_PERM_0)
    // and downstream Unity-style tooling can match against them. Only
    // when names are stripped do we fall through to ShaderHash and finally
    // ShaderIndex — both stable but opaque.
    private static string BuildVariantKeyword(UeShaderLabProgramData program)
    {
        bool hasShaderType = !string.IsNullOrWhiteSpace(program.ShaderTypeName);
        bool hasVf = !string.IsNullOrWhiteSpace(program.VertexFactoryTypeName);
        if (hasShaderType || hasVf)
        {
            string typePart = hasShaderType ? SanitizeIdent(program.ShaderTypeName) : "ANYTYPE";
            string vfPart = hasVf ? SanitizeIdent(program.VertexFactoryTypeName) : "NOVF";
            string permPart = program.PermutationId >= 0 ? $"_PERM_{program.PermutationId}" : string.Empty;
            return $"VARIANT_{typePart}_{vfPart}{permPart}";
        }
        if (program.PermutationId >= 0)
        {
            return $"VARIANT_PERM_{program.PermutationId}";
        }
        if (!string.IsNullOrWhiteSpace(program.ShaderHash))
        {
            string shortHash = program.ShaderHash.Length >= 12 ? program.ShaderHash[..12] : program.ShaderHash;
            return $"VARIANT_H_{shortHash}";
        }
        return $"VARIANT_IDX_{program.ShaderIndex:D6}";
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
