# EngineUbMetadata — Engine Uniform Buffer Member Layouts

This folder holds per-`(UBName, LayoutHash)` JSON files that name members
of UE engine-defined uniform buffers (`View`, `OpaqueBasePass`,
`LumenCardScene`, `VirtualShadowMap`, `LocalVF`, etc.) which cannot be
recovered from cooked shipping data on UE 5.1 (all SMs) or UE 5.4 SM5.

See [`UE_SYMBOL_SOURCES.md`](../../../../../../../Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md)
for the full closed-world analysis and why this folder exists.

## Filename convention

```
<UBName>_<LayoutHash:08x>_MetaData.json
```

Examples:
```
View_3F8A12C5_MetaData.json
OpaqueBasePass_A1B2C3D4_MetaData.json
LumenCardScene_98765432_MetaData.json
```

- `<UBName>` is the uniform-buffer name UE writes into the `'u'`
  optional-data block of every cooked shader (`View`, `Material`, etc.).
- `<LayoutHash:08x>` is `FRHIUniformBufferLayoutInitializer::ComputeHash()`
  rendered as 8 lowercase hex digits (no `0x` prefix). The decompiler
  reads this hash from `FBaseShaderResourceTable.ResourceTableLayoutHashes[i]`
  in each cooked shader's SRT chunk and looks up
  `(UBName, ResourceTableLayoutHashes[i])` here.

## Schema

```jsonc
{
  "name": "View",
  "engineVersion": "5.4.4",                                       // docs only
  "engineSource": "Engine/.../SceneView.h:1016",                   // docs only
  "layoutHash": "0x3F8A12C5",                                      // discriminator
  "constantBufferSize": 3776,                                       // cross-check
  "bindingFlags": "Shader",                                         // docs only
  "members": [
    { "offset":   0, "name": "TranslatedWorldToClip",  "type": "Float4x4" },
    { "offset":  64, "name": "WorldToClip",             "type": "Float4x4" },
    { "offset": 128, "name": "TranslatedWorldToView",   "type": "Float4x4" },
    // ...
  ],
  "resources": [
    { "index": 0,  "offset": 4096, "name": "MaterialTextureBilinearWrapedSampler", "type": "UBMT_SAMPLER" },
    { "index": 45, "offset": 4136, "name": "PerlinNoise3DTexture",                 "type": "UBMT_TEXTURE" }
    // ...
  ]
}
```

### Numeric member types

`type` is matched case-insensitively. Recognized:

| `type` | Means |
| --- | --- |
| `Float` / `Float2` / `Float3` / `Float4` | float scalar / vec2 / vec3 / vec4 |
| `Int` / `Int2..4` | int scalar / vector |
| `UInt` / `UInt2..4` | uint scalar / vector |
| `Bool` / `Bool2..4` | bool scalar / vector (HLSL cbuffer-encoded as int) |
| `Half` / `Half2..4` | half scalar / vector |
| `Float4x4`, `Float3x3`, `Float3x4`, `Float4x3` | matrix forms |

Anything else is silently skipped (use anonymous offset placeholder downstream).

### Resource types

`type` should be one of the `EUniformBufferBaseType` enum names:
- `UBMT_TEXTURE` — bound to `register(t#)`
- `UBMT_SRV` — bound to `register(t#)`
- `UBMT_SAMPLER` — bound to `register(s#)`
- `UBMT_UAV` — bound to `register(u#)`
- `UBMT_RDG_TEXTURE` / `UBMT_RDG_BUFFER` / others — same as plain types
  for our purposes (the SRT decoder already knows the register class
  from the SRT map type).

`index` MUST match `FRHIResourceTableEntry::Unpack(token).ResourceIndex`
as decoded from the cooked SRT for this UB. It is **not** the bind
register — it is the index into `FRHIUniformBufferLayoutInitializer.Resources[]`
that the engine source declares.

## How the hash is computed (so you can derive a file's name from UE source)

The hash function is byte-identical in UE 5.1.1 and UE 5.4.4 —
`Engine/Source/Runtime/RHI/Public/RHIUniformBufferLayoutInitializer.h:62-92`:

```cpp
void ComputeHash()
{
    uint32 TmpHash = ConstantBufferSize << 16
                   | static_cast<uint32>(BindingFlags) << 8
                   | static_cast<uint32>(StaticSlot != MAX_UNIFORM_BUFFER_STATIC_SLOTS);
    for (i = 0..Resources.Num()-1) TmpHash ^= Resources[i].MemberOffset;
    // unrolled XOR-fold of Resources[i].MemberType in 4 / 2 / 1 byte groups
    Hash = TmpHash;
}
```

Inputs:
- `ConstantBufferSize` (uint32)
- `BindingFlags` (uint8: `Shader` / `Static` / `StaticAndShader`)
- `(StaticSlot != INVALID)` bit
- For each entry in `Resources[]`: `MemberOffset`, `MemberType`

Member NAMES are NOT in the hash — same shape = same hash regardless of
naming. That's why one file can serve multiple engine versions when the
layout shape happens to match.

## Generating files

For a chosen UB on a given engine version:

1. Find the `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(...)` block in engine
   source (e.g. `FViewUniformShaderParameters` at `SceneView.h:1016` in
   5.4.4).
2. Walk each `SHADER_PARAMETER*` macro, computing the offset per UE's
   alignment rules (see `ShaderParameterMetadataBuilder.h`).
3. Collect every resource-typed member into `Resources[]` in declaration
   order (only `IsShaderParameterTypeForUniformBufferLayout()` types).
4. Compute the hash per the formula above.
5. Emit the JSON.

When unsure of the hash, run the decompiler once with `RURI_SRT_DEBUG=1`
and grep the stderr for `[SRT] LayoutHashes ...` — the value next to the
UB name is the discriminator you need.

## What happens when no file matches

- The decompiler logs a `[EngineUbMetadata] no match for (View, 0x3F8A12C5)`
  warning in verbose mode.
- The shader still decompiles, but `<UBName>_1_m0[N]` arrays stay as
  anonymous flat-vec4 arrays (current behaviour pre-loader).
- Bind-slot resources fall back to typed placeholders
  (`View_SRV45` / `View_Sampler39` / etc.).

This is **never** a hard failure — missing metadata just degrades to the
prior closed-world ceiling, never blocks decompile.
