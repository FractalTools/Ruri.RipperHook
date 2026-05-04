using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class RuntimeSymbolReader
{
    private static readonly Regex GeneratedUniformBufferNamePattern = new("^CB\\d+UBO$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SerializedProgramData Read(UnrealShaderParser.UnrealMetadata? metadata, MaterialUniformBufferLayout? materialLayout = null)
    {
        SerializedProgramData symbols = new();
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

            symbols.BufferBindingParameters.Add(new BufferBindingParameter
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
        => !string.IsNullOrWhiteSpace(name) && !GeneratedUniformBufferNamePattern.IsMatch(name);
}

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

    private static void DecodeMap(IReadOnlyList<uint>? map, uint resourceTableBits, SrtRegisterType registerType, IReadOnlyList<string>? uniformBufferNames, List<SrtRecord> result)
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

                result.Add(new SrtRecord(bufferIndex, bufferName, resourceIndex, bindIndex, registerType));
                idx++;
            }
        }
    }
}

internal static class ShaderResourceTableSymbolizer
{
    public static void EnrichSymbolData(SerializedProgramData target, UnrealShaderParser.UnrealMetadata? unrealMetadata, MaterialUniformBufferLayout? materialLayout = null)
    {
        if (unrealMetadata == null)
        {
            return;
        }

        IReadOnlyList<string>? uniformBufferNames = unrealMetadata.UniformBufferNames;
        AppendUniformBufferBindings(target, uniformBufferNames);

        FShaderResourceTable srt = unrealMetadata.SRT;
        if (Environment.GetEnvironmentVariable("RURI_SRT_DEBUG") == "1")
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
        Console.Error.WriteLine($"[SRT] ResourceTableBits=0x{srt.ResourceTableBits:X8} ({Convert.ToString(srt.ResourceTableBits, 2).PadLeft(32, '0')})");
        if (uniformBufferNames != null)
        {
            for (int i = 0; i < uniformBufferNames.Count; i++)
            {
                bool used = (srt.ResourceTableBits & (1u << i)) != 0;
                Console.Error.WriteLine($"[SRT] UB[{i}] = {uniformBufferNames[i]} (used={used})");
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
            Console.Error.WriteLine($"[SRT] {label}: <null>");
            return;
        }

        Console.Error.WriteLine($"[SRT] {label} ({map.Count} entries):");
        for (int i = 0; i < map.Count; i++)
        {
            uint token = map[i];
            (int bindIndex, int resourceIndex, int unpackedBufferIndex) = ShaderResourceTableDecoder.Unpack(token);
            Console.Error.WriteLine($"[SRT]   [{i:D3}] = 0x{token:X8} -> bind={bindIndex} resource={resourceIndex} ub={unpackedBufferIndex}");
        }
    }

    private static void AppendUniformBufferBindings(SerializedProgramData target, IReadOnlyList<string>? uniformBufferNames)
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

            if (target.BufferBindingParameters.Any(existing => target.GetSetIdFor(existing.Index, ShaderResourceType.ConstantBuffer) == 0 && existing.Index == i))
            {
                continue;
            }

            target.BufferBindingParameters.Add(new BufferBindingParameter
            {
                Name = name,
                NameIndex = -1,
                Index = i,
                ArraySize = 0,
            });
        }
    }

    private static void AppendTextureParameter(SerializedProgramData target, SrtRecord record, string resolvedName)
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

    private static void AppendSamplerParameter(SerializedProgramData target, SrtRecord record, string resolvedName)
    {
        if (target.SamplerParameters.Any(existing => target.GetSetIdFor(existing.BindPoint, ShaderResourceType.Sampler) == 0 && existing.BindPoint == record.BindIndex))
        {
            return;
        }

        target.SamplerParameters.Add(new SamplerParameter
        {
            Sampler = (uint)record.BindIndex,
            BindPoint = record.BindIndex,
            Name = resolvedName,
        });
    }

    private static void AppendUavParameter(SerializedProgramData target, SrtRecord record, string resolvedName)
    {
        if (target.UAVParameters.Any(existing => target.GetSetIdFor(existing.Index, ShaderResourceType.UAV) == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.UAVParameters.Add(new UAVParameter
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

        if (string.Equals(ubName, "Material", StringComparison.Ordinal) && materialLayout != null)
        {
            string? typed = materialLayout.ResolveResourceName(record);
            if (!string.IsNullOrWhiteSpace(typed))
            {
                return typed!;
            }
        }

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
