using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// JSON schema for a single engine uniform-buffer layout (e.g. `View`,
// `OpaqueBasePass`, `LumenCardScene`). One file per (UBName, LayoutHash)
// pair. See `UE_SYMBOL_SOURCES.md` §6 for the why/how.
//
// Filename convention: `<UBName>_<LayoutHash:08x>_MetaData.json`.
// LayoutHash is FRHIUniformBufferLayoutInitializer::ComputeHash() —
// recoverable from cooked data via FBaseShaderResourceTable
// .ResourceTableLayoutHashes[ubIndex].
//
// Reason for the hash discriminator: the hash closes over the structural
// shape of the UB (ConstantBufferSize, BindingFlags, hasStaticSlot bit,
// per-Resource (MemberOffset, MemberType)) — NOT member names. So one
// metadata file naturally serves every cook with that same shape,
// whether across engine versions or modded engines. Different shape →
// different hash → user drops in a different file.
internal sealed class EngineUbMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; set; } = string.Empty;

    [JsonPropertyName("engineSource")]
    public string EngineSource { get; set; } = string.Empty;

    [JsonPropertyName("layoutHash")]
    public string LayoutHashHex { get; set; } = string.Empty;

    [JsonPropertyName("constantBufferSize")]
    public uint ConstantBufferSize { get; set; }

    [JsonPropertyName("bindingFlags")]
    public string BindingFlags { get; set; } = string.Empty;

    [JsonPropertyName("members")]
    public List<EngineUbNumericMember> Members { get; set; } = new();

    [JsonPropertyName("resources")]
    public List<EngineUbResource> Resources { get; set; } = new();

    public uint ParsedHash()
    {
        string s = LayoutHashHex;
        if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X")) s = s.Substring(2);
        return uint.Parse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class EngineUbNumericMember
{
    [JsonPropertyName("offset")]   public uint Offset { get; set; }
    [JsonPropertyName("name")]     public string Name { get; set; } = string.Empty;
    // One of: Float, Float2, Float3, Float4, Float4x4, Int, Int2, Int3, Int4,
    // UInt, UInt2, UInt3, UInt4, Bool, Bool2, Bool3, Bool4. Case-insensitive.
    [JsonPropertyName("type")]     public string Type { get; set; } = string.Empty;
    // 0 / omitted = not an array. >0 = array of `arraySize` elements.
    [JsonPropertyName("arraySize")] public int ArraySize { get; set; }
}

internal sealed class EngineUbResource
{
    // Matches ResourceIndex from FRHIResourceTableEntry::Unpack(token) —
    // i.e. the index the SRT token stream points at. Must match the index
    // into FRHIUniformBufferLayoutInitializer.Resources[] in the engine
    // source.
    [JsonPropertyName("index")]  public int Index { get; set; }
    // MemberOffset from FRHIUniformBufferResourceInitializer. Informational
    // — used for collision diagnostics, not lookup.
    [JsonPropertyName("offset")] public uint Offset { get; set; }
    [JsonPropertyName("name")]   public string Name { get; set; } = string.Empty;
    // EUniformBufferBaseType enum name: UBMT_TEXTURE, UBMT_SAMPLER,
    // UBMT_SRV, UBMT_UAV, UBMT_RDG_TEXTURE, UBMT_RDG_BUFFER, etc.
    [JsonPropertyName("type")]   public string Type { get; set; } = string.Empty;
}
