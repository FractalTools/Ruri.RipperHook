using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Ruri.ShaderTools;
using Ruri.ShaderTools.Spirv;
// Alias engine types — our namespace ends in `ShaderDecompiler` which
// shadows the engine's `ShaderDecompiler` class name.
using ShaderDecompilerEngine = Ruri.ShaderTools.ShaderDecompiler;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 003 — The "Light" stage. Per-shader hot loop:
//   1. Strip the UE shader-binary wrapper -> clean DXBC/DXIL bytes +
//      UnrealMetadata (FShaderResourceTable, optional UB names, etc.)
//   2. Pick the best material symbol source (UnifiedMaterialReader ->
//      MaterialJsonSymbolReader). Both are caches; first lookup pays
//      the JSON-parse cost, repeats are O(1).
//   3. Assemble final ShaderSymbolData by combining:
//        - runtime symbols (FShaderCodeUniformBuffers + SRT-decoded
//          texture/sampler/UAV names, refined by the material's
//          MaterialUniformBufferLayout when available)
//        - the material's own ConstantBuffer schemas
//   4. Hand off to the engine-pure ShaderDecompiler. Pass
//      `MaterialTextureNameInferrer.InferAndAppend` as the
//      MetadataEnricher so UE's OpSampledImage-pair texture-name
//      recovery plugs in at the right point (post-rewrite,
//      pre-symbol-patch) without the engine itself knowing anything
//      UE-specific.
//   5. Write the decompiled HLSL + sidecar metadata.
//
// Failure handling: each shader's failure dumps land in a per-shader
// subdirectory under `<output>/_failures/<stem>/` so users can diff
// pre-rewrite vs post-rewrite vs post-patch SPIR-V offline.
//
// File holds the per-shader pipeline + every UE-specific helper it
// pulls in: UnrealShaderParser (binary header strip), RuntimeSymbolReader
// (UB bindings + SRT enrichment), ShaderResourceTableDecoder/Symbolizer,
// MaterialTextureNameInferrer (OpSampledImage texture-name recovery).
internal static class Pass003_DecompileShaders
{
	// 因为UE的Shader没有容器 必须手动切块Blob 否则就需要等待所有Blob反编译完成才会输出结果
    private const int DefaultBatchSize = 256;

    public static void DoPass(PipelineState state)
    {
        if (state.Library is null) throw new InvalidOperationException("Pass000 must run before Pass003.");

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

        HashSet<string>? materialFilterVariants = string.IsNullOrWhiteSpace(state.Options.MaterialFilter)
            ? null
            : MaterialPathVariants.Build(state.Options.MaterialFilter!.Replace('\\', '/'));

        ShaderLibrary lib = state.Library;

        // Phase 1 — sequential prep. Each shader's wrapper-strip + symbol-source lookup is
        // small and CPU-cheap; doing it on the main thread keeps the parallel hot path
        // (Phase 2) trivially thread-safe (state mutations only happen here and in Phase 3).
        var preps = new List<ShaderPrep>(lib.ShaderEntries.Length);
        for (int i = 0; i < lib.ShaderEntries.Length; i++)
        {
            if (state.Options.ShaderIndexFilter is { Count: > 0 } && !state.Options.ShaderIndexFilter.Contains(i))
            {
                state.Skipped++;
                continue;
            }

            byte[]? raw = lib.GetShaderCode(i);
            if (raw == null) { state.Skipped++; continue; }

            if (materialFilterVariants is { Count: > 0 })
            {
                if (!state.UsageByShaderIndex.TryGetValue(i, out HashSet<string>? usage)
                    || !usage.Any(m => MaterialPathVariants.Build(m).Overlaps(materialFilterVariants)))
                {
                    state.Skipped++;
                    continue;
                }
            }

            try
            {
                ShaderPrep prep = PrepareSingleShader(state, i, raw);
                preps.Add(prep);
            }
            catch (Exception ex)
            {
                state.Failed++;
                state.LogError($"Shader {i}: prep exception: {ex.Message}");
            }
        }

        // Phase 2 — hand the engine the entire request list. With > 1 request it scales
        // worker count automatically against system CPU; with one it just runs serially
        // on this same instance. From here it's mostly waiting on dxbc2dxil / dxil-spirv
        // / spirv-cross subprocesses.
        var requests = new (byte[] Binary, EngineDecompileOptions Options)[preps.Count];
        for (int i = 0; i < preps.Count; i++)
        {
            requests[i] = (preps[i].StrippedCode, preps[i].EngineOptions);
        }

        using ShaderDecompilerEngine engine = new(outputDir);

        // Phase 3 — process the library in fixed-size chunks so we keep the engine's
        // internal parallel batch execution, but don't retain one giant result array /
        // all emitted HLSL for the whole library before writing anything.
        for (int batchStart = 0; batchStart < preps.Count; batchStart += DefaultBatchSize)
        {
            int batchCount = Math.Min(DefaultBatchSize, preps.Count - batchStart);
            var batchRequests = new (byte[] Binary, EngineDecompileOptions Options)[batchCount];
            for (int i = 0; i < batchCount; i++)
            {
                batchRequests[i] = requests[batchStart + i];
            }

            DecompileResult[] results = engine.Decompile(batchRequests);

            // Finalize each chunk before submitting the next one so outputs appear on disk
            // incrementally and peak memory scales with batch size, not total shader count.
            for (int i = 0; i < batchCount; i++)
            {
                try
                {
                    FinalizeSingleShader(state, preps[batchStart + i], results[i]);
                }
                catch (Exception ex)
                {
                    state.Failed++;
                    state.LogError($"Shader {preps[batchStart + i].ShaderIndex}: finalize exception: {ex.Message}");
                }
            }
        }

        state.Log($"    Library {Path.GetFileName(state.Options.LibraryPath)}: total={lib.ShaderEntries.Length} decompiled={state.Decompiled} skipped={state.Skipped} failed={state.Failed}.");
    }

    // Per-shader artefacts the prep pass collects so Phase 2 only touches binary + options
    // and Phase 3 has everything it needs to write outputs without re-reading metadata.
    private sealed class ShaderPrep
    {
        public required int ShaderIndex { get; init; }
        public required string TypeSuffix { get; init; }
        public required byte[] StrippedCode { get; init; }
        public required EngineDecompileOptions EngineOptions { get; init; }
        public required string ProvisionalStem { get; init; }
        public required ShaderSymbolData Metadata { get; init; }
        public HashSet<string>? UsedBy { get; init; }
    }

    private static ShaderPrep PrepareSingleShader(PipelineState state, int shaderIndex, byte[] raw)
    {
        ShaderCodeEntry entry = state.Library!.ShaderEntries[shaderIndex];
        string typeSuffix = ShaderFrequency.ToString(entry.Frequency);

        string provisionalStem = state.NameByShaderIndex.TryGetValue(shaderIndex, out string? preMapped) && !string.IsNullOrWhiteSpace(preMapped)
            ? string.Join("_", preMapped.Split(Path.GetInvalidFileNameChars())) + $"_{typeSuffix}_{shaderIndex}"
            : $"shader_{typeSuffix}_{shaderIndex:D6}";
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

        ShaderSymbolData metadata = RuntimeSymbolReader.Read(unrealMetadata, bestSource?.MaterialLayout);
        if (bestSource != null)
        {
            foreach (ConstantBuffer cb in bestSource.Metadata.ConstantBuffers)
            {
                if (!metadata.ConstantBuffers.Any(existing => string.Equals(existing.Name, cb.Name, StringComparison.Ordinal)))
                {
                    metadata.ConstantBuffers.Add(cb);
                }
            }
        }

        EngineDecompileOptions engineOptions = new()
        {
            Format = detectedFormat,
            Metadata = metadata,
            ShaderModel = state.Options.ShaderModel,
            MetadataEnricher = static (spv, md) => MaterialTextureNameInferrer.InferAndAppend(spv, md),
            DebugDumpDirectory = state.Options.DumpFailures ? failureDumpDir : null,
            DebugDumpStem = state.Options.DumpFailures ? (bestSource != null ? "with-symbols" : "no-symbols") : null,
        };

        return new ShaderPrep
        {
            ShaderIndex = shaderIndex,
            TypeSuffix = typeSuffix,
            StrippedCode = strippedCode,
            EngineOptions = engineOptions,
            ProvisionalStem = provisionalStem,
            Metadata = metadata,
            UsedBy = hadUsage ? usedBy : null,
        };
    }

    private static void FinalizeSingleShader(PipelineState state, ShaderPrep prep, DecompileResult? result)
    {
        if (result is null)
        {
            state.Failed++;
            state.LogError($"Shader {prep.ShaderIndex} ({prep.ProvisionalStem}): batch worker returned no result.");
            return;
        }

        if (!result.Success)
        {
            state.Failed++;
            string firstLine = result.ErrorMessage?.Split('\n', 2)[0]?.Trim() ?? "<no message>";
            string dumpHint = string.IsNullOrEmpty(result.DebugDumpDirectory) ? "" : $" (dumped: {result.DebugDumpDirectory})";
            state.LogError($"Shader {prep.ShaderIndex} ({prep.ProvisionalStem}) [stage={result.FailedStage ?? "unknown"}]: {firstLine}{dumpHint}");
            return;
        }

        string finalName = ResolveFinalName(state, prep.ShaderIndex);
        finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
        string outNameStemNoExt = $"{finalName}_{prep.TypeSuffix}_{prep.ShaderIndex}";

        if (prep.UsedBy is { Count: > 0 } usedBy && result.FinalMetadata != null)
        {
            result.FinalMetadata.UsedMaterials = usedBy
                .OrderBy(static m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        string sourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension;
        string outputFilePath = Path.Combine(state.OutputDirectory, outNameStemNoExt + sourceExtension);
        string basePath = Path.Combine(state.OutputDirectory, outNameStemNoExt);
        File.WriteAllText(outputFilePath, result.SourceCode ?? string.Empty);

        if (result.FinalMetadata != null)
        {
            File.WriteAllText(basePath + ".metadata.json", JsonConvert.SerializeObject(result.FinalMetadata, Formatting.Indented));
        }

        state.Decompiled++;
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
            if (candidate != null)
            {
                // Defensive copy — caches share metadata across every shader
                // using the same material; SRT enrichment + texture-name
                // inferrer mutate it in place.
                ShaderSymbolData clone = new()
                {
                    ConstantBuffers = new List<ConstantBuffer>(candidate.Metadata.ConstantBuffers),
                    ConstantBufferBindings = new List<BufferBinding>(candidate.Metadata.ConstantBufferBindings),
                    TextureParameters = new List<TextureParameter>(candidate.Metadata.TextureParameters),
                    Samplers = new List<SamplerParameter>(candidate.Metadata.Samplers),
                    UAVs = new List<UAVParameter>(candidate.Metadata.UAVs),
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

// =====================================================================
// UnrealShaderParser - strips FShaderResourceTable + optional data from
// the front of a UE shader binary, returning the inner DXBC/DXIL/SHEX
// blob plus parsed UnrealMetadata.
// =====================================================================
public class UnrealShaderParser
    {
        public static byte[] Parse(byte[] data, out ShaderArchitecture architecture, out UnrealMetadata? metadata)
        {
            metadata = null;
            using var reader = new BinaryReader(new MemoryStream(data));
            
            // Try to parse FShaderResourceTable
            // SRT structure:
            // uint32 ResourceTableBits
            // TArray<uint32> ShaderResourceViewMap
            // TArray<uint32> SamplerMap
            // TArray<uint32> UnorderedAccessViewMap
            // TArray<uint32> ResourceTableLayoutHashes
            // TArray<uint32> TextureMap (Added in later versions, verify presence)
            
            // Heuristic: If it starts with a reasonable bitmask (usually 0 or small) and then valid array lengths
            // It might be a UE shader.
            
            // However, FModel might export raw DXBC if it skips SRT? 
            // If data starts with "DXBC", it's raw.
            if (IsDxbc(data))
            {
                architecture = ShaderArchitecture.Dxbc;
                return data;
            }
            if (IsDxil(data))
            {
                architecture = ShaderArchitecture.Dxil;
                return data;
            }

            // Assume UE format with SRT
            var srt = new FShaderResourceTable();
            try {
                srt.ResourceTableBits = reader.ReadUInt32();
                srt.ShaderResourceViewMap = ReadUInt32Array(reader);
                srt.SamplerMap = ReadUInt32Array(reader);
                srt.UnorderedAccessViewMap = ReadUInt32Array(reader);
                srt.ResourceTableLayoutHashes = ReadUInt32Array(reader);
            } catch {
                // Not a valid SRT, fall through to fallback
                Console.WriteLine("[Debug] SRT Parse failed, falling back to scan.");
            }


            // After SRT, there might be Optional Data (FShaderCodePackedResourceCounts etc)
            // UE uses FShaderCodeReader which checks for optional data map.
            // But usually the Native Code follows immediately or after some padding.
            
            // Find DXBC or DXIL magic
            long codeStart = -1;
            ShaderArchitecture arch = ShaderArchitecture.Unknown;
            
            long currentPos = reader.BaseStream.Position;
            byte[] remaining = reader.ReadBytes((int)(reader.BaseStream.Length - currentPos));
            
            int dxbcOffset = FindSequence(remaining, new byte[] { 0x44, 0x58, 0x42, 0x43 }); // DXBC
            if (dxbcOffset >= 0)
            {
                codeStart = currentPos + dxbcOffset;
                arch = ShaderArchitecture.Dxbc;
            }
            else
            {
                int dxilOffset = FindSequence(remaining, new byte[] { 0x44, 0x58, 0x49, 0x4C }); // DXIL
                if (dxilOffset >= 0)
                {
                    codeStart = currentPos + dxilOffset;
                    arch = ShaderArchitecture.Dxil;
                }
                else
                {
                     // Try finding "SHEX" or "ILDB" if header is stripped
                     int shexOffset = FindSequence(remaining, new byte[] { 0x53, 0x48, 0x45, 0x58 }); // SHEX
                     if (shexOffset >= 0)
                     {
                         // Found stripped DXBC
                         codeStart = currentPos; // We might need to reconstruct header, but for now take from current
                         // Wait, if SHEX found, the data start is likely 'currentPos' (after SRT).
                         // We'll pass the whole remaining block to Repair
                         arch = ShaderArchitecture.Dxbc;
                     }
                }
            }

            if (arch != ShaderArchitecture.Unknown && codeStart >= 0)
            {
                // Parse optional data between SRT and Code
                metadata = new UnrealMetadata { SRT = srt, UniformBufferNames = new List<string>(), OptionalDataKeys = new List<string>() };

                // Extract code
                int len = (int)(data.Length - codeStart);
                uint containerSize = BitConverter.ToUInt32(data, (int)codeStart + 24);
                
                // If containerSize is valid and smaller than len, there's data after
                int nativeCodeSize = (int)containerSize;
                if (nativeCodeSize <= 0 || nativeCodeSize > len) nativeCodeSize = len;

                byte[] code = new byte[nativeCodeSize];
                Array.Copy(data, codeStart, code, 0, nativeCodeSize);

                // UE source uses FShaderCodeReader on the full shader blob that still includes
                // optional data at the tail. Parse optional data from the original entry bytes,
                // not from the stripped native container we pass downstream for decompilation.
                ParseOptionalDataFromShaderTail(data, metadata);

                architecture = arch;
                return code;
            }
            
            // Fallback: Scan for magic in the whole buffer
            long fallbackOffset = -1;
            var fallbackArch = ShaderArchitecture.Unknown;
            
            // Re-use logic to find DXBC/DXIL/SHEX in 'data'
            Console.WriteLine($"[Debug] Fallback Scan on {data.Length} bytes...");
            int fDxbc = FindSequence(data, new byte[] { 0x44, 0x58, 0x42, 0x43 });
            if (fDxbc >= 0) { Console.WriteLine($"[Debug] Found DXBC at {fDxbc}"); fallbackOffset = fDxbc; fallbackArch = ShaderArchitecture.Dxbc; }
            else 
            {
                int fDxil = FindSequence(data, new byte[] { 0x44, 0x58, 0x49, 0x4C });
                if (fDxil >= 0) { Console.WriteLine($"[Debug] Found DXIL at {fDxil}"); fallbackOffset = fDxil; fallbackArch = ShaderArchitecture.Dxil; }
                else
                {
                    int fShex = FindSequence(data, new byte[] { 0x53, 0x48, 0x45, 0x58 });
                    if (fShex >= 0) { Console.WriteLine($"[Debug] Found SHEX at {fShex}"); fallbackOffset = fShex; fallbackArch = ShaderArchitecture.Dxbc; }
                    else Console.WriteLine("[Debug] No magic found.");
                }
            }

            if (fallbackArch != ShaderArchitecture.Unknown && fallbackOffset >= 0)
            {
                int len = (int)(data.Length - fallbackOffset);
                byte[] code = new byte[len];
                Array.Copy(data, fallbackOffset, code, 0, len);
                architecture = fallbackArch;
                return code;
            }

            architecture = ShaderArchitecture.Unknown;
            return data;
        }

        public class UnrealMetadata
        {
            public FShaderResourceTable SRT;
            public List<string> UniformBufferNames;
            public string ShaderName;
            public List<string> OptionalDataKeys;
            public FShaderCodePackedResourceCounts? ShaderCodePackedResourceCounts;
            public FShaderCodeResourceMasks? ShaderCodeResourceMasks;
            public FShaderCodeFeatures? ShaderCodeFeatures;
            public FShaderCodeName? ShaderCodeName;
            public FShaderCodeUniformBuffers? ShaderCodeUniformBuffers;
            public FShaderCodeVendorExtension? ShaderCodeVendorExtension;
            public bool? IsSm6Shader;
        }

        // UE source name: FShaderCodePackedResourceCounts (ShaderCore.h)
        public struct FShaderCodePackedResourceCounts
        {
            public const byte Key = (byte)'p';
            public byte UsageFlags;
            public byte NumSamplers;
            public byte NumSRVs;
            public byte NumCBs;
            public byte NumUAVs;
        }

        // UE source name: FShaderCodeResourceMasks (ShaderCore.h)
        public struct FShaderCodeResourceMasks
        {
            public const byte Key = (byte)'m';
            public uint UAVMask;
        }

        // UE source name: FShaderCodeFeatures (ShaderCore.h)
        public struct FShaderCodeFeatures
        {
            public const byte Key = (byte)'x';
            public byte CodeFeatures;
        }

        // UE source name: FShaderCodeName (ShaderCore.h)
        public sealed class FShaderCodeName
        {
            public const byte Key = (byte)'n';
            public string Value { get; set; } = string.Empty;
        }

        // UE source name: FShaderCodeUniformBuffers (ShaderCore.h)
        public sealed class FShaderCodeUniformBuffers
        {
            public const byte Key = (byte)'u';
            public List<string> Names { get; set; } = new();
        }

        // UE source name: FShaderCodeVendorExtension (ShaderCore.h)
        // Keep as a formal placeholder even when current sample does not use it.
        public sealed class FShaderCodeVendorExtension
        {
            public const byte Key = (byte)'v';
            public byte[] RawData { get; set; } = Array.Empty<byte>();
        }

        // UE source usage: AddOptionalData('6', &IsSM6, 1)
        public struct FShaderCodeSm6Flag
        {
            public const byte Key = (byte)'6';
            public byte Value;
        }

        private static void ParseOptionalDataFromShaderTail(byte[] shaderCode, UnrealMetadata metadata)
        {
            if (shaderCode.Length < sizeof(int))
            {
                return;
            }

            int optionalDataSize = BitConverter.ToInt32(shaderCode, shaderCode.Length - sizeof(int));
            if (optionalDataSize <= 0 || optionalDataSize > shaderCode.Length)
            {
                return;
            }

            int optionalDataStart = shaderCode.Length - optionalDataSize;
            int optionalDataPayloadLength = optionalDataSize - sizeof(int);
            if (optionalDataPayloadLength <= 0)
            {
                return;
            }

            try 
            {
                using var stream = new MemoryStream(shaderCode, optionalDataStart, optionalDataPayloadLength, false);
                using var reader = new BinaryReader(stream);
                
                while (stream.Position < stream.Length)
                {
                    byte key = reader.ReadByte();
                    int size = reader.ReadInt32();

                    if (size < 0 || stream.Position + size > stream.Length)
                    {
                        break;
                    }

                    long nextPos = stream.Position + size;
                    metadata.OptionalDataKeys.Add(DescribeOptionalDataKey(key, size));

                    if (key == FShaderCodePackedResourceCounts.Key && size >= 5)
                    {
                        metadata.ShaderCodePackedResourceCounts = new FShaderCodePackedResourceCounts
                        {
                            UsageFlags = reader.ReadByte(),
                            NumSamplers = reader.ReadByte(),
                            NumSRVs = reader.ReadByte(),
                            NumCBs = reader.ReadByte(),
                            NumUAVs = reader.ReadByte()
                        };
                    }
                    else if (key == FShaderCodeResourceMasks.Key && size == 4)
                    {
                        metadata.ShaderCodeResourceMasks = new FShaderCodeResourceMasks
                        {
                            UAVMask = reader.ReadUInt32()
                        };
                    }
                    else if (key == FShaderCodeFeatures.Key && size >= 1)
                    {
                        metadata.ShaderCodeFeatures = new FShaderCodeFeatures
                        {
                            CodeFeatures = reader.ReadByte()
                        };
                    }
                    
                    // UE source name: FShaderCodeUniformBuffers
                    else if (key == FShaderCodeUniformBuffers.Key) 
                    {
                        if (metadata.UniformBufferNames == null) metadata.UniformBufferNames = new List<string>();
                        metadata.ShaderCodeUniformBuffers ??= new FShaderCodeUniformBuffers();
                        
                        // int count
                        int count = reader.ReadInt32();
                        for(int i=0; i<count; i++)
                        {
                            int strLen = reader.ReadInt32(); // FString length
                            string s = "";
                            if (strLen == 0) { }
                            else if (strLen > 0)
                            {
                                // Detection: Read strLen bytes. If we find many nulls or it looks like UTF16, read strLen*2.
                                byte[] peek = reader.ReadBytes(Math.Min(strLen * 2, (int)(stream.Length - stream.Position)));
                                stream.Position -= peek.Length;

                                if (peek.Length >= 2 && peek[1] == 0) // Heuristic: second byte 0 is common for ASCII in UTF16
                                {
                                    byte[] strBytes = reader.ReadBytes(strLen * 2);
                                    if (strLen > 1) s = System.Text.Encoding.Unicode.GetString(strBytes, 0, (strLen - 1) * 2);
                                }
                                else
                                {
                                    byte[] strBytes = reader.ReadBytes(strLen);
                                    if (strLen > 1) s = System.Text.Encoding.ASCII.GetString(strBytes, 0, strLen - 1);
                                }
                            }
                            else
                            {
                                int len = -strLen;
                                byte[] strBytes = reader.ReadBytes(len);
                                if (len > 1) s = System.Text.Encoding.ASCII.GetString(strBytes, 0, len - 1);
                            }
                            metadata.UniformBufferNames.Add(s);
                            metadata.ShaderCodeUniformBuffers.Names.Add(s);
                            // Console.WriteLine($"[Debug] Found UB Name: {s}");
                        }
                    }
                    // UE source name: FShaderCodeName
                    else if (key == FShaderCodeName.Key)
                    {
                        if (size > 0)
                        {
                            byte[] nameBytes = reader.ReadBytes(size);
                            int stringLength = Array.IndexOf(nameBytes, (byte)0);
                            if (stringLength < 0)
                            {
                                stringLength = nameBytes.Length;
                            }

                            metadata.ShaderName = System.Text.Encoding.ASCII.GetString(nameBytes, 0, stringLength);
                            metadata.ShaderCodeName = new FShaderCodeName { Value = metadata.ShaderName };
                        }
                    }
                    else if (key == FShaderCodeVendorExtension.Key)
                    {
                        metadata.ShaderCodeVendorExtension = new FShaderCodeVendorExtension
                        {
                            RawData = reader.ReadBytes(size)
                        };
                    }
                    else if (key == FShaderCodeSm6Flag.Key && size >= 1)
                    {
                        metadata.IsSm6Shader = reader.ReadByte() != 0;
                    }
                    else
                    {
                        stream.Seek(size, SeekOrigin.Current);
                    }
                    stream.Position = nextPos;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Debug] Error parsing optional data: {ex.Message}");
            }
        }


        private static List<uint> ReadUInt32Array(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 10000) throw new Exception("Invalid array count");
            var list = new List<uint>(count);
            for(int i=0; i<count; i++) list.Add(reader.ReadUInt32());
            return list;
        }

        private static bool IsDxbc(byte[] data)
        {
             if (data.Length < 4) return false;
             return data[0] == 0x44 && data[1] == 0x58 && data[2] == 0x42 && data[3] == 0x43;
        }
        
        private static bool IsDxil(byte[] data)
        {
             if (data.Length < 4) return false;
             return data[0] == 0x44 && data[1] == 0x58 && data[2] == 0x49 && data[3] == 0x4C;
        }

        private static ShaderArchitecture DetectArch(byte[] data)
        {
            if (IsDxbc(data)) return ShaderArchitecture.Dxbc;
            if (IsDxil(data)) return ShaderArchitecture.Dxil;
            return ShaderArchitecture.Unknown;
        }

        private static int FindSequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string DescribeOptionalDataKey(byte key, int size)
        {
            string name = key switch
            {
                FShaderCodePackedResourceCounts.Key => "FShaderCodePackedResourceCounts",
                FShaderCodeResourceMasks.Key => "FShaderCodeResourceMasks",
                FShaderCodeFeatures.Key => "FShaderCodeFeatures",
                FShaderCodeName.Key => "FShaderCodeName",
                FShaderCodeUniformBuffers.Key => "FShaderCodeUniformBuffers",
                FShaderCodeVendorExtension.Key => "FShaderCodeVendorExtension",
                FShaderCodeSm6Flag.Key => "SM6Flag",
                _ => $"unknown('{(char)key}')"
            };

            return $"{name}[{(char)key}] Size={size}";
        }
    }

public struct FShaderResourceTable
{
    public uint ResourceTableBits;
    public List<uint> ShaderResourceViewMap;
    public List<uint> SamplerMap;
    public List<uint> UnorderedAccessViewMap;
    public List<uint> ResourceTableLayoutHashes;
}

// =====================================================================
// RuntimeSymbolReader - UB bindings from FShaderCodeUniformBuffers, then
// SRT enrichment (delegated to ShaderResourceTableSymbolizer).
// =====================================================================
internal static class RuntimeSymbolReader
{
    private static readonly Regex GeneratedUniformBufferNamePattern = new("^CB\\d+UBO$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ShaderSymbolData Read(UnrealShaderParser.UnrealMetadata? metadata, MaterialUniformBufferLayout? materialLayout = null)
    {
        ShaderSymbolData symbols = new();
        if (metadata?.UniformBufferNames == null)
        {
            return symbols;
        }

        for (int i = 0; i < metadata.UniformBufferNames.Count; i++)
        {
            string name = metadata.UniformBufferNames[i];
            if (!IsCanonicalUniformBufferName(name))
            {
                continue;
            }

            symbols.ConstantBufferBindings.Add(new BufferBinding
            {
                Name = name,
                NameIndex = -1,
                Index = i,
                ArraySize = 0,
            });
        }

        ShaderResourceTableSymbolizer.EnrichSymbolData(symbols, metadata, materialLayout);

        return symbols;
    }

    private static bool IsCanonicalUniformBufferName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && !GeneratedUniformBufferNamePattern.IsMatch(name);
    }
}

// =====================================================================
// ShaderResourceTableDecoder - unpacks SRT map[] tokens into typed records.
// =====================================================================
internal enum SrtRegisterType
{
    Texture,
    ShaderResourceView,
    Sampler,
    UnorderedAccessView,
}

internal sealed record SrtRecord(
    int UniformBufferIndex,
    string? UniformBufferName,
    int ResourceIndex,
    int BindIndex,
    SrtRegisterType RegisterType);

// Decodes FShaderResourceTable's packed uint32 entries into named slot
// records. Layout per Engine/Source/Runtime/RHI/Public/RHIDefinitions.h
// FRHIResourceTableEntry:
//   bits  0..7  -> BindIndex          (shader register)
//   bits  8..23 -> ResourceIndex      (index into UB's resource list)
//   bits 24..31 -> UniformBufferIndex (slot of the UB)
// Header layout per Engine/Source/Runtime/D3D12RHI/Private/D3D12Commands.cpp:
//   ResourceMap[bufferIndex] is the array offset where that UB's token
//   stream begins; tokens are read until UniformBufferIndex changes.
internal static class ShaderResourceTableDecoder
{
    public static List<SrtRecord> Decode(FShaderResourceTable srt, IReadOnlyList<string>? uniformBufferNames)
    {
        List<SrtRecord> result = new();
        if (srt.ResourceTableBits == 0)
        {
            return result;
        }

        DecodeMap(srt.ShaderResourceViewMap, srt.ResourceTableBits, SrtRegisterType.ShaderResourceView, uniformBufferNames, result);
        DecodeMap(srt.SamplerMap, srt.ResourceTableBits, SrtRegisterType.Sampler, uniformBufferNames, result);
        DecodeMap(srt.UnorderedAccessViewMap, srt.ResourceTableBits, SrtRegisterType.UnorderedAccessView, uniformBufferNames, result);
        return result;
    }

    public static (int BindIndex, int ResourceIndex, int UniformBufferIndex) Unpack(uint token)
    {
        int bindIndex = (int)(token & 0xFFu);
        int resourceIndex = (int)((token >> 8) & 0xFFFFu);
        int uniformBufferIndex = (int)((token >> 24) & 0xFFu);
        return (bindIndex, resourceIndex, uniformBufferIndex);
    }

    private static void DecodeMap(
        IReadOnlyList<uint>? map,
        uint resourceTableBits,
        SrtRegisterType registerType,
        IReadOnlyList<string>? uniformBufferNames,
        List<SrtRecord> result)
    {
        if (map == null || map.Count == 0)
        {
            return;
        }

        for (int bufferIndex = 0; bufferIndex < 32; bufferIndex++)
        {
            if ((resourceTableBits & (1u << bufferIndex)) == 0)
            {
                continue;
            }

            if (bufferIndex >= map.Count)
            {
                break;
            }

            uint headerOffset = map[bufferIndex];
            if (headerOffset == 0)
            {
                continue;
            }

            int idx = (int)headerOffset;
            while (idx >= 0 && idx < map.Count)
            {
                uint token = map[idx];
                if (token == 0xFFFFFFFFu)
                {
                    break;
                }

                (int bindIndex, int resourceIndex, int unpackedBufferIndex) = Unpack(token);
                if (unpackedBufferIndex != bufferIndex)
                {
                    break;
                }

                string? bufferName = uniformBufferNames != null && bufferIndex < uniformBufferNames.Count
                    ? uniformBufferNames[bufferIndex]
                    : null;

                result.Add(new SrtRecord(
                    bufferIndex,
                    bufferName,
                    resourceIndex,
                    bindIndex,
                    registerType));
                idx++;
            }
        }
    }
}

// =====================================================================
// ShaderResourceTableSymbolizer - bridges SRT decode to ShaderSymbolData.
// =====================================================================
// Bridges SRT decode + the shader's optional FShaderCodeUniformBuffers
// list to a ShaderSymbolData populated with named bindings:
//   - one BufferBinding per uniform buffer (`b<i>` named `<UBName>`)
//   - one TextureParameter / SamplerParameter / BufferBinding /
//     UAVParameter per SRT entry, named `<UBName>_<ResourceLabel>`.
//
// The ResourceLabel here is a *shape-correct placeholder* — for the
// `Material` UB it is later overwritten with proper
// `Texture2D_<i>` / `Texture2D_<i>Sampler` etc. by the Material UB
// layout helper. For engine UBs (`View`, `OpaqueBasePass`, ...) it is
// looked up against EngineUniformBuffers, with a placeholder fallback.
//
// Anonymous placeholders carry the UB context so that even when we
// don't yet know the canonical member name, the decompiled HLSL gets
// a far more readable identifier than spirv-cross's
// `_RegisterSpace0[N]`.
internal static class ShaderResourceTableSymbolizer
{
    public static void EnrichSymbolData(
        ShaderSymbolData target,
        UnrealShaderParser.UnrealMetadata? unrealMetadata,
        MaterialUniformBufferLayout? materialLayout = null)
    {
        if (unrealMetadata == null)
        {
            return;
        }

        IReadOnlyList<string>? uniformBufferNames = unrealMetadata.UniformBufferNames;
        AppendUniformBufferBindings(target, uniformBufferNames);

        FShaderResourceTable srt = unrealMetadata.SRT;
        if (System.Environment.GetEnvironmentVariable("RURI_SRT_DEBUG") == "1")
        {
            DumpSrt(srt, uniformBufferNames);
        }
        List<SrtRecord> records = ShaderResourceTableDecoder.Decode(srt, uniformBufferNames);
        foreach (SrtRecord record in records)
        {
            string resolvedName = ResolveResourceName(record, materialLayout);
            switch (record.RegisterType)
            {
                case SrtRegisterType.Texture:
                case SrtRegisterType.ShaderResourceView:
                    AppendTextureParameter(target, record, resolvedName);
                    break;
                case SrtRegisterType.Sampler:
                    AppendSamplerParameter(target, record, resolvedName);
                    break;
                case SrtRegisterType.UnorderedAccessView:
                    AppendUavParameter(target, record, resolvedName);
                    break;
            }
        }
    }

    private static void DumpSrt(FShaderResourceTable srt, IReadOnlyList<string>? uniformBufferNames)
    {
        System.Console.Error.WriteLine($"[SRT] ResourceTableBits=0x{srt.ResourceTableBits:X8} ({System.Convert.ToString(srt.ResourceTableBits, 2).PadLeft(32, '0')})");
        if (uniformBufferNames != null)
        {
            for (int i = 0; i < uniformBufferNames.Count; i++)
            {
                bool used = (srt.ResourceTableBits & (1u << i)) != 0;
                System.Console.Error.WriteLine($"[SRT] UB[{i}] = {uniformBufferNames[i]} (used={used})");
            }
        }
        DumpMap("SRV/Texture", srt.ShaderResourceViewMap);
        DumpMap("Sampler", srt.SamplerMap);
        DumpMap("UAV", srt.UnorderedAccessViewMap);
        DumpMap("LayoutHashes", srt.ResourceTableLayoutHashes);
    }

    private static void DumpMap(string label, IReadOnlyList<uint>? map)
    {
        if (map == null)
        {
            System.Console.Error.WriteLine($"[SRT] {label}: <null>");
            return;
        }
        System.Console.Error.WriteLine($"[SRT] {label} ({map.Count} entries):");
        for (int i = 0; i < map.Count; i++)
        {
            uint token = map[i];
            (int bindIndex, int resourceIndex, int unpackedBufferIndex) = ShaderResourceTableDecoder.Unpack(token);
            System.Console.Error.WriteLine($"[SRT]   [{i:D3}] = 0x{token:X8} -> bind={bindIndex} resource={resourceIndex} ub={unpackedBufferIndex}");
        }
    }

    private static void AppendUniformBufferBindings(ShaderSymbolData target, IReadOnlyList<string>? uniformBufferNames)
    {
        if (uniformBufferNames == null)
        {
            return;
        }

        for (int i = 0; i < uniformBufferNames.Count; i++)
        {
            string name = uniformBufferNames[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (target.ConstantBufferBindings.Any(existing => target.GetSetIdFor(existing.Index, ShaderResourceType.ConstantBuffer) == 0 && existing.Index == i))
            {
                continue;
            }

            target.ConstantBufferBindings.Add(new BufferBinding
            {
                Name = name,
                NameIndex = -1,
                Index = i,
                ArraySize = 0,
            });
        }
    }

    private static void AppendTextureParameter(ShaderSymbolData target, SrtRecord record, string resolvedName)
    {
        if (target.TextureParameters.Any(existing => target.GetSetIdFor(existing.Index, ShaderResourceType.Texture) == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.TextureParameters.Add(new TextureParameter
        {
            Name = resolvedName,
            NameIndex = -1,
            Index = record.BindIndex,
            SamplerIndex = -1,
            MultiSampled = false,
            Dim = 2,
        });
    }

    private static void AppendSamplerParameter(ShaderSymbolData target, SrtRecord record, string resolvedName)
    {
        if (target.Samplers.Any(existing => target.GetSetIdFor(existing.Index, ShaderResourceType.Sampler) == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.Samplers.Add(new SamplerParameter
        {
            Sampler = (uint)record.BindIndex,
            Index = record.BindIndex,
            Name = resolvedName,
        });
    }

    private static void AppendUavParameter(ShaderSymbolData target, SrtRecord record, string resolvedName)
    {
        if (target.UAVs.Any(existing => target.GetSetIdFor(existing.Index, ShaderResourceType.UAV) == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.UAVs.Add(new UAVParameter
        {
            Name = resolvedName,
            NameIndex = -1,
            Index = record.BindIndex,
            OriginalIndex = record.BindIndex,
        });
    }

    private static string ResolveResourceName(SrtRecord record, MaterialUniformBufferLayout? materialLayout)
    {
        string ubName = string.IsNullOrWhiteSpace(record.UniformBufferName)
            ? $"UB{record.UniformBufferIndex}"
            : record.UniformBufferName!;

        if (string.Equals(ubName, "Material", System.StringComparison.Ordinal) && materialLayout != null)
        {
            string? typed = materialLayout.ResolveResourceName(record);
            if (!string.IsNullOrWhiteSpace(typed))
            {
                return typed!;
            }
        }

        // Engine UBs (View, OpaqueBasePass, SceneTextures, LumenCardScene,
        // VirtualShadowMap, ...) — their per-member names live only in
        // engine C++ source and are NOT serialized into cooked data.
        // Recovery from a shipping cook alone is impossible by design
        // (see UE_SHIPPING_NAME_TRUTH.md). We deliberately do not
        // hard-code those layouts: they would silently rot across UE
        // versions and outright fabricate names for any custom-engine
        // fork. So everything outside the Material UB falls through to
        // the placeholder below — UB context is preserved (`View_SRV45`
        // tells the reader which UB the slot belongs to and at which
        // resource index) without inventing a member name we cannot
        // prove from the game files.
        string suffix = record.RegisterType switch
        {
            SrtRegisterType.Sampler => $"Sampler{record.ResourceIndex}",
            SrtRegisterType.UnorderedAccessView => $"UAV{record.ResourceIndex}",
            SrtRegisterType.ShaderResourceView => $"SRV{record.ResourceIndex}",
            _ => $"Resource{record.ResourceIndex}",
        };
        return $"{ubName}_{suffix}";
    }
}

// =====================================================================
// MaterialTextureNameInferrer - recovers Material UB texture names by
// matching OpSampledImage pairs against already-named samplers.
// =====================================================================
// Recovers Material UB texture names by leveraging UE's mechanically-paired
// texture/sampler emission.
//
// In Engine/Source/Runtime/Engine/Private/Materials/HLSLMaterialTranslator.cpp:6108-6131,
// FHLSLMaterialTranslator::TextureSample() emits the sampler argument as:
//
//   SSM_FromTextureAsset           -> "<TextureName>Sampler"
//   SSM_Wrap_WorldGroupSettings    -> "GetMaterialSharedSampler(<TextureName>Sampler, View.MaterialTextureBilinearWrapedSampler)"
//                                  or "GetMaterialSharedSampler(<TextureName>Sampler, Material.Wrap_WorldGroupSettings)"
//   SSM_Clamp_WorldGroupSettings   -> "GetMaterialSharedSampler(<TextureName>Sampler, View.MaterialTextureBilinearClampedSampler)"
//                                  or "GetMaterialSharedSampler(<TextureName>Sampler, Material.Clamp_WorldGroupSettings)"
//   SSM_TerrainWeightmapGroupSettings -> "GetMaterialSharedSampler(<TextureName>Sampler, View.LandscapeWeightmapSampler)"
//
// where <TextureName> is "Material.Texture2D_<i>" / "Material.TextureCube_<i>" /
// etc., the same name CreateBufferStruct() registered for the Material UB
// member. After RemoveUniformBuffersFromSource flattens "." -> "_", the
// shader compiler sees them as paired top-level globals.
//
// On the SSM_FromTextureAsset path the texture and its OWN sampler are the
// arguments to the shader's `Texture.Sample(Sampler, ...)` call, so once we
// know the sampler is `Material_Texture2D_<i>Sampler` (recovered via SRT +
// CreateBufferStruct replay), the texture in that SampledImage pair is by
// construction `Material_Texture2D_<i>` -- no inference, no heuristic, the
// pairing is literally hardcoded one line above in the same C++ function.
//
// This pass scans the SPIR-V module for OpSampledImage instructions, walks
// back the texture-load and sampler-load operands to their OpVariable
// declarations, reads the (DescriptorSet, Binding) decorations off both,
// and -- for any pair whose sampler resolves to a Material UB sampler name
// -- adds a TextureParameter for the texture binding with the canonical
// Material UB texture name.
//
// Limits:
// - Only the SSM_FromTextureAsset path is closed-form recoverable (the
//   other SSM_* paths route through GetMaterialSharedSampler, which makes
//   the sampler argument at the OpSampledImage call site a *shared*
//   View / Material sampler that doesn't match the texture name).
//   For those, this pass does nothing and the texture stays anonymous.
//   That's correct: we can't *prove* the pairing for shared-sampler
//   textures from SPIR-V alone.
internal static class MaterialTextureNameInferrer
{
    private const ushort OpLoad = 61;
    private const ushort OpSampledImage = 86;

    public static int InferAndAppend(byte[] spirv, ShaderSymbolData symbols)
    {
        if (spirv == null || spirv.Length < SpvOpCode.HeaderWordCount * 4)
        {
            return 0;
        }
        if (symbols.Samplers.Count == 0)
        {
            return 0;
        }

        uint[] words = BytesToWords(spirv);
        if (words.Length < SpvOpCode.HeaderWordCount || words[0] != SpvOpCode.MagicNumber)
        {
            return 0;
        }

        // Build maps in a single pass:
        //   loadResult -> sourceVarId          (from OpLoad pointer if pointer is a variable)
        //   varId -> (DescriptorSet, Binding)  (from OpDecorate)
        Dictionary<uint, uint> loadToVar = new();
        Dictionary<uint, int?> varToSet = new();
        Dictionary<uint, int?> varToBinding = new();
        List<(uint ImageLoadId, uint SamplerLoadId)> sampledImagePairs = new();

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint header = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(header);
            ushort wordCount = SpvOpCode.GetWordCount(header);
            if (wordCount == 0)
            {
                break;
            }

            switch (opCode)
            {
                case SpvOpCode.OpDecorate when wordCount >= 4:
                    {
                        uint targetId = words[offset + 1];
                        uint decoration = words[offset + 2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            varToSet[targetId] = (int)words[offset + 3];
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            varToBinding[targetId] = (int)words[offset + 3];
                        }
                        break;
                    }
                case OpLoad when wordCount >= 4:
                    {
                        // OpLoad result_type result_id pointer [memory_access]
                        uint resultId = words[offset + 2];
                        uint pointerId = words[offset + 3];
                        // We optimistically treat the pointer as the variable
                        // itself; in HLSL-style SPIR-V from dxil-spirv the
                        // texture/sampler loads consume OpVariables directly
                        // without intervening OpAccessChain. If a later OpLoad
                        // overwrites the same result id, we just keep the most
                        // recent one (single-static-assignment in valid SPIR-V
                        // means this only happens across non-overlapping basic
                        // blocks anyway, so any of them points at the same
                        // texture/sampler binding for this pass's purposes).
                        loadToVar[resultId] = pointerId;
                        break;
                    }
                case OpSampledImage when wordCount >= 5:
                    {
                        // OpSampledImage result_type result_id image sampler
                        uint imageOperand = words[offset + 3];
                        uint samplerOperand = words[offset + 4];
                        sampledImagePairs.Add((imageOperand, samplerOperand));
                        break;
                    }
            }

            offset += wordCount;
        }

        if (sampledImagePairs.Count == 0)
        {
            return 0;
        }

        // Build a lookup: bindIndex -> sampler name (only Material samplers).
        Dictionary<int, string> samplerNameByBinding = new();
        foreach (SamplerParameter sampler in symbols.Samplers)
        {
            if (symbols.GetSetIdFor(sampler.Index, ShaderResourceType.Sampler) != 0 || string.IsNullOrWhiteSpace(sampler.Name))
            {
                continue;
            }
            samplerNameByBinding[sampler.Index] = sampler.Name!;
        }
        if (samplerNameByBinding.Count == 0)
        {
            return 0;
        }

        // Build a lookup: bindIndex -> existing TextureParameter (so we
        // don't overwrite a name that already came from a more authoritative
        // source like SRT-bound Material textures).
        HashSet<int> existingTextureBindings = new();
        foreach (TextureParameter texture in symbols.TextureParameters)
        {
            if (symbols.GetSetIdFor(texture.Index, ShaderResourceType.Texture) == 0)
            {
                existingTextureBindings.Add(texture.Index);
            }
        }

        // Dedupe pairs and group by sampler binding -- the SSM_FromTextureAsset
        // tight pair is a 1:1 invariant on the UE side
        // (HLSLMaterialTranslator.cpp:6110 emits one TexName + one
        // TexNameSampler for the call site). When a single sampler is paired
        // with MULTIPLE distinct textures inside the same shader, that
        // sampler is NOT SSM_FromTextureAsset's per-texture sampler -- it's
        // a shared sampler being used for several textures, and we cannot
        // derive any one texture's name from it. Drop those samplers.
        Dictionary<int, HashSet<int>> texturesPerSamplerBinding = new();
        Dictionary<(int, int), bool> resolvedPairs = new();
        foreach ((uint imageLoadId, uint samplerLoadId) in sampledImagePairs)
        {
            if (!loadToVar.TryGetValue(imageLoadId, out uint imageVarId)
                || !loadToVar.TryGetValue(samplerLoadId, out uint samplerVarId))
            {
                continue;
            }

            int? imageSet = varToSet.GetValueOrDefault(imageVarId);
            int? imageBinding = varToBinding.GetValueOrDefault(imageVarId);
            int? samplerSet = varToSet.GetValueOrDefault(samplerVarId);
            int? samplerBinding = varToBinding.GetValueOrDefault(samplerVarId);
            if (imageSet != 0 || imageBinding == null || samplerSet != 0 || samplerBinding == null)
            {
                continue;
            }

            int sb = samplerBinding.Value;
            int ib = imageBinding.Value;
            if (!texturesPerSamplerBinding.TryGetValue(sb, out HashSet<int>? texSet))
            {
                texSet = new HashSet<int>();
                texturesPerSamplerBinding[sb] = texSet;
            }
            texSet.Add(ib);
            resolvedPairs[(sb, ib)] = true;
        }

        int appended = 0;
        HashSet<int> alreadyInferred = new();
        foreach (var kvp in resolvedPairs)
        {
            int samplerBinding = kvp.Key.Item1;
            int imageBinding = kvp.Key.Item2;

            // 1:1 invariant: skip samplers paired with multiple distinct
            // textures (shared-sampler pattern, not SSM_FromTextureAsset).
            if (texturesPerSamplerBinding[samplerBinding].Count != 1)
            {
                continue;
            }
            if (existingTextureBindings.Contains(imageBinding) || alreadyInferred.Contains(imageBinding))
            {
                continue;
            }
            if (!samplerNameByBinding.TryGetValue(samplerBinding, out string? samplerName))
            {
                continue;
            }

            string? textureName = DeriveTextureNameFromSamplerName(samplerName);
            if (textureName == null)
            {
                continue;
            }

            symbols.TextureParameters.Add(new TextureParameter
            {
                Name = textureName,
                NameIndex = -1,
                Index = imageBinding,
                SamplerIndex = samplerBinding,
                MultiSampled = false,
                Dim = 2,
            });
            alreadyInferred.Add(imageBinding);
            appended++;
        }

        return appended;
    }

    // SSM_FromTextureAsset (the only SSM whose sampler argument *is* the
    // texture's own paired sampler) emits sampler name "<TexName>Sampler".
    // The TexName can be either CreateBufferStruct's typed name (Texture2D_<i>
    // etc.) or the author-facing parameter name (`BambooBaseMaps`) when our
    // layout substituted it. Either way, stripping the trailing "Sampler"
    // suffix gives the texture's name. The Wrap/Clamp_WorldGroupSettings
    // unconditional samplers don't have a paired texture, so reject them.
    private static string? DeriveTextureNameFromSamplerName(string samplerName)
    {
        const string SamplerSuffix = "Sampler";
        if (!samplerName.EndsWith(SamplerSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        if (!samplerName.StartsWith("Material_", StringComparison.Ordinal))
        {
            return null;
        }

        string textureName = samplerName.Substring(0, samplerName.Length - SamplerSuffix.Length);

        // Reject the two unconditional fixed members. They have no paired
        // texture (UE emits them as standalone shared samplers).
        if (textureName.EndsWith("_Wrap_WorldGroupSettings", StringComparison.Ordinal)
            || textureName.EndsWith("_Clamp_WorldGroupSettings", StringComparison.Ordinal))
        {
            return null;
        }

        return textureName;
    }

    private static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
        return words;
    }
}
