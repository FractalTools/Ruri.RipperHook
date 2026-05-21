using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ruri.ShaderTools;

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
// Schema design — this file is the engine's predefined symbol drop for one
// cooked-symbol-poor binding (UB name dropped at cook). The shape mirrors
// what a Material produces at decompile time so the rewriter consumes
// engine UBs through the SAME plumbing as material data:
//   * ConstantBuffer (ConstantBufferParameter) — the numeric cbuffer view
//     (matrix/vector/struct members with byte offsets), name = UB name.
//   * Textures / Samplers / Buffers / UAVs (standard Texture/Sampler/
//     BufferBinding/UAV parameter types) — the named resource bindings,
//     keyed by their position in the engine's resource-table (the SRT
//     "ResourceIndex" — NOT the bind point).
//   * Resources (EngineUbResourceSlot[]) — the canonical engine-side flat
//     resource list (1:1 with FRHIUniformBufferLayoutInitializer.Resources).
//     This is what the layout-hash math folds in (offset + UBMT enum) and
//     what RuntimeSymbolReader matches against record.ResourceIndex.
//
// Why both the typed buckets AND the flat Resources list:
//   The standard parameter types (TextureParameter, SamplerParameter, etc.)
//   carry name + bind slot, but NOT member offset + UBMT enum — those are
//   engine-layout properties unique to FRHIUniformBufferLayoutInitializer.
//   We keep them on a parallel Resources list so the JSON wire format
//   stays standard-types-first while still carrying the engine-side data
//   needed to re-verify the layout hash and to dispatch SRT lookups.
//
// Reason for the hash discriminator: the hash closes over the structural
// shape of the UB (ConstantBufferSize, BindingFlags, hasStaticSlot bit,
// per-Resource (MemberOffset, MemberType)) — NOT member names. So one
// metadata file naturally serves every cook with that same shape,
// whether across engine versions or modded engines. Different shape →
// different hash → user drops in a different file.
internal sealed class EngineUbMetadata
{
    public string Name { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string EngineSource { get; set; } = string.Empty;

    // Hex layout hash. Bound by reflection name `LayoutHash` (PascalCase) +
    // case-insensitive matching covers the legacy `layoutHash` JSON key.
    [JsonPropertyName("LayoutHash")]
    public string LayoutHashHex { get; set; } = string.Empty;

    // EUniformBufferBindingFlags name: "Shader" | "Static" | "StaticAndShader".
    public string BindingFlags { get; set; } = string.Empty;

    // The cbuffer side: standard ConstantBufferParameter (matrix/vector/struct
    // members at their byte offsets, name = UB name). Empty/null for UBs that
    // are pure resource holders (no numeric members).
    public ConstantBufferParameter? ConstantBuffer { get; set; }

    // The resource side, broken out by UBMT class so consumers can iterate
    // a typed list without re-classifying. These are populated post-load
    // from `Resources`, and the JSON also carries the typed-bucket form so
    // hand-authored seeds can be read either way.
    //
    // NOTE on `Index` semantics: for engine-UB seeds, these entries' Index
    // (or SamplerParameter.BindPoint) holds the engine-side RESOURCE
    // TABLE position (== the SRT `ResourceIndex` token field), NOT a HLSL
    // register bind slot. The cooked shader uses the SRT mechanism to
    // map (UB, ResourceIndex) -> bind slot; we only need the resource
    // table position here for the lookup.
    public List<TextureParameter> Textures { get; set; } = new();
    public List<SamplerParameter> Samplers { get; set; } = new();
    public List<BufferBindingParameter> Buffers { get; set; } = new();
    public List<UAVParameter> UAVs { get; set; } = new();

    // Canonical engine-side flat resource list. Each entry is 1:1 with a
    // slot in FRHIUniformBufferLayoutInitializer.Resources[] (post-offset-sort,
    // matching the engine's `ByMemberOffset` comparator). This is the source
    // of truth for layout-hash verification and SRT-resource-index lookup.
    // The typed Textures/Samplers/Buffers/UAVs above are derived views of
    // the same data — populated automatically by `PopulateTypedBucketsFrom
    // Resources()` after deserialization when the seed JSON omits them.
    public List<EngineUbResourceSlot> Resources { get; set; } = new();

    // Optional free-form diagnostic dictionary. Reserved for future schemes
    // to attach scratch info (e.g. dump provenance, validation notes) without
    // changing the schema. Loader ignores it; never used for lookup.
    public Dictionary<string, object>? Debug { get; set; }

    public uint ParsedHash()
    {
        string s = LayoutHashHex;
        if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X")) s = s.Substring(2);
        return uint.Parse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Diagnostic helper: total bytes of the cbuffer portion (sum of numeric
    // members, ignoring any final 16-byte alignment padding the engine adds).
    // Falls back to 0 when ConstantBuffer is null. The layout-hash math
    // uses the C++-struct-sizeof (resources included) which is NOT this;
    // see EngineUbMetadataRegistry.ComputeLayoutHash.
    [JsonIgnore]
    public int ConstantBufferSize => ConstantBuffer?.Size ?? 0;
}

// One slot in FRHIUniformBufferLayoutInitializer.Resources[] — the engine's
// flat resource list, sorted by MemberOffset. Used both for layout-hash
// verification (Offset + UbmtType) and for SRT lookup (record.ResourceIndex
// matches Index here). Name is duplicated into the typed bucket entry that
// holds the same resource (Textures / Samplers / Buffers / UAVs).
internal sealed class EngineUbResourceSlot
{
    public int Index { get; set; }
    public uint Offset { get; set; }
    public string Name { get; set; } = string.Empty;
    // EUniformBufferBaseType enum name: UBMT_TEXTURE, UBMT_SAMPLER,
    // UBMT_SRV, UBMT_UAV, UBMT_RDG_TEXTURE, UBMT_RDG_BUFFER, etc.
    public string UbmtType { get; set; } = string.Empty;
    // HLSL/CPP-side type signature as it appears in the UE source macro
    // — e.g. `Texture3D<uint4>`, `ByteAddressBuffer`, `SamplerState`.
    // Empty for the rare macros without a type token. Used by the
    // type-uniqueness rename path: when the cooked shader has an
    // anonymous binding of a given (UbmtType, ShaderType) combo and the
    // engine UB index has exactly one resource matching that combo, the
    // anonymous slot can be confidently renamed to that real source name.
    public string ShaderType { get; set; } = string.Empty;
}
