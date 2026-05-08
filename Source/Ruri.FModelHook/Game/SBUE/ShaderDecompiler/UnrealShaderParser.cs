using System;
using System.Collections.Generic;
using System.IO;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

public class UnrealShaderParser
{
    public static byte[] Parse(byte[] data, out ShaderArchitecture architecture, out UnrealMetadata? metadata)
    {
        metadata = null;
        using var reader = new BinaryReader(new MemoryStream(data));

        // CRITICAL ordering: deep-scan for "DXBC magic + DXIL chunk inside"
        // BEFORE the surface IsDxbc check. SM 6.0+ shaders compiled by DXC
        // ship with the legacy DXBC container header so the runtime's
        // DXBC-style chunk loader can pick them up — but the actual bytecode
        // inside is DXIL. Returning `Dxbc` for these mis-routes them to the
        // dxbc2dxil tool (which only accepts SM 5.x and earlier and refuses
        // SM 6.0 Wave ops / non-float texture sampling), and ALSO causes
        // Pass 180 to skip the SM-bump heuristic so spirv-cross gets handed
        // `--shader-model 51` and rejects the Wave ops with
        // "Wave ops requires SM 6.0 or higher" / "Sampling non-float textures
        // is not supported in HLSL SM < 6.7" (35/36 of the Global archive's
        // _failures observed empirically). The deep scan is ~µs work since
        // the chunk table sits within the first 256 bytes of any DXBC blob.
        if (IsDxbcWrappedDxil(data))
        {
            architecture = ShaderArchitecture.Dxil;
            return data;
        }
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

        FShaderResourceTable srt = new();
        try
        {
            srt.ResourceTableBits = reader.ReadUInt32();
            srt.ShaderResourceViewMap = ReadUInt32Array(reader);
            srt.SamplerMap = ReadUInt32Array(reader);
            srt.UnorderedAccessViewMap = ReadUInt32Array(reader);
            srt.ResourceTableLayoutHashes = ReadUInt32Array(reader);
        }
        catch
        {
            Console.WriteLine("[Debug] SRT Parse failed, falling back to scan.");
        }

        long codeStart = -1;
        ShaderArchitecture arch = ShaderArchitecture.Unknown;

        long currentPos = reader.BaseStream.Position;
        byte[] remaining = reader.ReadBytes((int)(reader.BaseStream.Length - currentPos));

        int dxbcOffset = FindSequence(remaining, [0x44, 0x58, 0x42, 0x43]);
        if (dxbcOffset >= 0)
        {
            int absStart = (int)currentPos + dxbcOffset;
            codeStart = absStart;

            // Deep-scan the DXBC slice for an embedded DXIL chunk too;
            // SM 6.0+ shaders sit inside a DXBC container even though
            // their bytecode is DXIL. Same SM6-routing rationale as the
            // entry-of-Parse() check above.
            arch = ShaderArchitecture.Dxbc;
            int slice = data.Length - absStart;
            if (slice >= 32)
            {
                byte[] view = new byte[slice];
                Array.Copy(data, absStart, view, 0, slice);
                if (IsDxbcWrappedDxil(view))
                {
                    arch = ShaderArchitecture.Dxil;
                }
            }
        }
        else
        {
            int dxilOffset = FindSequence(remaining, [0x44, 0x58, 0x49, 0x4C]);
            if (dxilOffset >= 0)
            {
                codeStart = currentPos + dxilOffset;
                arch = ShaderArchitecture.Dxil;
            }
            else
            {
                // SM 6.0+ shaders emitted via DXC may ship with their
                // bitcode payload directly (no DXBC container, no DXIL
                // chunk magic). Fall through to a raw LLVM bitcode magic
                // probe before giving up — `BC \xC0\xDE` at the very
                // start signals a bare DXIL module that dxil-spirv
                // accepts on its --raw-llvm path.
                int bitcodeOffset = FindSequence(remaining, [0x42, 0x43, 0xC0, 0xDE]);
                if (bitcodeOffset >= 0)
                {
                    codeStart = currentPos + bitcodeOffset;
                    arch = ShaderArchitecture.Dxil;
                }
                else
                {
                    int shexOffset = FindSequence(remaining, [0x53, 0x48, 0x45, 0x58]);
                    if (shexOffset >= 0)
                    {
                        codeStart = currentPos;
                        arch = ShaderArchitecture.Dxbc;
                    }
                }
            }
        }

        if (arch != ShaderArchitecture.Unknown && codeStart >= 0)
        {
            metadata = new UnrealMetadata
            {
                SRT = srt,
                UniformBufferNames = new List<string>(),
                OptionalDataKeys = new List<string>()
            };

            int len = (int)(data.Length - codeStart);
            uint containerSize = BitConverter.ToUInt32(data, (int)codeStart + 24);

            int nativeCodeSize = (int)containerSize;
            if (nativeCodeSize <= 0 || nativeCodeSize > len)
            {
                nativeCodeSize = len;
            }

            byte[] code = new byte[nativeCodeSize];
            Array.Copy(data, codeStart, code, 0, nativeCodeSize);

            ParseOptionalDataFromShaderTail(data, metadata);

            architecture = arch;
            return code;
        }

        long fallbackOffset = -1;
        ShaderArchitecture fallbackArch = ShaderArchitecture.Unknown;

        Console.WriteLine($"[Debug] Fallback Scan on {data.Length} bytes...");
        int fDxbc = FindSequence(data, [0x44, 0x58, 0x42, 0x43]);
        if (fDxbc >= 0)
        {
            Console.WriteLine($"[Debug] Found DXBC at {fDxbc}");
            fallbackOffset = fDxbc;
            fallbackArch = ShaderArchitecture.Dxbc;
        }
        else
        {
            int fDxil = FindSequence(data, [0x44, 0x58, 0x49, 0x4C]);
            if (fDxil >= 0)
            {
                Console.WriteLine($"[Debug] Found DXIL at {fDxil}");
                fallbackOffset = fDxil;
                fallbackArch = ShaderArchitecture.Dxil;
            }
            else
            {
                int fShex = FindSequence(data, [0x53, 0x48, 0x45, 0x58]);
                if (fShex >= 0)
                {
                    Console.WriteLine($"[Debug] Found SHEX at {fShex}");
                    fallbackOffset = fShex;
                    fallbackArch = ShaderArchitecture.Dxbc;
                }
                else
                {
                    Console.WriteLine("[Debug] No magic found.");
                }
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
        public List<string> UniformBufferNames = new();
        public string ShaderName = string.Empty;
        public List<string> OptionalDataKeys = new();
        public FShaderCodePackedResourceCounts? ShaderCodePackedResourceCounts;
        public FShaderCodeResourceMasks? ShaderCodeResourceMasks;
        public FShaderCodeFeatures? ShaderCodeFeatures;
        public FShaderCodeName? ShaderCodeName;
        public FShaderCodeUniformBuffers? ShaderCodeUniformBuffers;
        public FShaderCodeVendorExtension? ShaderCodeVendorExtension;
        public bool? IsSm6Shader;
    }

    public struct FShaderCodePackedResourceCounts
    {
        public const byte Key = (byte)'p';
        public byte UsageFlags;
        public byte NumSamplers;
        public byte NumSRVs;
        public byte NumCBs;
        public byte NumUAVs;
    }

    public struct FShaderCodeResourceMasks
    {
        public const byte Key = (byte)'m';
        public uint UAVMask;
    }

    public struct FShaderCodeFeatures
    {
        public const byte Key = (byte)'x';
        public byte CodeFeatures;
    }

    public sealed class FShaderCodeName
    {
        public const byte Key = (byte)'n';
        public string Value { get; set; } = string.Empty;
    }

    public sealed class FShaderCodeUniformBuffers
    {
        public const byte Key = (byte)'u';
        public List<string> Names { get; set; } = new();
    }

    public sealed class FShaderCodeVendorExtension
    {
        public const byte Key = (byte)'v';
        public byte[] RawData { get; set; } = Array.Empty<byte>();
    }

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
                else if (key == FShaderCodeUniformBuffers.Key)
                {
                    metadata.UniformBufferNames ??= new List<string>();
                    metadata.ShaderCodeUniformBuffers ??= new FShaderCodeUniformBuffers();

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int strLen = reader.ReadInt32();
                        string s = string.Empty;
                        if (strLen == 0)
                        {
                        }
                        else if (strLen > 0)
                        {
                            byte[] peek = reader.ReadBytes(Math.Min(strLen * 2, (int)(stream.Length - stream.Position)));
                            stream.Position -= peek.Length;

                            if (peek.Length >= 2 && peek[1] == 0)
                            {
                                byte[] strBytes = reader.ReadBytes(strLen * 2);
                                if (strLen > 1)
                                {
                                    s = System.Text.Encoding.Unicode.GetString(strBytes, 0, (strLen - 1) * 2);
                                }
                            }
                            else
                            {
                                byte[] strBytes = reader.ReadBytes(strLen);
                                if (strLen > 1)
                                {
                                    s = System.Text.Encoding.ASCII.GetString(strBytes, 0, strLen - 1);
                                }
                            }
                        }
                        else
                        {
                            int len = -strLen;
                            byte[] strBytes = reader.ReadBytes(len);
                            if (len > 1)
                            {
                                s = System.Text.Encoding.ASCII.GetString(strBytes, 0, len - 1);
                            }
                        }

                        metadata.UniformBufferNames.Add(s);
                        metadata.ShaderCodeUniformBuffers.Names.Add(s);
                    }
                }
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
        if (count < 0 || count > 10000)
        {
            throw new Exception("Invalid array count");
        }

        List<uint> list = new(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadUInt32());
        }
        return list;
    }

    private static bool IsDxbc(byte[] data)
        => data.Length >= 4 && data[0] == 0x44 && data[1] == 0x58 && data[2] == 0x42 && data[3] == 0x43;

    // Mirror of `ShaderDecompiler.IsDxil`'s deep-chunk-scan path. The DXBC
    // container header is 32 bytes; chunk count sits at offset 28; the
    // chunk-offset table starts at 32 and runs `count*4` bytes. We walk
    // every chunk and look for `DXIL` (the actual bitcode chunk) or
    // `ILDB`/`ILDN` (DXC's debug-info chunks that are emitted alongside
    // DXIL on `-Zi` / `-Qstrip_reflect` builds). Either signal proves
    // the container is SM 6.0+ even though the outer magic is DXBC.
    //
    // Capped at 256 chunks as a sanity gate against malformed headers
    // (real cooked shaders ship with 5-12 chunks max).
    private static bool IsDxbcWrappedDxil(byte[] data)
    {
        if (!IsDxbc(data) || data.Length < 32) return false;
        int count = BitConverter.ToInt32(data, 28);
        if (count <= 0 || count > 256 || 32 + (count * 4) > data.Length) return false;
        for (int i = 0; i < count; i++)
        {
            int offset = BitConverter.ToInt32(data, 32 + (i * 4));
            if (offset < 0 || offset + 4 > data.Length) continue;
            // 'D' 'X' 'I' 'L'
            if (data[offset] == 0x44 && data[offset + 1] == 0x58 && data[offset + 2] == 0x49 && data[offset + 3] == 0x4C) return true;
            // 'I' 'L' 'D' 'B' / 'I' 'L' 'D' 'N' — DXC debug-info chunks.
            if (data[offset] == 0x49 && data[offset + 1] == 0x4C && data[offset + 2] == 0x44 &&
                (data[offset + 3] == 0x42 || data[offset + 3] == 0x4E)) return true;
        }
        return false;
    }

    // True when `data` STARTS with the DXIL chunk magic. Distinct from
    // ShaderDecompiler.IsDxil which scans inside a DXBC container — this
    // method intentionally checks ONLY the start because it's used after
    // the parser has already located the chunk start via FindSequence.
    //
    // SM 6.0+ shaders emitted by DXC arrive with the DXIL chunk preceded
    // by the DXBC container header; the chunk offset (16 bytes after the
    // container start) is what FindSequence resolves to before this
    // check fires.
    //
    // We also accept a bare LLVM bitcode magic (`BC \xC0\xDE`) so shaders
    // hand-extracted from a container still route through the DXIL path.
    private static bool IsDxil(byte[] data)
    {
        if (data.Length < 4) return false;
        // 'D' 'X' 'I' 'L' container chunk magic.
        if (data[0] == 0x44 && data[1] == 0x58 && data[2] == 0x49 && data[3] == 0x4C) return true;
        // 'B' 'C' 0xC0 0xDE — bare LLVM bitcode (raw DXIL outside the
        // DXBC envelope). dxil-spirv handles this with --raw-llvm.
        if (data[0] == 0x42 && data[1] == 0x43 && data[2] == 0xC0 && data[3] == 0xDE) return true;
        return false;
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
            if (match)
            {
                return i;
            }
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
