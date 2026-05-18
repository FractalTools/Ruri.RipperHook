using System.Buffers;
using System.Text;
using System.Threading;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Shaders.Extensions;
using AssetRipper.Export.Modules.Shaders.ShaderBlob;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Shaders;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader.GpuProgramType;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedPlayerSubProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgramParameters;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderRTBlendState;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderState;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubProgram;
using Ruri.RipperHook;
using Ruri.SourceGenerated.NativeEnums.Global;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.AR;

public sealed class ShaderRuriDecompileExporter : ShaderExporterBase
{
    /// <summary>
    /// Optional callback installed by game-specific hooks (e.g. EndField) to rewrite the
    /// <see cref="SerializedProgramData"/> for one pass after the standard reader has filled it in.
    /// Use this to apply proprietary register-decoding, deduplicate duplicate-name bindings,
    /// reorder lookups, etc. The generic exporter must stay free of game-specific branches.
    /// </summary>
    public static Action<SerializedProgramData, ShaderSubProgram, ShaderReadContext>? PostProcessSymbols;

    /// <summary>
    /// Live-read from the persisted ShaderDecompilerSettings snapshot
    /// (loaded at host startup, mutated by the host's settings UI).
    /// When true, multi-variant stages get their HLSL bodies emitted to
    /// sibling `<shaderStem>/<variantKey>.hlsl` files and the `.shader`
    /// references them via `#include`. When false (default), variants
    /// stay inline inside the `.shader` file under their
    /// `#if defined(KEYWORD)` blocks. Single-variant stages always inline.
    /// </summary>
    public static bool SplitVariantsToHlslFiles
    {
        get => ShaderDecompilerSettingsAccess.Current.SplitVariantsToHlslFiles;
        set
        {
            var current = ShaderDecompilerSettingsAccess.Current;
            if (current.SplitVariantsToHlslFiles == value) return;
            ShaderDecompilerSettingsAccess.Replace(new ShaderDecompilerSettings
            {
                SplitVariantsToHlslFiles = value,
                WarnIfNoMappings = current.WarnIfNoMappings,
                TryMatchBaseEngineVersion = current.TryMatchBaseEngineVersion,
            });
        }
    }

    /// <summary>
    /// Platform priority for the auto-pick. In an ideal world Vulkan comes first because the
    /// downstream <see cref="ShaderDecompiler"/> already speaks SPIR-V natively — but Unity's
    /// Vulkan bytecode is wrapped with SMOL-V (Aras P.'s compressed-SPIR-V format) and we don't
    /// yet ship a SMOL-V decoder. Until that lands, prefer D3D11 so we go through the working
    /// DXBC→DXIL→SPV path. Other platforms (Metal MSL, GLSL, console) aren't ingestible by
    /// spirv-cross today, so we skip them entirely.
    ///
    /// TODO: when a SMOL-V decoder is added (port of github.com/aras-p/smol-v), swap order so
    /// Vulkan wins for vanilla Unity shaders that use raw SPIR-V. EndField specifically wraps
    /// Vulkan blobs in SMOL-V at offset 0xB0 (header field at 0x0C tells the strip size), so
    /// EndField would still need that strip + decompress before raw SPIR-V can flow through.
    /// </summary>
    private static readonly GPUPlatform[] PreferredPlatforms = new[]
    {
        GPUPlatform.D3D11,
        GPUPlatform.Vulkan,
    };

    public override bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
    {
        IShader shader = (IShader)asset;
        GPUPlatform platform = PickBestPlatform(shader);
        if (platform == GPUPlatform.Unknown)
        {
            return false;
        }

        return DecompileShader(shader, platform, path);
    }

    /// <summary>
    /// Pick the highest-quality platform actually present in the shader, walking
    /// <see cref="PreferredPlatforms"/> in declared order. EndField (and any modern Unity
    /// project) ships Vulkan pre-built, so we get SPIR-V straight from the bundle and skip the
    /// lossy DXBC translation entirely. Returns <see cref="GPUPlatform.Unknown"/> when no
    /// supported platform is present (e.g. Metal-only mobile shader).
    /// </summary>
    private static GPUPlatform PickBestPlatform(IShader shader)
    {
        if (shader.Platforms is null || shader.Platforms.Count == 0)
        {
            return GPUPlatform.Unknown;
        }

        HashSet<GPUPlatform> available = new();
        foreach (var p in shader.Platforms)
        {
            available.Add((GPUPlatform)(int)p);
        }
        foreach (GPUPlatform candidate in PreferredPlatforms)
        {
            if (available.Contains(candidate))
            {
                return candidate;
            }
        }
        return GPUPlatform.Unknown;
    }

    private static bool DecompileShader(IShader shader, GPUPlatform platform, string outputPath)
    {
        if (shader.ParsedForm is null)
        {
            return false;
        }

        ShaderSubProgramBlob[] blobs = shader.ReadBlobs();
        if (blobs.Length == 0)
        {
            return false;
        }

        List<ShaderReadPass> reads = ReadPasses(shader, blobs, platform);
        if (reads.Count == 0)
        {
            return false;
        }

        List<ShaderSymbolPass> symbols = BuildSymbols(reads);
        if (symbols.Count == 0)
        {
            return false;
        }

        UnityShaderMetadata unityMetadata = UnityShaderMetadataBuilder.Build(shader, platform, EnumerateProgramBlobIndices);
        DecompileAndWritePasses(shader, symbols, unityMetadata, outputPath);
        return true;
    }

    private static List<ShaderReadPass> ReadPasses(IShader shader, ShaderSubProgramBlob[] blobs, GPUPlatform platform)
    {
        List<int> platformValues = shader.Platforms?.Select(p => (int)p).ToList() ?? [];
        int selectedPlatformIndex = platformValues.FindIndex(p => p == (int)platform);
        if (selectedPlatformIndex < 0 || selectedPlatformIndex >= blobs.Length)
        {
            return [];
        }

        ShaderSubProgramBlob blob = blobs[selectedPlatformIndex];
        List<ShaderReadPass> result = [];
        for (int subShaderIndex = 0; subShaderIndex < shader.ParsedForm!.SubShaders.Count; subShaderIndex++)
        {
            var subShader = shader.ParsedForm.SubShaders[subShaderIndex];
            for (int passIndex = 0; passIndex < subShader.Passes.Count; passIndex++)
            {
                var pass = subShader.Passes[passIndex];
                Dictionary<int, string> nameTable = BuildNameTable(pass.NameIndices);
                ReadProgram(shader, blob, pass, pass.ProgVertex, platform, subShaderIndex, passIndex, "Vertex", nameTable, result);
                ReadProgram(shader, blob, pass, pass.ProgFragment, platform, subShaderIndex, passIndex, "Fragment", nameTable, result);
                ReadProgram(shader, blob, pass, pass.ProgGeometry, platform, subShaderIndex, passIndex, "Geometry", nameTable, result);
                ReadProgram(shader, blob, pass, pass.ProgHull, platform, subShaderIndex, passIndex, "Hull", nameTable, result);
                ReadProgram(shader, blob, pass, pass.ProgDomain, platform, subShaderIndex, passIndex, "Domain", nameTable, result);
                ReadProgram(shader, blob, pass, pass.ProgRayTracing, platform, subShaderIndex, passIndex, "RayTracing", nameTable, result);
            }
        }

        return result;
    }

    private static void ReadProgram(
        IShader shader,
        ShaderSubProgramBlob blob,
        ISerializedPass pass,
        ISerializedProgram? program,
        GPUPlatform platform,
        int subShaderIndex,
        int passIndex,
        string stage,
        Dictionary<int, string> nameTable,
        List<ShaderReadPass> result)
    {
        if (program is null)
        {
            return;
        }

        LogProgramEnumeration(shader.Name, stage, program, shader.Collection.Version);

        foreach (ShaderReadSource source in EnumerateProgramSources(program, shader.Collection.Version, platform))
        {
            ShaderSubProgram subProgram = source.ParameterBlobIndex is uint paramBlobIndex
                ? blob.GetSubProgram(source.BlobIndex, paramBlobIndex)
                : blob.GetSubProgram(source.BlobIndex);

            if (subProgram.ProgramData.Length == 0)
            {
                continue;
            }

            byte[] payload = ExtractPayload(subProgram.ProgramData, shader.Collection.Version);
            if (payload.Length == 0)
            {
                continue;
            }

            result.Add(new ShaderReadPass(
                pass.State.Name_R,
                subShaderIndex,
                passIndex,
                stage,
                source.BlobIndex,
                source.ParameterBlobIndex,
                source.KeywordIndices,
                subProgram,
                ReadProgramSymbols(program.CommonParameters, nameTable),
                ReadProgramSymbols(source.Parameters, nameTable),
                payload,
                shader.Name,
                shader.Collection.Version));
        }
    }

    private static IEnumerable<ShaderReadSource> EnumerateProgramSources(ISerializedProgram program, UnityVersion version, GPUPlatform platform)
    {
        Dictionary<uint, ISerializedSubProgram> subProgramsByBlob = new();
        foreach (ISerializedSubProgram subProgram in program.SubPrograms)
        {
            subProgramsByBlob[subProgram.BlobIndex] = subProgram;
        }

        HashSet<(uint BlobIndex, uint? ParameterBlobIndex, string KeywordIdentity)> emitted = new();

        // PlayerSubPrograms outer dimension is NOT per-platform in EndField (contrary to vanilla
        // Unity layout). A pass typically has 4 outer groups with all real entries packed into
        // the last group, mixing GpuProgramType across platforms (e.g. DX11VertexSM40 + SPIRV in
        // one group). So we walk every group and filter per-entry by GpuProgramType→platform.
        // ParameterBlobIndices is parallel to PlayerSubPrograms (per outer group, then per entry).
        if (program.Has_PlayerSubPrograms() && program.Has_ParameterBlobIndices()
            && program.PlayerSubPrograms is not null && program.ParameterBlobIndices is not null)
        {
            for (int groupIndex = 0; groupIndex < program.PlayerSubPrograms.Count; groupIndex++)
            {
                AssetList<SerializedPlayerSubProgram> group = program.PlayerSubPrograms[groupIndex];
                AssetList<uint>? paramGroup = groupIndex < program.ParameterBlobIndices.Count
                    ? program.ParameterBlobIndices[groupIndex]
                    : null;
                for (int i = 0; i < group.Count; i++)
                {
                    SerializedPlayerSubProgram playerSubProgram = group[i];
                    if (!MatchesPlatform(version, playerSubProgram.GpuProgramType, platform))
                    {
                        continue;
                    }
                    uint? parameterBlobIndex = paramGroup is not null && i < paramGroup.Count ? paramGroup[i] : null;
                    var emissionKey = CreateEmissionKey(playerSubProgram.BlobIndex, parameterBlobIndex, playerSubProgram.KeywordIndices);
                    if (emitted.Contains(emissionKey))
                    {
                        continue;
                    }

                    subProgramsByBlob.TryGetValue(playerSubProgram.BlobIndex, out ISerializedSubProgram? sourceSubProgram);
                    emitted.Add(emissionKey);
                    yield return new ShaderReadSource(
                        playerSubProgram.BlobIndex,
                        parameterBlobIndex,
                        playerSubProgram.KeywordIndices?.ToList() ?? [],
                        sourceSubProgram?.Has_Parameters() == true ? sourceSubProgram.Parameters : null);
                }
            }
        }

        // Older shader formats only populate the flat SubPrograms list; modern ones sometimes
        // carry SPIRV entries here too. Dedupe by blob + parameter blob + keyword set so we
        // don't collapse distinct variants that share the same bytecode blob.
        foreach (ISerializedSubProgram subProgram in program.SubPrograms)
        {
            var emissionKey = CreateEmissionKey(subProgram.BlobIndex, null, subProgram.KeywordIndices);
            if (emitted.Contains(emissionKey))
            {
                continue;
            }
            if (!MatchesPlatform(version, (sbyte)subProgram.GpuProgramType, platform))
            {
                continue;
            }

            emitted.Add(emissionKey);
            yield return new ShaderReadSource(
                subProgram.BlobIndex,
                null,
                subProgram.KeywordIndices?.ToList() ?? [],
                subProgram.Has_Parameters() ? subProgram.Parameters : null);
        }
    }

    private static SerializedProgramData ReadProgramSymbols(ISerializedProgramParameters? parameters, Dictionary<int, string> nameTable)
    {
        SerializedProgramData data = new();
        if (parameters is null)
        {
            return data;
        }

        Func<int, string> resolveName = nameIndex => nameTable.TryGetValue(nameIndex, out string? name) ? name : $"name_{nameIndex}";

        foreach (var cbuffer in parameters.ConstantBuffers)
        {
            ConstantBufferParameter buffer = new()
            {
                Name = resolveName(cbuffer.NameIndex),
                NameIndex = cbuffer.NameIndex,
                Size = cbuffer.Size,
                IsPartialCB = cbuffer.Has_IsPartialCB() && cbuffer.IsPartialCB,
                MatrixParameters = cbuffer.MatrixParams.Select(matrix => new MatrixParameter
                {
                    Name = resolveName(matrix.NameIndex),
                    NameIndex = matrix.NameIndex,
                    Index = matrix.Index,
                    ArraySize = matrix.ArraySize,
                    Type = (Ruri.ShaderTools.ShaderParamType)(int)(sbyte)matrix.Type,
                    RowCount = unchecked((byte)matrix.RowCount),
                    ColumnCount = 4,
                    IsMatrix = true,
                }).ToArray(),
                VectorParameters = cbuffer.VectorParams.Select(vector => new VectorParameter
                {
                    Name = resolveName(vector.NameIndex),
                    NameIndex = vector.NameIndex,
                    Index = vector.Index,
                    ArraySize = vector.ArraySize,
                    Type = (Ruri.ShaderTools.ShaderParamType)(int)(sbyte)vector.Type,
                    RowCount = unchecked((byte)vector.Dim),
                    ColumnCount = 1,
                    IsMatrix = false,
                }).ToArray(),
                StructParameters = cbuffer.StructParams.Select(structParam => new StructParameter
                {
                    Name = resolveName(structParam.NameIndex),
                    NameIndex = structParam.NameIndex,
                    Index = structParam.Index,
                    ArraySize = structParam.ArraySize,
                    StructSize = structParam.StructSize,
                    MatrixMembers = structParam.MatrixMembers.Select(matrix => new MatrixParameter
                    {
                        Name = $"{resolveName(structParam.NameIndex)}.{resolveName(matrix.NameIndex)}",
                        NameIndex = matrix.NameIndex,
                        Index = matrix.Index,
                        ArraySize = matrix.ArraySize,
                        Type = (Ruri.ShaderTools.ShaderParamType)(int)(sbyte)matrix.Type,
                        RowCount = unchecked((byte)matrix.RowCount),
                        ColumnCount = 4,
                        IsMatrix = true,
                    }).ToArray(),
                    VectorMembers = structParam.VectorMembers.Select(vector => new VectorParameter
                    {
                        Name = $"{resolveName(structParam.NameIndex)}.{resolveName(vector.NameIndex)}",
                        NameIndex = vector.NameIndex,
                        Index = vector.Index,
                        ArraySize = vector.ArraySize,
                        Type = (Ruri.ShaderTools.ShaderParamType)(int)(sbyte)vector.Type,
                        RowCount = unchecked((byte)vector.Dim),
                        ColumnCount = 1,
                        IsMatrix = false,
                    }).ToArray(),
                }).ToArray(),
            };
            data.ConstantBufferParameters.Add(buffer);
        }

        foreach (var binding in parameters.ConstantBufferBindings)
        {
            data.BufferBindingParameters.Add(new BufferBindingParameter
            {
                Name = resolveName(binding.NameIndex),
                NameIndex = binding.NameIndex,
                Index = binding.Index,
                ArraySize = binding.Has_ArraySize() ? binding.ArraySize : 0,
            });
        }

        foreach (var texture in parameters.TextureParams)
        {
            data.TextureParameters.Add(new TextureParameter
            {
                Name = resolveName(texture.NameIndex),
                NameIndex = texture.NameIndex,
                Index = texture.Index,
                SamplerIndex = texture.SamplerIndex,
                MultiSampled = texture.Has_MultiSampled() && texture.MultiSampled,
                Dim = unchecked((byte)(sbyte)texture.Dim),
            });
        }

        foreach (var sampler in parameters.Samplers)
        {
            data.SamplerParameters.Add(new SamplerParameter
            {
                Sampler = sampler.Sampler,
                BindPoint = sampler.BindPoint,
            });
        }

        foreach (var uav in parameters.UAVParams)
        {
            data.UAVParameters.Add(new UAVParameter
            {
                Name = resolveName(uav.NameIndex),
                NameIndex = uav.NameIndex,
                Index = uav.Index,
                OriginalIndex = uav.OriginalIndex,
            });
        }

        // Program-level numeric / buffer params (m_VectorParams / m_MatrixParams /
        // m_BufferParams). Modern Unity packs almost everything into CBs so these
        // are usually empty; the older flat-uniform path still uses them.
        foreach (var vector in parameters.VectorParams)
        {
            data.VectorParameters.Add(new VectorParameter
            {
                Name = resolveName(vector.NameIndex),
                NameIndex = vector.NameIndex,
                Index = vector.Index,
                ArraySize = vector.ArraySize,
                Type = (Ruri.ShaderTools.ShaderParamType)(int)(sbyte)vector.Type,
                RowCount = unchecked((byte)vector.Dim),
                ColumnCount = 1,
                IsMatrix = false,
            });
        }

        foreach (var matrix in parameters.MatrixParams)
        {
            data.MatrixParameters.Add(new MatrixParameter
            {
                Name = resolveName(matrix.NameIndex),
                NameIndex = matrix.NameIndex,
                Index = matrix.Index,
                ArraySize = matrix.ArraySize,
                Type = (Ruri.ShaderTools.ShaderParamType)(int)(sbyte)matrix.Type,
                RowCount = unchecked((byte)matrix.RowCount),
                ColumnCount = 4,
                IsMatrix = true,
            });
        }

        foreach (var buffer in parameters.BufferParams)
        {
            data.BufferParameters.Add(new BufferBindingParameter
            {
                Name = resolveName(buffer.NameIndex),
                NameIndex = buffer.NameIndex,
                Index = buffer.Index,
                ArraySize = buffer.Has_ArraySize() ? buffer.ArraySize : 0,
            });
        }

        return data;
    }

    private static List<ShaderSymbolPass> BuildSymbols(List<ShaderReadPass> reads)
    {
        List<ShaderSymbolPass> result = [];
        var postProcess = PostProcessSymbols;
        foreach (ShaderReadPass read in reads)
        {
            SerializedProgramData symbols = new()
            {
                EntryPoint = "main",
                DebugName = $"{read.ShaderName}/SubShader{read.SubShaderIndex}/Pass{read.PassIndex}/{read.Stage}/{read.SubProgram.GetProgramType(read.Version)}/{read.BlobIndex}",
            };

            AppendSymbols(symbols, read.CommonSymbols);
            AppendSymbols(symbols, read.ParameterSymbols);
            AppendRuntimeSymbols(symbols, read.SubProgram);

            // Game-specific symbol rewrite (no game branches in the generic path). EndField uses
            // this to apply its packed-binding decode + size-perfect-match for CB→register
            // resolution. Vanilla Unity shaders don't install a callback so the call is a no-op.
            postProcess?.Invoke(symbols, read.SubProgram, new ShaderReadContext(read.ShaderName, read.SubShaderIndex, read.PassIndex, read.BlobIndex, read.Version));

            result.Add(new ShaderSymbolPass(read, symbols));
        }

        return result;
    }

    /// <summary>
    /// Decompile every pass through the engine's batch API (CPU-aware parallel pool) and
    /// stream the results to disk. Successful passes leave only the merged .shader text
    /// behind; failures land in `<output>.failures/<passStem>/` with the engine's full
    /// debug dump (input.bin + intermediate SPV + error.txt + metadata) so they can be
    /// re-run via `Ruri.ShaderDecompiler.exe --batch <failures-root>`. Pass order is
    /// preserved in the .shader file because consumers cross-reference passes by name
    /// and reruns must be textually deterministic.
    /// </summary>
    private static void DecompileAndWritePasses(IShader shader, List<ShaderSymbolPass> symbols, UnityShaderMetadata unityMetadata, string outputPath)
    {
        // Failure dumps land beside the .shader output under
        // `<output>.failures/<passStem>/`, populated by the engine's WriteFailureDump
        // (input.bin, intermediate SPVs, error.txt, metadata.json). On success we
        // intentionally write nothing per-pass — the merged .shader file is the only
        // artifact, keeping the export tree clean.
        string failuresRoot = outputPath + ".failures";
        int total = symbols.Count;
        var passStems = new string[total];
        var requests = new (byte[] Binary, DecompileOptions Options)[total];

        for (int i = 0; i < total; i++)
        {
            ShaderSymbolPass pass = symbols[i];
            string passStem = $"sub{pass.Read.SubShaderIndex}.pass{pass.Read.PassIndex}.{pass.Read.Stage.ToLowerInvariant()}.blob{pass.Read.BlobIndex}.{SanitizeFileName(pass.Read.PassName)}";
            passStems[i] = passStem;

            requests[i] = (pass.Read.Binary, new DecompileOptions
            {
                Format = ShaderArchitecture.Unknown,
                Metadata = pass.Symbols,
                UnityMetadata = unityMetadata,
                ShaderModel = 51,
                DebugDumpDirectory = Path.Combine(failuresRoot, passStem),
                DebugDumpStem = "with-symbols",
            });
        }

        // 2. Run the batch. The engine handles concurrency / CPU throttling internally —
        // we just hand it the request list and let it parallelise. The progress callback
        // fires on a worker thread per completion; we use it for ordered "[k/total]"
        // logging via an atomic counter.
        int completed = 0;
        using ShaderDecompiler decompiler = new(AppDomain.CurrentDomain.BaseDirectory);
        DecompileResult[] results = decompiler.Decompile(requests, (idx, r) =>
        {
            int now = Interlocked.Increment(ref completed);
            string suffix = r.Success ? string.Empty : $" — fail: {FirstLine(r.ErrorMessage)}";
            Console.WriteLine($"[ShaderDecompile] {shader.Name} [{now}/{total}] {passStems[idx]}{suffix}");
        });

        // 3. Compose the final .shader text and write in one shot. Pass order matters —
        // consumers cross-reference passes by name and reruns must be textually
        // deterministic even when the worker pool finishes blobs out of order.
        int succeeded = 0;
        for (int i = 0; i < total; i++)
        {
            if (results[i]?.Success == true) succeeded++;
        }
        UnityShaderMetadataBuilder.BackfillProgramSources(
            unityMetadata,
            symbols.Select(static s => new UnityShaderMetadataBuilder.ProgramResultLocation(s.Read.SubShaderIndex, s.Read.PassIndex, s.Read.Stage, s.Read.BlobIndex, s.Read.ParameterBlobIndex, s.Read.KeywordIndices)).ToArray(),
            results);

        // Variant emission strategy is controlled by SplitVariantsToHlslFiles:
        //   - true:  multi-variant stages spool to sibling
        //            `<shaderStem>/<variantKey>.hlsl` files; .shader uses
        //            `#include` lines per `#if defined(KEYWORD)` branch.
        //   - false: every variant body stays inline inside the .shader file
        //            (legacy single-file layout).
        // Either way, single-variant stages skip the `#if` chain entirely.
        if (SplitVariantsToHlslFiles)
        {
            string variantFolderStem = Path.GetFileNameWithoutExtension(outputPath);
            UnityShaderLabResult result = UnityShaderLabWriter.WriteSplit(unityMetadata, variantFolderStem);
            File.WriteAllText(outputPath, result.ShaderText);

            if (result.VariantFiles.Count > 0)
            {
                string outputDir = Path.GetDirectoryName(outputPath) ?? string.Empty;
                string variantDir = Path.Combine(outputDir, variantFolderStem);
                Directory.CreateDirectory(variantDir);
                foreach (var (filename, body) in result.VariantFiles)
                {
                    File.WriteAllText(Path.Combine(variantDir, filename), body);
                }
            }

            Console.WriteLine($"[ShaderDecompile] {shader.Name} done ({succeeded}/{total} passes, {result.VariantFiles.Count} variant files)");
        }
        else
        {
            File.WriteAllText(outputPath, UnityShaderLabWriter.Write(unityMetadata));
            Console.WriteLine($"[ShaderDecompile] {shader.Name} done ({succeeded}/{total} passes, inline)");
        }
    }

    private static IEnumerable<UnityShaderMetadataBuilder.ProgramBlobReference> EnumerateProgramBlobIndices(ISerializedProgram program, UnityVersion version, GPUPlatform platform)
    {
        foreach (ShaderReadSource source in EnumerateProgramSources(program, version, platform))
        {
            yield return new UnityShaderMetadataBuilder.ProgramBlobReference(source.BlobIndex, source.ParameterBlobIndex, source.KeywordIndices);
        }
    }

    private static (uint BlobIndex, uint? ParameterBlobIndex, string KeywordIdentity) CreateEmissionKey(uint blobIndex, uint? parameterBlobIndex, IReadOnlyList<ushort>? keywordIndices)
    {
        return (blobIndex, parameterBlobIndex, BuildKeywordIdentity(keywordIndices));
    }

    private static string BuildKeywordIdentity(IReadOnlyList<ushort>? keywordIndices)
    {
        if (keywordIndices is null || keywordIndices.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",", keywordIndices);
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "<no message>";
        int newlineIndex = message.IndexOf('\n');
        return newlineIndex < 0 ? message : message.Substring(0, newlineIndex).TrimEnd();
    }

    /// <summary>
    /// Build the merged .shader text in a single rented buffer and flush UTF-8 bytes
    /// in one write. Hot path for large shaders (50+ passes, multi-MB sources): the
    /// previous StreamWriter loop allocated a string per pass header / TrimEnd / each
    /// interpolated `// SubShader ...` line. The new path:
    ///   1. computes total UTF-16 char count up front,
    ///   2. rents a `char[]` from ArrayPool,
    ///   3. writes every section into the buffer via Span ops + `int.TryFormat`
    ///      (no intermediate strings, integer formatting in-place),
    ///   4. encodes UTF-8 in one pass into a second rented `byte[]`,
    ///   5. writes the file with a single `FileStream.Write` call.
    /// </summary>
    private static void WriteMergedShaderFile(string outputPath, string shaderName, List<ShaderSymbolPass> symbols, DecompileResult[] results)
    {
        // Collect per-pass spans up front. The decompiler's source string is the only
        // big payload; we hold it as a TrimEnd'd ReadOnlyMemory to skip the trailing-
        // whitespace copy without materialising a new string.
        ReadOnlySpan<char> headerLine1Prefix = "// Shader: ".AsSpan();
        ReadOnlySpan<char> headerLine2 = "// Decompiled by ShaderRuriDecompileExporter".AsSpan();
        ReadOnlySpan<char> subShaderPrefix = "// SubShader ".AsSpan();
        ReadOnlySpan<char> passInfix = ", Pass ".AsSpan();
        ReadOnlySpan<char> blobInfix = ", Blob ".AsSpan();
        ReadOnlySpan<char> passNamePrefix = "// PassName: ".AsSpan();
        ReadOnlySpan<char> errorPrefix = "// DecompileError: ".AsSpan();
        ReadOnlySpan<char> noSource = "// No decompiled source generated.".AsSpan();
        ReadOnlySpan<char> nl = Environment.NewLine.AsSpan();

        int total = symbols.Count;
        var trimmedSources = new ReadOnlyMemory<char>[total];
        var firstErrorLines = new ReadOnlyMemory<char>[total];

        // First pass: count chars exactly so we can rent a buffer of the right size.
        // Integer widths are computed by simple base-10 digit count (avoids ToString
        // allocations just to measure length).
        int charCount = 0;
        charCount += headerLine1Prefix.Length + shaderName.Length + nl.Length;
        charCount += headerLine2.Length + nl.Length;

        for (int i = 0; i < total; i++)
        {
            ShaderSymbolPass pass = symbols[i];
            DecompileResult? r = results[i];

            charCount += nl.Length; // blank separator line before each pass

            // "// SubShader N, Pass M, Blob K\n"
            charCount += subShaderPrefix.Length + DecimalDigitCount(pass.Read.SubShaderIndex);
            charCount += passInfix.Length + DecimalDigitCount(pass.Read.PassIndex);
            charCount += blobInfix.Length + DecimalDigitCount(pass.Read.BlobIndex);  // uint overload
            charCount += nl.Length;

            // "// PassName: <name>\n"
            charCount += passNamePrefix.Length + (pass.Read.PassName?.Length ?? 0) + nl.Length;

            // "// DecompileError: <first-line>\n"  (only if message non-empty)
            string? msg = r?.ErrorMessage;
            ReadOnlyMemory<char> firstLine = default;
            if (!string.IsNullOrWhiteSpace(msg))
            {
                int newlineIndex = msg.IndexOf('\n');
                firstLine = newlineIndex < 0 ? msg.AsMemory() : msg.AsMemory(0, newlineIndex).TrimEnd();
                charCount += errorPrefix.Length + firstLine.Length + nl.Length;
            }
            firstErrorLines[i] = firstLine;

            // Body: the decompiled source (TrimEnd'd) or a stub line.
            ReadOnlyMemory<char> srcMem = default;
            if (r is { Success: true, SourceCode: { Length: > 0 } srcText })
            {
                srcMem = srcText.AsMemory().TrimEnd();
            }
            trimmedSources[i] = srcMem;

            if (srcMem.IsEmpty)
            {
                charCount += noSource.Length + nl.Length;
            }
            else
            {
                charCount += srcMem.Length + nl.Length;
            }
        }

        // Second pass: fill the buffer, then encode UTF-8 once and flush.
        char[] charBuffer = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            Span<char> buf = charBuffer.AsSpan(0, charCount);
            int pos = 0;

            CopyAndAdvance(headerLine1Prefix, buf, ref pos);
            CopyAndAdvance(shaderName.AsSpan(), buf, ref pos);
            CopyAndAdvance(nl, buf, ref pos);
            CopyAndAdvance(headerLine2, buf, ref pos);
            CopyAndAdvance(nl, buf, ref pos);

            for (int i = 0; i < total; i++)
            {
                ShaderSymbolPass pass = symbols[i];
                CopyAndAdvance(nl, buf, ref pos);

                CopyAndAdvance(subShaderPrefix, buf, ref pos);
                FormatIntAndAdvance(pass.Read.SubShaderIndex, buf, ref pos);
                CopyAndAdvance(passInfix, buf, ref pos);
                FormatIntAndAdvance(pass.Read.PassIndex, buf, ref pos);
                CopyAndAdvance(blobInfix, buf, ref pos);
                FormatIntAndAdvance(pass.Read.BlobIndex, buf, ref pos);
                CopyAndAdvance(nl, buf, ref pos);

                CopyAndAdvance(passNamePrefix, buf, ref pos);
                CopyAndAdvance((pass.Read.PassName ?? string.Empty).AsSpan(), buf, ref pos);
                CopyAndAdvance(nl, buf, ref pos);

                ReadOnlyMemory<char> firstLine = firstErrorLines[i];
                if (!firstLine.IsEmpty)
                {
                    CopyAndAdvance(errorPrefix, buf, ref pos);
                    CopyAndAdvance(firstLine.Span, buf, ref pos);
                    CopyAndAdvance(nl, buf, ref pos);
                }

                ReadOnlyMemory<char> srcMem = trimmedSources[i];
                if (srcMem.IsEmpty)
                {
                    CopyAndAdvance(noSource, buf, ref pos);
                }
                else
                {
                    CopyAndAdvance(srcMem.Span, buf, ref pos);
                }
                CopyAndAdvance(nl, buf, ref pos);
            }

            // pos must equal charCount because the precount was exact; if not, our
            // length math is off and we'd write garbage past `pos`.
            if (pos != charCount)
            {
                throw new InvalidOperationException($"Shader-merge length math mismatched: expected {charCount} chars, wrote {pos}.");
            }

            int byteCount = System.Text.Encoding.UTF8.GetByteCount(buf);
            byte[] byteBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = System.Text.Encoding.UTF8.GetBytes(buf, byteBuffer.AsSpan());
                using FileStream fs = File.Create(outputPath);
                fs.Write(byteBuffer.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(byteBuffer);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private static void CopyAndAdvance(ReadOnlySpan<char> src, Span<char> dst, ref int pos)
    {
        src.CopyTo(dst.Slice(pos));
        pos += src.Length;
    }

    private static void FormatIntAndAdvance(int value, Span<char> dst, ref int pos)
    {
        if (!value.TryFormat(dst.Slice(pos), out int written))
        {
            throw new InvalidOperationException($"Span buffer too small to format int {value} at offset {pos}.");
        }
        pos += written;
    }

    private static void FormatIntAndAdvance(uint value, Span<char> dst, ref int pos)
    {
        if (!value.TryFormat(dst.Slice(pos), out int written))
        {
            throw new InvalidOperationException($"Span buffer too small to format uint {value} at offset {pos}.");
        }
        pos += written;
    }

    private static int DecimalDigitCount(int value)
    {
        if (value < 0) return 1 + DecimalDigitCount((uint)(-(long)value));
        return DecimalDigitCount((uint)value);
    }

    private static int DecimalDigitCount(uint value)
    {
        if (value < 10) return 1;
        if (value < 100) return 2;
        if (value < 1000) return 3;
        if (value < 10000) return 4;
        if (value < 100000) return 5;
        if (value < 1000000) return 6;
        if (value < 10000000) return 7;
        if (value < 100000000) return 8;
        if (value < 1000000000) return 9;
        return 10;
    }

    /// <summary>
    /// Copy parameters/bindings from <paramref name="source"/> into <paramref name="target"/>
    /// verbatim. The generic exporter is no longer responsible for decoding packed bindings —
    /// that decoder (formerly <c>DecodeUnityBindPoint</c>) is EndField-specific and now lives
    /// in <see cref="PostProcessSymbols"/> game hooks.
    /// </summary>
    private static void AppendSymbols(SerializedProgramData target, SerializedProgramData source)
    {
        foreach (ConstantBufferParameter buffer in source.ConstantBufferParameters)
        {
            target.ConstantBufferParameters.Add(buffer);
        }

        foreach (BufferBindingParameter binding in source.BufferBindingParameters)
        {
            target.BufferBindingParameters.Add(binding);
        }

        foreach (TextureParameter texture in source.TextureParameters)
        {
            target.TextureParameters.Add(texture);
        }

        foreach (VectorParameter vector in source.VectorParameters)
        {
            target.VectorParameters.Add(vector);
        }

        foreach (MatrixParameter matrix in source.MatrixParameters)
        {
            target.MatrixParameters.Add(matrix);
        }

        foreach (BufferBindingParameter buffer in source.BufferParameters)
        {
            target.BufferParameters.Add(buffer);
        }
    }

    private static void AppendRuntimeSymbols(SerializedProgramData target, ShaderSubProgram subProgram)
    {
        foreach (ConstantBufferParameter buffer in subProgram.ConstantBufferParameters)
        {
            target.ConstantBufferParameters.Add(buffer);
        }

        foreach (BufferBindingParameter binding in subProgram.BufferBindingParameters)
        {
            target.BufferBindingParameters.Add(binding);
        }

        foreach (TextureParameter texture in subProgram.TextureParameters)
        {
            target.TextureParameters.Add(texture);
        }

        foreach (SamplerParameter sampler in subProgram.SamplerParameters)
        {
            target.SamplerParameters.Add(sampler);
        }

        foreach (UAVParameter uav in subProgram.UAVParameters)
        {
            target.UAVParameters.Add(uav);
        }

        foreach (VectorParameter vector in subProgram.VectorParameters)
        {
            target.VectorParameters.Add(vector);
        }

        foreach (MatrixParameter matrix in subProgram.MatrixParameters)
        {
            target.MatrixParameters.Add(matrix);
        }

        foreach (BufferBindingParameter buffer in subProgram.BufferParameters)
        {
            target.BufferParameters.Add(buffer);
        }
    }

    private static Dictionary<int, string> BuildNameTable(AccessDictionaryBase<Utf8String, int> nameIndices)
    {
        Dictionary<int, string> table = new(nameIndices.Count);
        for (int i = 0; i < nameIndices.Count; i++)
        {
            var pair = nameIndices.GetPair(i);
            table[pair.Value] = pair.Key.ToString();
        }
        return table;
    }

    /// <summary>
    /// Strip Unity's program-data wrapper to recover the raw GPU bytecode (DXBC for D3D11,
    /// SPIR-V for Vulkan, etc.). Layout per Unity ≥ 5.4 is the same across platforms:
    ///   [u8 header_version] [5 bytes prefix/marker] [optional 0x20-byte common header for v≥2] [payload]
    /// We don't peek the payload's magic bytes here — the downstream
    /// <see cref="ShaderDecompiler"/> auto-detects DXBC / DXIL / SPIR-V by their own magic.
    /// </summary>
    private static byte[] ExtractPayload(byte[] programData, UnityVersion version)
    {
        if (programData.Length == 0)
        {
            return [];
        }

        int headerVersion = programData[0];
        int offset = version.GreaterThanOrEquals(5, 4) ? 6 : 5;
        if (headerVersion >= 2)
        {
            offset += 0x20;
        }
        if (offset < 0 || offset >= programData.Length)
        {
            return [];
        }

        byte[] trimmed = new byte[programData.Length - offset];
        Buffer.BlockCopy(programData, offset, trimmed, 0, trimmed.Length);
        return trimmed;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);
        foreach (char c in value)
        {
            builder.Append(invalidChars.Contains(c) ? '_' : c);
        }
        return builder.ToString();
    }

    private static void LogProgramEnumeration(string shaderName, string stage, ISerializedProgram program, UnityVersion version)
    {
        string? filter = Environment.GetEnvironmentVariable("RURI_SHADER_ENUM_DEBUG");
        if (string.IsNullOrWhiteSpace(filter))
        {
            return;
        }

        if (!shaderName.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Console.WriteLine($"[ShaderEnum] {shaderName} stage={stage}");

        if (program.Has_PlayerSubPrograms() && program.PlayerSubPrograms is not null)
        {
            for (int groupIndex = 0; groupIndex < program.PlayerSubPrograms.Count; groupIndex++)
            {
                AssetList<SerializedPlayerSubProgram> group = program.PlayerSubPrograms[groupIndex];
                AssetList<uint>? paramGroup = program.Has_ParameterBlobIndices() && program.ParameterBlobIndices is not null && groupIndex < program.ParameterBlobIndices.Count
                    ? program.ParameterBlobIndices[groupIndex]
                    : null;
                for (int i = 0; i < group.Count; i++)
                {
                    SerializedPlayerSubProgram playerSubProgram = group[i];
                    uint? parameterBlobIndex = paramGroup is not null && i < paramGroup.Count ? paramGroup[i] : null;
                    ShaderGpuProgramType unityType = ToUnityProgramType(version, playerSubProgram.GpuProgramType);
                    GPUPlatform resolvedPlatform = ProgramTypeToPlatform(unityType);
                    Console.WriteLine($"[ShaderEnum]   Player group={groupIndex} index={i} blob={playerSubProgram.BlobIndex} paramBlob={(parameterBlobIndex.HasValue ? parameterBlobIndex.Value.ToString() : "<none>")} rawType={playerSubProgram.GpuProgramType} unityType={unityType} platform={resolvedPlatform} keywords=[{string.Join(",", playerSubProgram.KeywordIndices ?? [])}]");
                }
            }
        }

        for (int i = 0; i < program.SubPrograms.Count; i++)
        {
            ISerializedSubProgram subProgram = program.SubPrograms[i];
            ShaderGpuProgramType unityType = ToUnityProgramType(version, (sbyte)subProgram.GpuProgramType);
            GPUPlatform resolvedPlatform = ProgramTypeToPlatform(unityType);
            Console.WriteLine($"[ShaderEnum]   Flat index={i} blob={subProgram.BlobIndex} rawType={(sbyte)subProgram.GpuProgramType} unityType={unityType} platform={resolvedPlatform} keywords=[{string.Join(",", subProgram.KeywordIndices ?? [])}]");
        }
    }

    private static bool MatchesPlatform(UnityVersion version, sbyte rawType, GPUPlatform platform)
    {
        ShaderGpuProgramType ut = ToUnityProgramType(version, rawType);
        return ProgramTypeToPlatform(ut) == platform;
    }

    private static ShaderGpuProgramType ToUnityProgramType(UnityVersion version, sbyte rawType)
    {
		int value = rawType;
		if (value < 0)
		{
			throw new NotSupportedException($"Unsupported negative gpu program type {value}");
		}

		if (ShaderGpuProgramTypeExtensions.GpuProgramType55Relevant(version))
		{
			if (Enum.IsDefined(typeof(ShaderGpuProgramType55), value))
			{
				return ((ShaderGpuProgramType55)value).ToGpuProgramType();
			}

			if (Enum.IsDefined(typeof(ShaderGpuProgramType), value))
			{
				return (ShaderGpuProgramType)value;
			}
		}
		else if (Enum.IsDefined(typeof(ShaderGpuProgramType53), value))
		{
			return ((ShaderGpuProgramType53)value).ToGpuProgramType();
		}

		throw new NotSupportedException($"Unsupported gpu program type {value} for Unity {version}");
    }

    /// <summary>
    /// In-process equivalent of <see cref="ShaderGpuProgramTypeExtensions.ToGPUPlatform"/>
    /// without throwing on console types (which require a BuildTarget we don't carry). Returns
    /// <see cref="GPUPlatform.Unknown"/> for anything we don't currently route through
    /// <see cref="ShaderDecompiler"/>.
    /// </summary>
    private static GPUPlatform ProgramTypeToPlatform(ShaderGpuProgramType type)
    {
        return type switch
        {
            ShaderGpuProgramType.SPIRV => GPUPlatform.Vulkan,
            ShaderGpuProgramType.MetalVS or ShaderGpuProgramType.MetalFS => GPUPlatform.Metal,
            ShaderGpuProgramType.DX11VertexSM40
                or ShaderGpuProgramType.DX11VertexSM50
                or ShaderGpuProgramType.DX11PixelSM40
                or ShaderGpuProgramType.DX11PixelSM50
                or ShaderGpuProgramType.DX11GeometrySM40
                or ShaderGpuProgramType.DX11GeometrySM50
                or ShaderGpuProgramType.DX11HullSM50
                or ShaderGpuProgramType.DX11DomainSM50 => GPUPlatform.D3D11,
            ShaderGpuProgramType.DX10Level9Vertex
                or ShaderGpuProgramType.DX10Level9Pixel => GPUPlatform.D3D11_9x,
            ShaderGpuProgramType.DX9VertexSM20
                or ShaderGpuProgramType.DX9VertexSM30
                or ShaderGpuProgramType.DX9PixelSM20
                or ShaderGpuProgramType.DX9PixelSM30 => GPUPlatform.D3D9,
            ShaderGpuProgramType.GLES => GPUPlatform.Gles20,
            ShaderGpuProgramType.GLES3
                or ShaderGpuProgramType.GLES31
                or ShaderGpuProgramType.GLES31AEP => GPUPlatform.Gles3x,
            ShaderGpuProgramType.GLCore32
                or ShaderGpuProgramType.GLCore41
                or ShaderGpuProgramType.GLCore43 => GPUPlatform.GlCore,
            ShaderGpuProgramType.GLLegacy => GPUPlatform.OpenGL,
            ShaderGpuProgramType.PS5NGGC => GPUPlatform.PS5NGGC,
            ShaderGpuProgramType.RayTracing => GPUPlatform.Unknown,
            _ => GPUPlatform.Unknown,
        };
    }

    /// <summary>
    /// Per-pass context handed to <see cref="PostProcessSymbols"/>. Identifies the shader and
    /// pass so a game-specific hook can branch on identity (debug logging, per-pass quirks).
    /// </summary>
    public readonly record struct ShaderReadContext(string ShaderName, int SubShaderIndex, int PassIndex, uint BlobIndex, UnityVersion Version);

    private sealed record ShaderReadSource(uint BlobIndex, uint? ParameterBlobIndex, List<ushort> KeywordIndices, ISerializedProgramParameters? Parameters);

    private sealed record ShaderReadPass(
        string PassName,
        int SubShaderIndex,
        int PassIndex,
        string Stage,
        uint BlobIndex,
        uint? ParameterBlobIndex,
        List<ushort> KeywordIndices,
        ShaderSubProgram SubProgram,
        SerializedProgramData CommonSymbols,
        SerializedProgramData ParameterSymbols,
        byte[] Binary,
        string ShaderName,
        UnityVersion Version);

    private sealed record ShaderSymbolPass(ShaderReadPass Read, SerializedProgramData Symbols);
}
