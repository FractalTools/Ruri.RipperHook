using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class ShaderLabEmitter
{
    /// <summary>What the decompiler prefixes every binding of the material's own buffer with.</summary>
    private const string MaterialBufferPrefix = "Material_";

    private sealed class ContainerOutputEntry
    {
        public required ShaderPrep Prep { get; init; }
        public required DecompileResult Result { get; init; }
        public required string BasePath { get; init; }
        public required string SourceExtension { get; init; }
    }

    public static void Emit(ShaderSourceState state, ShaderMapInfo map)
        => EmitShaderMap(state, map);

    private static void EmitShaderMap(ShaderSourceState state, ShaderMapInfo map)
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
            System.Threading.Interlocked.Increment(ref state.Skipped);
            return;
        }

        WriteShaderMapOutputs(state, map, outputs);
    }

    private static ContainerOutputEntry? FinalizeForMap(
        ShaderSourceState state,
        ShaderMapInfo map,
        ShaderMapMember member,
        ShaderPrep prep,
        DecompileResult? result)
    {
        if (result == null)
        {
            System.Threading.Interlocked.Increment(ref state.Failed);
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}): batch worker returned no result.");
            return null;
        }

        if (!result.Success)
        {
            System.Threading.Interlocked.Increment(ref state.Failed);
            string firstLine = result.ErrorMessage?.Split('\n', 2)[0]?.Trim() ?? "<no message>";
            state.LogError($"Shader {member.ArchiveShaderIndex} (map {map.PrimaryName}) [reached {result.FailedStage}]: {firstLine}");
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
            result.FinalSymbols.UsedMaterials = new List<string>(map.Assets);
        }

        System.Threading.Interlocked.Increment(ref state.Decompiled);
        return new ContainerOutputEntry
        {
            Prep = prep,
            Result = result,
            BasePath = Path.Combine(state.OutputDirectory, BuildShaderMapStem(map)),
            SourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension,
        };
    }

    private static string BuildShaderMapStem(ShaderMapInfo map)
        => SanitizeFileStem($"{HashPrefix(map.ShaderMapHash)}_{map.PrimaryName}");

    /// <summary>
    /// What a shader map's own folder is called wherever it lands: its hash, then the material it
    /// was first named by. The hash prefix is what says WHICH map a folder holds, so an output
    /// folder states for itself which maps it already carries.
    /// </summary>
    public static string HashPrefix(string shaderMapHash)
        => "SM" + (shaderMapHash.Length >= 12 ? shaderMapHash[..12] : shaderMapHash);

    /// <summary>The map hashes one output folder already holds source for.</summary>
    public static HashSet<string> Written(string outputDirectory)
    {
        HashSet<string> hashes = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outputDirectory))
        {
            return hashes;
        }
        foreach (string file in Directory.EnumerateFiles(outputDirectory, "SM*.shader"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            int underscore = stem.IndexOf('_');
            hashes.Add(underscore > 0 ? stem[..underscore] : stem);
        }
        return hashes;
    }

    private static void WriteShaderMapOutputs(ShaderSourceState state, ShaderMapInfo map, List<ContainerOutputEntry> outputs)
    {
        string containerStem = BuildShaderMapStem(map);
        string containerBasePath = Path.Combine(state.OutputDirectory, containerStem);

        UeShaderLabContainerMetadata metadata = BuildShaderMapMetadata(state, map, outputs);

        HashSet<string> splittableStages = ComputeSplittableStages(metadata.Programs, state.Request.SplitVariantsToHlslFiles);

        Dictionary<UeShaderLabProgramData, string> pooled = new();
        if (splittableStages.Count > 0)
        {
            foreach (UeShaderLabProgramData program in metadata.Programs)
            {
                if (!splittableStages.Contains(program.Stage)) continue;
                string keyword = BuildVariantKeyword(program);
                pooled[program] = state.Variants.Include(keyword, program.SourceFileExtension, WriteVariantHlslFile(metadata, program, keyword));
            }
        }

        OutputFile.Write(containerBasePath + ".shader", WriteContainerShaderFile(metadata, pooled, splittableStages));
    }

    /// <summary>
    /// Which stages are written as their own .hlsl files: the ones holding more than one program.
    /// A stage with a single body reads better where it stands than behind an #include.
    /// </summary>
    private static HashSet<string> ComputeSplittableStages(List<UeShaderLabProgramData> programs, bool splitEnabled)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (!splitEnabled)
        {
            return result;
        }
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (UeShaderLabProgramData program in programs)
        {
            counts[program.Stage] = counts.GetValueOrDefault(program.Stage) + 1;
        }
        foreach (KeyValuePair<string, int> stage in counts)
        {
            if (stage.Value > 1)
            {
                result.Add(stage.Key);
            }
        }
        return result;
    }

    private static string WriteVariantHlslFile(UeShaderLabContainerMetadata metadata, UeShaderLabProgramData program, string keyword)
    {
        StringBuilder sb = new();
        sb.AppendLine("// =============================================================");
        sb.AppendLine($"// Variant: {keyword}");
        sb.AppendLine($"// Stage: {program.Stage}");
        sb.AppendLine($"// ShaderIndex: {program.ShaderIndex}");
        sb.AppendLine($"// PermutationId: {program.PermutationId}");
        if (!string.IsNullOrWhiteSpace(program.ShaderHash)) sb.AppendLine($"// ShaderHash: {program.ShaderHash}");
        if (!string.IsNullOrWhiteSpace(program.ShaderTypeName)) sb.AppendLine($"// ShaderType: {program.ShaderTypeName}");
        if (!string.IsNullOrWhiteSpace(program.VertexFactoryTypeName)) sb.AppendLine($"// VertexFactoryType: {program.VertexFactoryTypeName}");
        if (!string.IsNullOrWhiteSpace(program.PipelineTypeName)) sb.AppendLine($"// PipelineType: {program.PipelineTypeName}");
        sb.AppendLine("// =============================================================");
        sb.AppendLine();

        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            string source = RenameAnonymousGlobals(program.SourceCode!, program.ShaderTypeName, program.ShaderHash, program.SymbolMetadata, metadata.MaterialTextureOrder);
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

    private static UeShaderLabContainerMetadata BuildShaderMapMetadata(ShaderSourceState state, ShaderMapInfo map, List<ContainerOutputEntry> outputs)
    {
        return new UeShaderLabContainerMetadata
        {
            Name = map.PrimaryName,
            ContainerKey = $"SM{(map.ShaderMapHash.Length >= 12 ? map.ShaderMapHash[..12] : map.ShaderMapHash)}",
            MaterialName = map.PrimaryName,
            UsedMaterials = new List<string>(map.Assets),
            PropertiesBlock = map.PropertiesBlock,
            MaterialTextureOrder = new List<string>(map.MaterialTextureOrder),
            MaterialTextureBuckets = new List<int>(map.MaterialTextureBuckets),
            SubShaderTags = map.SubShaderTags,
            PassCommands = map.PassCommands,
            Programs = outputs
                .OrderBy(static o => StageSortKey(StageName(o.Result.Stage)))
                .ThenBy(static o => o.Prep.ShaderIndex)
                .Select(output =>
                {
                    ShaderContainerInfo? perMap = ResolvePerMapContainer(state, map, output.Prep.ShaderIndex);
                    ShaderContainerInfo? container = perMap ?? output.Prep.ContainerInfo;
                    return new UeShaderLabProgramData
                    {
                        Stage = StageName(output.Result.Stage),
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

    private static ShaderContainerInfo? ResolvePerMapContainer(ShaderSourceState state, ShaderMapInfo map, int archiveShaderIndex)
    {
        return map.ContainerByShaderIndex.GetValueOrDefault(archiveShaderIndex);
    }

    private static string WriteContainerShaderFile(UeShaderLabContainerMetadata metadata, IReadOnlyDictionary<UeShaderLabProgramData, string> pooled, HashSet<string> splittableStages)
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
        if (!string.IsNullOrEmpty(metadata.SubShaderTags))
        {
            foreach (string line in metadata.SubShaderTags.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) sb.AppendLine();
                else sb.AppendLine("        " + trimmed);
            }
        }
        if (metadata.Programs.Count > 0)
        {
            List<UeShaderLabProgramData> passPrograms = metadata.Programs
                .OrderBy(static p => StageSortKey(p.Stage))
                .ThenBy(static p => p.ShaderIndex)
                .ToList();
            sb.AppendLine("        Pass {");
            if (!string.IsNullOrWhiteSpace(metadata.ContainerKey)) sb.AppendLine($"            // ContainerKey: {metadata.ContainerKey}");
            if (!string.IsNullOrEmpty(metadata.PassCommands))
            {
                foreach (string line in metadata.PassCommands.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (trimmed.Length == 0) continue;
                    sb.AppendLine("            " + trimmed);
                }
            }
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

            bool anyGlsl = passPrograms.Any(p => string.Equals(p.SourceLanguage, "glsl", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine(anyGlsl ? "            GLSLPROGRAM" : "            HLSLPROGRAM");

            if (!anyGlsl)
            {
                sb.AppendLine("            #pragma target 5.0");
                sb.AppendLine("            #pragma use_dxc");
            }

            foreach (string pragma in passPrograms
                         .Select(p => TryGetStagePragma(p.Stage, out string pr) ? pr : string.Empty)
                         .Where(static p => !string.IsNullOrEmpty(p))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(static p => p, StringComparer.Ordinal))
            {
                sb.AppendLine($"            {pragma} main");
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

                sb.AppendLine($"            // ============================================================");
                sb.AppendLine($"            // Stage: {stageGroup.Key} — {stagePrograms.Count} variant(s)");
                sb.AppendLine($"            // ============================================================");

                string? stageMacro = GetShaderStageMacro(stageGroup.Key);
                if (stageMacro != null)
                {
                    sb.AppendLine($"            #ifdef {stageMacro}");
                }

                EmitStageVariants(sb, stagePrograms, pooled,
                    splitInclude: splittableStages.Contains(stageGroup.Key), metadata.MaterialTextureOrder);

                if (stageMacro != null)
                {
                    sb.AppendLine($"            #endif");
                }
                sb.AppendLine();
            }
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

    /// <summary>Prefix of the keyword that selects one compiled variant of a stage.</summary>
    private const string VariantKeywordPrefix = "RURI_VARIANT_";

    /// <summary>What selects one variant: its own name, which is also the name of its file.</summary>
    private static string VariantSelectKeyword(UeShaderLabProgramData program)
        => VariantKeywordPrefix + BuildVariantKeyword(program);

    /// <summary>
    /// Every program this stage compiled to, each reachable by a keyword of its own.
    ///
    /// A shader map holds one program per permutation the material was compiled for, and they are
    /// ALTERNATIVES -- exactly one is what a given draw runs. When each is written as its own
    /// file, naming only the first left every other file unreferenced: on one character material
    /// that was 69 of 72 written files that nothing in the shader pointed at, which is not a
    /// translation of anything. Each is guarded by its own keyword instead, and the first is what
    /// a reader who names none gets, so the shader still says something on its own.
    ///
    /// The keywords are deliberately NOT declared as a multi_compile set: which vertex program ran
    /// with which pixel program is a pairing the archive does not record, and declaring a set
    /// would state one.
    ///
    /// Variants only exist to be selected between when they were written separately. A stage kept
    /// inline states the one program it was asked for and says so, which is what asking for it
    /// inline means.
    /// </summary>
    private static void EmitStageVariants(StringBuilder sb, List<UeShaderLabProgramData> stagePrograms,
        IReadOnlyDictionary<UeShaderLabProgramData, string> pooled, bool splitInclude, IReadOnlyList<string> materialTextureOrder)
    {
        if (stagePrograms.Count == 1)
        {
            EmitProgramBlock(sb, stagePrograms[0], pooled, splitInclude, materialTextureOrder);
            return;
        }

        if (!splitInclude)
        {
            sb.AppendLine($"            // Note: {stagePrograms.Count - 1} further variant(s) of this stage were not emitted."
                          + " Ask for split variants to get each as its own file.");
            EmitProgramBlock(sb, stagePrograms[0], pooled, splitInclude, materialTextureOrder);
            return;
        }

        sb.AppendLine($"            // Define one of these to pick a variant; none defined compiles the first.");
        for (int i = 0; i < stagePrograms.Count; i++)
        {
            sb.AppendLine($"            #{(i == 0 ? "if" : "elif")} defined({VariantSelectKeyword(stagePrograms[i])})");
            EmitProgramBlock(sb, stagePrograms[i], pooled, splitInclude, materialTextureOrder);
        }
        sb.AppendLine("            #else");
        EmitProgramBlock(sb, stagePrograms[0], pooled, splitInclude, materialTextureOrder);
        sb.AppendLine("            #endif");
    }

    private static void EmitProgramBlock(StringBuilder sb, UeShaderLabProgramData program, IReadOnlyDictionary<UeShaderLabProgramData, string> pooled, bool splitInclude, IReadOnlyList<string> materialTextureOrder)
    {
        if (splitInclude)
        {
            sb.AppendLine($"            #include \"{pooled[program]}\"");
            return;
        }

        sb.AppendLine($"            // Stage: {program.Stage}");
        sb.AppendLine($"            // ShaderIndex: {program.ShaderIndex}");
        sb.AppendLine($"            // ResourceIndex: {program.ResourceIndex}");
        sb.AppendLine($"            // PermutationId: {program.PermutationId}");
        if (!string.IsNullOrWhiteSpace(program.ShaderHash)) sb.AppendLine($"            // ShaderHash: {program.ShaderHash}");

        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            string renamed = RenameAnonymousGlobals(program.SourceCode!, program.ShaderTypeName, program.ShaderHash, program.SymbolMetadata, materialTextureOrder);
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

    private static string BuildVariantKeyword(UeShaderLabProgramData program)
    {
        StringBuilder sb = new();
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

    private static string CompressTemplateIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int lt = raw.IndexOf('<');
        if (lt < 0) return SanitizeIdent(raw);
        string head = raw.Substring(0, lt);
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


    /// <summary>
    /// A material texture's name as the emitted source spells it. The order list is read back off
    /// the symbols the decompiler HANDED BACK, which already carry the buffer they belong to, so
    /// the prefix is put on only when it is not there yet -- doubling it made every already-named
    /// slot disagree with itself and the ordering was abandoned every single time.
    /// </summary>
    private static void ApplyMaterialTextureOrder(
        string source,
        List<(string Ident, string HlslType, string UbmtKind, string SlotPrefix, string SlotIdx)> anons,
        Dictionary<int, string> rename,
        HashSet<int> claimed,
        IReadOnlyList<string> materialTextureOrder)
    {
        if (materialTextureOrder.Count == 0) return;

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

        // A slot is the material's when it is still anonymous or already carries the
        // material buffer's own prefix. Naming every OTHER uniform buffer instead was a
        // list that could only ever be incomplete: one unlisted buffer put an extra slot
        // in the count, the count stopped matching the material's own texture list, and
        // the whole ordering was abandoned for that shader.
        var materialSlots = declarations
            .Where(d => d.IsAnon || d.Name.StartsWith(MaterialBufferPrefix, StringComparison.Ordinal))
            .ToList();
        if (materialSlots.Count == 0) return;

        IReadOnlyList<string> order = materialTextureOrder;
        if (order.Count < materialSlots.Count)
        {
            Console.Error.WriteLine($"[ShaderLab] material-texture order: 槽 {materialSlots.Count} 个 > 材质贴图表 {order.Count} 项 — 放弃按序命名(宁可无名)。");
            return;
        }

        for (int i = 0; i < materialSlots.Count; i++)
        {
            var slot = materialSlots[i];
            if (slot.IsAnon) continue;
            string expected = MaterialSlotName(order[i]);
            if (!string.Equals(slot.Name, expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"[ShaderLab] material-texture order: 锚点不符(t{slot.Slot} 已具名 '{slot.Name}',按序应为 '{expected}')" +
                    " — 声明序假设不成立,放弃按序命名。");
                return;
            }
        }

        for (int i = 0; i < materialSlots.Count; i++)
        {
            var slot = materialSlots[i];
            if (!slot.IsAnon || claimed.Contains(slot.AnonIndex)) continue;
            rename[slot.AnonIndex] = MaterialSlotName(order[i]);
            claimed.Add(slot.AnonIndex);
        }
    }

    private static string MaterialSlotName(string stated)
    {
        string identifier = SanitizeIdent(stated);
        return identifier.StartsWith(MaterialBufferPrefix, StringComparison.Ordinal)
            ? identifier
            : MaterialBufferPrefix + identifier;
    }

    private static string RenameAnonymousGlobals(string source, string shaderTypeName, string shaderHash, SerializedProgramData? symbolMetadata, IReadOnlyList<string> materialTextureOrder)
    {
        if (string.IsNullOrWhiteSpace(source)) return source;
        string discriminator = string.IsNullOrWhiteSpace(shaderTypeName)
            ? string.Empty
            : SanitizeIdent(shaderTypeName);

        string result = source;

        if (!string.IsNullOrEmpty(discriminator) && result.Contains("_Globals_m0", StringComparison.Ordinal))
        {
            result = result.Replace("_Globals_m0", $"_loose_{discriminator}", StringComparison.Ordinal);
        }

        if (result.Contains(" : register(", StringComparison.Ordinal))
        {
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

            HashSet<string> shaderUsedUbs = new(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                result,
                @"^cbuffer\s+type_([A-Za-z_][A-Za-z0-9_]*)\s*:",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                shaderUsedUbs.Add(m.Groups[1].Value.ToLowerInvariant());
            }

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
                        Console.Error.WriteLine($"[ShaderLab][rename] type=({grp.Key.Item1}|{grp.Key.Item2}) run=t{firstSlot}..t{lastSlot} count={run.Count} usedUbs=[{usedUbsCsv}] -> ownerUb={ownerUb} ordered={(ordered == null ? "<null>" : string.Join(",", ordered))}");
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

            ApplyUsagePatternMatches(result, anons, rename, claimedByOrdered);

            ApplyMaterialTextureOrder(result, anons, rename, claimedByOrdered, materialTextureOrder);


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
                rename[i] = string.IsNullOrEmpty(discriminator) ? a.Ident : $"{discriminator}_{suffix}";
            }

            Dictionary<string, string> identToFinal = new(StringComparer.Ordinal);
            for (int i = 0; i < anons.Count; i++)
            {
                if (!identToFinal.ContainsKey(anons[i].Ident))
                {
                    identToFinal[anons[i].Ident] = rename[i];
                }
            }
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
                    $"[ShaderLab] name collision: '{kv.Key}' claimed by {kv.Value.Count} bindings " +
                    $"({string.Join(", ", kv.Value)}) — reverting them to anonymous identifiers " +
                    "(a wrong symbol is worse than none).");
                foreach (string ident in kv.Value) identToFinal[ident] = ident;
            }

            if (Environment.GetEnvironmentVariable("RURI_UB_DEBUG") == "1")
            {
                foreach (KeyValuePair<string, string> kv in identToFinal)
                {
                    Console.Error.WriteLine($"[ShaderLab][applyRename] '{kv.Key}' -> '{kv.Value}'");
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

    private static void ApplyUsagePatternMatches(
        string hlsl,
        List<(string Ident, string HlslType, string UbmtKind, string SlotPrefix, string SlotIdx)> anons,
        Dictionary<int, string> rename,
        HashSet<int> claimed)
    {
        Dictionary<string, string> sampleCallByIdent = new(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            hlsl,
            @"([TU]\d+|_\d+)\.Sample(?:Level|Bias|Grad|Cmp|CmpLevelZero)?\([^;]+;"))
        {
            string id = m.Groups[1].Value;
            if (!sampleCallByIdent.ContainsKey(id))
                sampleCallByIdent[id] = m.Value;
        }

        bool decalsGate = hlsl.Contains("View_ShowDecalsMask", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(
                hlsl,
                @"View_PrimitiveSceneData\.Load<uint>\([^)]+\)\s*&\s*8u");
        if (decalsGate)
        {
            List<(int AnonIdx, int Slot)> dbufCandidates = new();
            for (int i = 0; i < anons.Count; i++)
            {
                if (claimed.Contains(i)) continue;
                var a = anons[i];
                if (a.SlotPrefix != "t") continue;
                if (!a.HlslType.StartsWith("Texture2D", StringComparison.Ordinal)) continue;
                if (!sampleCallByIdent.TryGetValue(a.Ident, out string? callSite)) continue;
                if (!IsSampleUvFromScreenSpace(hlsl, callSite)) continue;
                if (int.TryParse(a.SlotIdx, out int slot))
                    dbufCandidates.Add((i, slot));
            }
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
                    break;                }
            }
        }

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
            System.Text.RegularExpressions.MatchCollection layerMaskMatches =
                System.Text.RegularExpressions.Regex.Matches(
                    window,
                    @"Material_LayerMask_[A-Za-z0-9_]+");
            if (layerMaskMatches.Count < 2) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                window,
                @"dot\([^;]*" + System.Text.RegularExpressions.Regex.Escape(local) + @"[^;]*Material_LayerMask")) continue;
            rename[i] = "Landscape_WeightmapTexture";
            claimed.Add(i);
        }

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
            bool zwBias = System.Text.RegularExpressions.Regex.IsMatch(
                window, @"mad\(" + System.Text.RegularExpressions.Regex.Escape(local) + @"\.z,\s*2\.0f,\s*-1\.0f\)")
                && System.Text.RegularExpressions.Regex.IsMatch(
                window, @"mad\(" + System.Text.RegularExpressions.Regex.Escape(local) + @"\.w,\s*2\.0f,\s*-1\.0f\)");
            if (!zwBias) continue;
            rename[i] = "LandscapeParameters_NormalmapTexture";
            claimed.Add(i);
        }
    }

    private static bool IsSampleUvFromScreenSpace(string hlsl, string sampleCall)
    {
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
        return matchedUvComponents >= 2;    }

    private static string ClassifyUbmtFromHlslType(string hlslType, string slotPrefix)
    {
        if (string.IsNullOrEmpty(hlslType)) return string.Empty;

        int lt = hlslType.IndexOf('<');
        string head = lt < 0 ? hlslType : hlslType.Substring(0, lt);

        if (head.StartsWith("RW", StringComparison.Ordinal)) return "UBMT_UAV";

        if (head == "SamplerState" || head == "SamplerComparisonState") return "UBMT_SAMPLER";

        if (head.StartsWith("Texture", StringComparison.Ordinal))
        {
            return slotPrefix == "u" ? "UBMT_UAV" : "UBMT_TEXTURE";
        }

        if (head == "ByteAddressBuffer"
            || head == "Buffer"
            || head == "StructuredBuffer"
            || head == "AppendStructuredBuffer"
            || head == "ConsumeStructuredBuffer")
        {
            return slotPrefix == "u" ? "UBMT_UAV" : "UBMT_SRV";
        }

        if (head == "RaytracingAccelerationStructure") return "UBMT_SRV";

        return string.Empty;
    }

    private static string SanitizeIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
        }
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
        "Amplification" => 1,
        "Mesh" => 2,
        "Hull" => 3,
        "Domain" => 4,
        "Geometry" => 5,
        "Fragment" => 6,
        "Compute" => 7,
        "RayGeneration" => 8,
        "Intersection" => 9,
        "AnyHit" => 10,
        "ClosestHit" => 11,
        "Miss" => 12,
        "Callable" => 13,
        _ => 100,
    };

    /// <summary>
    /// What to call the stage a shader runs at, as the BINARY declared it.
    ///
    /// Never from the container's frequency byte: that byte is numbered by the engine
    /// BUILD, and a fork that inserts a frequency shifts every later one. One shipped
    /// title numbers pixel 5 and geometry 6 where stock 4.26 numbers them 3 and 4, so
    /// every pixel shader it ships was written out as a compute shader and every map
    /// read as having no pixel shader at all -- silently, because a mislabelled file
    /// still decompiles.
    /// </summary>
    private static string StageName(PipelineStage stage) => stage switch
    {
        PipelineStage.Vertex => "Vertex",
        PipelineStage.TessControl => "Hull",
        PipelineStage.TessEvaluation => "Domain",
        PipelineStage.Geometry => "Geometry",
        PipelineStage.Fragment => "Fragment",
        PipelineStage.Compute => "Compute",
        PipelineStage.RayGeneration => "RayGeneration",
        PipelineStage.Intersection => "Intersection",
        PipelineStage.AnyHit => "AnyHit",
        PipelineStage.ClosestHit => "ClosestHit",
        PipelineStage.Miss => "Miss",
        PipelineStage.Callable => "Callable",
        PipelineStage.Task => "Amplification",
        PipelineStage.Mesh => "Mesh",
        _ => "Unknown",
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

        public List<string> MaterialTextureOrder { get; set; } = new();

        public List<int> MaterialTextureBuckets { get; set; } = new();




        public List<UeShaderLabProgramData> Programs { get; set; } = new();
        public string PropertiesBlock { get; set; } = string.Empty;
        public string SubShaderTags { get; set; } = string.Empty;
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
