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
using AssetRipper.SourceGenerated.NativeEnums.Global;
using Ruri.RipperHook;
using Ruri.ShaderTools;
using Ruri.ShaderTools.Unity.ShaderLab;
using Ruri.ShaderTools.Pipeline.Frontend;

namespace Ruri.RipperHook.AR;

public sealed class ShaderRuriDecompileExporter : ShaderExporterBase
{
    public interface IShaderExportObserver
    {
        void OnPassSymbolsRead(SerializedProgramData symbols, ShaderSubProgram subProgram, ShaderReadContext context) { }

        void OnShaderSymbolsRead(IReadOnlyList<ShaderPassView> passes) { }

        void OnShaderDecompiled(string shaderName, IReadOnlyList<ShaderPassResultView> passes) { }

        GPUPlatform PickPlatform(IShader shader, IReadOnlyCollection<GPUPlatform> available, GPUPlatform defaultChoice) => defaultChoice;

        IReadOnlyList<(string Stage, byte[] Binary)>? SplitProgramPayload(byte[] programData, GPUPlatform platform, string stage, UnityVersion version) => null;
    }

    public static IShaderExportObserver? Observer;

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

    private static readonly GPUPlatform[] PreferredPlatforms = new[]
    {
        GPUPlatform.D3D11,
        GPUPlatform.Vulkan,
    };

    public static bool OneVariantPerProgramSlot { get; set; } =
        Environment.GetEnvironmentVariable("RURI_SHADER_FAST_ITERATION") == "1";

    public static bool StrictShaderExport { get; set; } =
        Environment.GetEnvironmentVariable("RURI_STRICT_SHADER_EXPORT") == "1";

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

        string? pinned = Environment.GetEnvironmentVariable("RURI_SHADER_PLATFORM");
        if (!string.IsNullOrWhiteSpace(pinned)
            && Enum.TryParse(pinned, ignoreCase: true, out GPUPlatform pinnedPlatform)
            && available.Contains(pinnedPlatform))
        {
            return pinnedPlatform;
        }

        GPUPlatform chosen = GPUPlatform.Unknown;
        foreach (GPUPlatform candidate in PreferredPlatforms)
        {
            if (available.Contains(candidate))
            {
                chosen = candidate;
                break;
            }
        }

        GPUPlatform preferred = Observer?.PickPlatform(shader, available, chosen) ?? chosen;
        return available.Contains(preferred) ? preferred : chosen;
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

        UnityShaderMetadata unityMetadata = UnityShaderMetadataBuilder.Build(shader, platform, EnumerateProgramBlobIndices,
            symbols.Select(static s => new UnityShaderMetadataBuilder.ProgramResultLocation(
                s.Read.SubShaderIndex, s.Read.PassIndex, s.Read.Stage, s.Read.BlobIndex, s.Read.ParameterBlobIndex, s.Read.KeywordIndices)).ToList());
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

        int slotStart = result.Count;

        foreach (ShaderReadSource source in EnumerateProgramSources(program, shader.Collection.Version, platform))
        {
            ShaderSubProgram subProgram = source.ParameterBlobIndex is uint paramBlobIndex
                ? blob.GetSubProgram(source.BlobIndex, paramBlobIndex)
                : blob.GetSubProgram(source.BlobIndex);

            if (subProgram.ProgramData.Length == 0)
            {
                continue;
            }

            List<(string Stage, byte[] Binary)> binaries = [];
            IReadOnlyList<(string Stage, byte[] Binary)>? split =
                Observer?.SplitProgramPayload(subProgram.ProgramData, platform, stage, shader.Collection.Version);
            if (split is not null)
            {
                binaries.AddRange(split);
            }
            else
            {
                byte[] payload = ExtractPayload(subProgram.ProgramData, shader.Collection.Version);
                if (payload.Length > 0)
                {
                    binaries.Add((stage, payload));
                }
            }
            if (binaries.Count == 0)
            {
                continue;
            }

            foreach ((string moduleStage, byte[] binary) in binaries)
            {
                if (result.Any(existing => existing.SubShaderIndex == subShaderIndex
                    && existing.PassIndex == passIndex
                    && existing.Stage == moduleStage
                    && existing.BlobIndex == source.BlobIndex
                    && existing.KeywordIndices.SequenceEqual(source.KeywordIndices)))
                {
                    continue;
                }

                result.Add(new ShaderReadPass(
                    pass.State.Name_R,
                    subShaderIndex,
                    passIndex,
                    moduleStage,
                    source.BlobIndex,
                    source.ParameterBlobIndex,
                    source.KeywordIndices,
                    subProgram,
                    ReadProgramSymbols(program.CommonParameters, nameTable),
                    ReadProgramSymbols(source.Parameters, nameTable),
                    binary,
                    shader.Name,
                    shader.Collection.Version));
            }
        }

        if (OneVariantPerProgramSlot)
        {
            KeepLargestVariantPerStage(result, slotStart);
        }
    }

    private static void KeepLargestVariantPerStage(List<ShaderReadPass> result, int slotStart)
    {
        if (result.Count - slotStart <= 1)
        {
            return;
        }

        Dictionary<string, ShaderReadPass> largestByStage = new(StringComparer.Ordinal);
        for (int i = slotStart; i < result.Count; i++)
        {
            ShaderReadPass candidate = result[i];
            if (!largestByStage.TryGetValue(candidate.Stage, out ShaderReadPass? incumbent)
                || candidate.Binary.Length > incumbent.Binary.Length)
            {
                largestByStage[candidate.Stage] = candidate;
            }
        }

        result.RemoveRange(slotStart, result.Count - slotStart);
        result.AddRange(largestByStage.Values);
    }

    private static IEnumerable<ShaderReadSource> EnumerateProgramSources(ISerializedProgram program, UnityVersion version, GPUPlatform platform)
    {
        Dictionary<uint, ISerializedSubProgram> subProgramsByBlob = new();
        foreach (ISerializedSubProgram subProgram in program.SubPrograms)
        {
            subProgramsByBlob[subProgram.BlobIndex] = subProgram;
        }

        HashSet<(uint BlobIndex, uint? ParameterBlobIndex, string KeywordIdentity)> emitted = new();

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
                    Index = matrix.OffsetInConstantBuffer,
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
                    Index = vector.OffsetInConstantBuffer,
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
                    Index = structParam.OffsetInConstantBuffer,
                    ArraySize = structParam.ArraySize,
                    StructSize = structParam.StructSize,
                    MatrixMembers = structParam.MatrixMembers.Select(matrix => new MatrixParameter
                    {
                        Name = $"{resolveName(structParam.NameIndex)}.{resolveName(matrix.NameIndex)}",
                        NameIndex = matrix.NameIndex,
                        Index = matrix.OffsetInConstantBuffer,
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
                        Index = vector.OffsetInConstantBuffer,
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

        foreach (var vector in parameters.VectorParams)
        {
            data.VectorParameters.Add(new VectorParameter
            {
                Name = resolveName(vector.NameIndex),
                NameIndex = vector.NameIndex,
                Index = vector.OffsetInConstantBuffer,
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
                Index = matrix.OffsetInConstantBuffer,
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
        IShaderExportObserver? observer = Observer;
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

            observer?.OnPassSymbolsRead(symbols, read.SubProgram, new ShaderReadContext(
                read.ShaderName, read.SubShaderIndex, read.PassIndex, read.BlobIndex, read.Version, read.Stage,
                ProgramTypeToPlatform(read.SubProgram.GetProgramType(read.Version)),
                read.CommonSymbols, read.ParameterSymbols));

            result.Add(new ShaderSymbolPass(read, symbols));
        }

        observer?.OnShaderSymbolsRead(result.Select(static p => new ShaderPassView(
            p.Symbols,
            p.Read.SubShaderIndex,
            p.Read.PassIndex,
            p.Read.Stage,
            ProgramTypeToPlatform(p.Read.SubProgram.GetProgramType(p.Read.Version)) == GPUPlatform.D3D11,
            p.Read.BlobIndex,
            p.Read.Binary,
            p.Read.KeywordIndices)).ToList());

        return result;
    }

    private static void DecompileAndWritePasses(IShader shader, List<ShaderSymbolPass> symbols, UnityShaderMetadata unityMetadata, string outputPath)
    {
        string failuresRoot = outputPath + ".failures";
        int total = symbols.Count;
        var passStems = new string[total];
        var requests = new (byte[] Binary, DecompileOptions Options)[total];

        string? dumpInputDir = Environment.GetEnvironmentVariable("RURI_DUMP_INPUT_DIR");

        for (int i = 0; i < total; i++)
        {
            ShaderSymbolPass pass = symbols[i];
            string passStem = $"sub{pass.Read.SubShaderIndex}.pass{pass.Read.PassIndex}.{pass.Read.Stage.ToLowerInvariant()}.blob{pass.Read.BlobIndex}.{SanitizeFileName(pass.Read.PassName)}";
            passStems[i] = passStem;

            if (!string.IsNullOrEmpty(dumpInputDir))
            {
                Directory.CreateDirectory(dumpInputDir);
                string safeShader = SanitizeFileName(shader.Name);
                File.WriteAllBytes(Path.Combine(dumpInputDir, $"{safeShader}.{passStem}.input.bin"), pass.Read.Binary);
            }

            requests[i] = (pass.Read.Binary, new DecompileOptions
            {
                Format = ShaderBinaryFormat.Unknown,
                Symbols = pass.Symbols,
                UnityMetadata = unityMetadata,
                ShaderModel = 51,
                DebugDumpDirectory = Path.Combine(failuresRoot, passStem),
                DebugDumpStem = "with-symbols",
            });
        }

        int completed = 0;
        using ShaderDecompiler decompiler = new(AppDomain.CurrentDomain.BaseDirectory);
        DecompileResult[] results = decompiler.Decompile(requests, (idx, r) =>
        {
            int now = Interlocked.Increment(ref completed);
            string suffix = r.Success ? string.Empty : $"  Efail: {FirstLine(r.ErrorMessage)}";
            Console.WriteLine($"[ShaderDecompile] {shader.Name} [{now}/{total}] {passStems[idx]}{suffix}");

            if (!r.Success && StrictShaderExport)
            {
                Console.Error.WriteLine($"[ShaderDecompile] RURI_STRICT_SHADER_EXPORT: aborting on first failure  E{shader.Name} {passStems[idx]}");
                Console.Error.WriteLine(r.ErrorMessage);
                Console.Error.WriteLine($"Debug dump: {Path.Combine(failuresRoot, passStems[idx])}");
                Environment.Exit(1);
            }
        });

        Observer?.OnShaderDecompiled(shader.Name, Enumerable.Range(0, total).Select(i => new ShaderPassResultView(
            symbols[i].Symbols,
            symbols[i].Read.Binary,
            passStems[i],
            results[i]?.Success == true)).ToList());

        int succeeded = 0;
        for (int i = 0; i < total; i++)
        {
            if (results[i]?.Success == true) succeeded++;
        }
        UnityShaderMetadataBuilder.BackfillProgramSources(
            unityMetadata,
            symbols.Select(static s => new UnityShaderMetadataBuilder.ProgramResultLocation(s.Read.SubShaderIndex, s.Read.PassIndex, s.Read.Stage, s.Read.BlobIndex, s.Read.ParameterBlobIndex, s.Read.KeywordIndices)).ToArray(),
            results);

        if (SplitVariantsToHlslFiles)
        {
            string variantFolderStem = Path.GetFileNameWithoutExtension(outputPath);
            ShaderLabDocument result = ShaderLabWriter.WriteSplit(unityMetadata, variantFolderStem);
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
            File.WriteAllText(outputPath, ShaderLabWriter.Write(unityMetadata));
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

    private static void WriteMergedShaderFile(string outputPath, string shaderName, List<ShaderSymbolPass> symbols, DecompileResult[] results)
    {
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

        int charCount = 0;
        charCount += headerLine1Prefix.Length + shaderName.Length + nl.Length;
        charCount += headerLine2.Length + nl.Length;

        for (int i = 0; i < total; i++)
        {
            ShaderSymbolPass pass = symbols[i];
            DecompileResult? r = results[i];

            charCount += nl.Length;
            charCount += subShaderPrefix.Length + DecimalDigitCount(pass.Read.SubShaderIndex);
            charCount += passInfix.Length + DecimalDigitCount(pass.Read.PassIndex);
            charCount += blobInfix.Length + DecimalDigitCount(pass.Read.BlobIndex);            charCount += nl.Length;

            charCount += passNamePrefix.Length + (pass.Read.PassName?.Length ?? 0) + nl.Length;

            string? msg = r?.ErrorMessage;
            ReadOnlyMemory<char> firstLine = default;
            if (!string.IsNullOrWhiteSpace(msg))
            {
                int newlineIndex = msg.IndexOf('\n');
                firstLine = newlineIndex < 0 ? msg.AsMemory() : msg.AsMemory(0, newlineIndex).TrimEnd();
                charCount += errorPrefix.Length + firstLine.Length + nl.Length;
            }
            firstErrorLines[i] = firstLine;

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

    public static string SanitizeFileName(string value)
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

    public readonly record struct ShaderReadContext(
        string ShaderName,
        int SubShaderIndex,
        int PassIndex,
        uint BlobIndex,
        UnityVersion Version,
        string Stage,
        GPUPlatform Platform,
        SerializedProgramData CommonSymbols,
        SerializedProgramData ParameterSymbols);

    public sealed record ShaderPassView(SerializedProgramData Symbols, int SubShaderIndex, int PassIndex, string Stage, bool IsDxbc, uint BlobIndex, byte[] Binary, IReadOnlyList<ushort> KeywordIndices);

    public sealed record ShaderPassResultView(SerializedProgramData Symbols, byte[] Binary, string PassStem, bool Success);

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
