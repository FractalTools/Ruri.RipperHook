# Shader Symbol Sources — UE 5.4 Cooked-Build Recovery Matrix

> Inventory of every on-disk source of shader parameter symbol names that
> can survive a UE shipping IoStore cook, along with what we currently
> consume vs. what's available but unused.

This is the empirical answer to "what symbols can we recover from a
shipped game?" — surveyed against `E:\UnrealEngine-5.4.4-release` and
cross-checked against the X6Game (Infinity Nikki) cook layout.

---

## ✅ Currently Used

| Source | Location in Cooked Data | Where We Read It | Yields |
| --- | --- | --- | --- |
| **`FShaderCodeUniformBuffers`** (optional-data `'u'` key) | Per-shader optional-data tail in `.ushaderbytecode` shader bodies | [`UnrealShaderParser.ParseOptionalDataFromShaderTail`](UnrealShaderParser.cs) — keys `'p'`/`'r'`/`'u'`/`'n'`/`'x'`/`'v'`/`'m'`/`'6'` | **UB binding names** (e.g. `View`, `Scene`, `Material`, `View_Sampler0`) — the primary binding-level name source. |
| **`FShaderResourceTable`** (optional-data, parallel arrays at top of shader binary) | Per-shader, immediately after the SRT header | [`UnrealShaderParser.Parse`](UnrealShaderParser.cs) reads ResourceTableBits + SRV/Sampler/UAV maps; [`RuntimeSymbolReader.ShaderResourceTableSymbolizer.EnrichSymbolData`](RuntimeSymbolReader.cs) decodes them into typed bindings | **Resource bind indices + (UB→resource) mapping**; combined with `'u'` UB names, gives the patcher enough to label `Material_Texture0` / `View_Sampler1` / etc. |
| **`UMaterial.LoadedMaterialResources[*].LoadedShaderMap.MaterialShaderMapContent.UniformExpressionSet`** (inline shader-map) | UMaterial UAsset — only when `bShareCode == false` (older / non-IoStore cooks) | [`Pass030_ScanMaterialPackages.BuildShaderContent`](Passes/Pass030_ScanMaterialPackages.cs) → `UnifiedUniformExpressionSet` | **Gold standard**: every Material cbuffer member's name + byte offset + type + array size. Empty in modern shipping IoStore cooks. |
| **`UMaterialInterface.CachedExpressionData`** (FStructFallback property bag) | UAsset top level; survives IoStore cook | [`MaterialCachedExpressionReader.Read`](MaterialCachedExpressionReader.cs) — defensive recursive sweep + 8-bucket classifier | **Material parameter NAMES** by kind (Scalar / Vector / Texture / RVT / SVT / Font / StaticSwitch / Unknown). No byte offsets — names get synthesised onto a flat Material cbuffer downstream. |
| **`FIoStoreContainerHeader.StoreEntries[i].ShaderMapHashes`** (per-package) | IoStore container header (`.utoc`) | [`Pass020_ExtractIoStoreShaderMapHashes`](Passes/Pass020_ExtractIoStoreShaderMapHashes.cs) | **Package → shader-map hash** bridge for FMaterialShaderMap-side maps (the IoStore-cooked equivalent of what `LoadedShaderMaps[*].CookedShaderMapIdHash` carries on older cooks). |
| **`FNiagaraShaderMap.ResourceHash`** (FShaderMapBase.ResourceHash from `bShareCode=true`) | UNiagaraScript UAsset → `LoadedScriptResources[i].RenderingThreadShaderMap.ResourceHash` | [`Pass035_ExtractNiagaraShaderMapBridge`](Passes/Pass035_ExtractNiagaraShaderMapBridge.cs) — 8-way parallel walk | **Niagara-side hash → asset path** bridge. Independent from the material ID space; required for archives whose maps come from Niagara compute scripts only. |

## ⚠️ Available in cook but UNUSED

| Source | Location | Why Unused | What It Would Add |
| --- | --- | --- | --- |
| **`FNiagaraShaderScript.SerializedShaderParameters`** + DI param info | After `RenderingThreadShaderMap` in cooked UNiagaraScript serialization | CUE4Parse's `FNiagaraShaderScript` deserializer stops at `RenderingThreadShaderMap`; doesn't read the trailing `LooseMetadataNames[]`/`ExternalConstants[]`/`DataInterfaceParamInfo[]` | **Niagara cbuffer member names** + DI HLSL symbol prefixes (`<DISymbol>_<Function>`) — this is what actually lets Niagara compute shaders' `Material_m0[N]` get named members instead of anonymous slots. **Largest single remaining win.** |
| **`FShaderParametersMetadata` (MemoryImage)** | Frozen-MemoryImage blob inside `.ushaderbytecode` shader bodies, on shaders compiled with `BEGIN_SHADER_PARAMETER_STRUCT` | Requires walking the MemoryImage frozen-archive format (FShaderParametersMetadata.Members[].Name/Offset/BaseType/NumElements) — non-trivial but possible via CUE4Parse's existing `FMemoryImageArchive` infra | **Engine-internal cbuffer member layouts** for shaders defined with `SHADER_PARAMETER_STRUCT()`. Would unlock named members for Material / View / Scene / LocalVF / RootShaderParameters cbuffers across all shaders, not just material-bound ones. |
| **`FShaderParameterMapInfo` per-shader** | `FShader.ParameterMapInfo` MemoryImage payload | We already partially extract this via Pass030's `BuildShaderParameterMapInfo`, but only carry the binding indices forward — the resolved names would have to come from cross-referencing UB names | Complementary index-side info; alone gives only `BaseIndex`/`Size` — needs name source to be useful. |

## ❌ NOT available in shipping cook (don't bother)

| Source | Why Stripped |
| --- | --- |
| **`.shk` (ShaderStableInfo) files** | Not present in X6Game's cook (verified via filesystem scan); typically only present in PGO / shipping-with-pipeline-cache builds. When present, would directly bridge shader-output-hash → material asset path. |
| **`.upipelinecache`** | Same — not in X6Game. Even when present, only carries shader hashes + render state, no binding names. |
| **`UMaterialInterfaceEditorOnlyData`** | Editor-only; stripped at cook (`WITH_EDITORONLY_DATA`). |
| **`FShaderType` registration metadata (`IMPLEMENT_SHADER_TYPE` macros)** | Compile-time C++ introspection; runtime reflection stripped at cook. Only lives in the engine binary. |
| **`UNiagaraScript.GeneratedHlslSource`** / Niagara compile results | Editor-only. |
| **`FRDGTextureExtractedDescription`** (Render Dependency Graph binding metadata) | Frame-transient; never serialised. |

---

## Field-by-field accuracy ranking

For a Material cbuffer `cbuffer type_Material { float4 Material_m0[N]; }` where the patcher needs to label members:

1. **`UniformExpressionSet.UniformNumericParameters[*].ParameterInfo.Name`** — **PERFECT** (name + byte offset + type). Only available when inline shader-map survived.
2. **`CachedParameters.ScalarNames` / `.VectorNames` / etc.** — **NAMES only** (no offset). Patcher uses synthetic byte offsets, accuracy depends on the order matching the cook's compile order.
3. **`FShaderCodeUniformBuffers` `'u'` key + `FShaderResourceTable`** — **PERFECT for cbuffer-level names** (`Material`, `View`, `Scene`) and per-resource binding (`Material_Texture0`). Doesn't carry CB MEMBER names.
4. **(unused) FShaderParametersMetadata MemoryImage** — would be **PERFECT for engine-cbuffer member names** (e.g., `View.WorldToClip`, `Scene.SunLight`).
5. **(unused) FNiagaraShaderScript.SerializedShaderParameters** — would be **PERFECT for Niagara cbuffer member names**.

---

## Recommended next steps (priority order)

1. **Extend CUE4Parse's `FNiagaraShaderScript`** to read the trailing
   `SerializedShaderParameters` (LooseMetadataNames + ExternalConstants + DataInterfaceParamInfo arrays). About 30 lines of CUE4Parse code; unlocks named cbuffer members in the Niagara compute shader output that currently land as anonymous `Material_m0[N]`.

2. **MemoryImage `FShaderParametersMetadata` reader**. Bigger lift — needs to walk the frozen-archive byte layout for `FShaderParametersMetadata.Members[]`. Would unlock View/Scene/Material engine-cbuffer member names across the entire decompiled corpus.

3. (Wishlist) `.shk` reader for cooks that include them. X6Game doesn't ship them so skip for this project, but the reader would benefit any project where the publisher kept pipeline-cache assets.
