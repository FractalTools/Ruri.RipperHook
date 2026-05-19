# EngineUbMetadata Generator

Single-file Python generator that walks UE engine source headers,
re-implements `FShaderParametersMetadata::InitializeLayout` +
`FRHIUniformBufferLayoutInitializer::ComputeHash`, and emits one
`<UBName>_<LayoutHash:08X>_MetaData.json` per uniform buffer.

**No C++ compile step. No AI lookup.** Just regex + a tiny
preprocessor for `#define` constants and macro tables
(`VIEW_UNIFORM_BUFFER_MEMBER_TABLE` and friends).

## Run

```powershell
# UE 5.1 (FModel EGame name = GAME_UE5_1)
python gen_ub_metadata.py `
    --engine-src "D:\GameStudy\UnrealEngine-5.1.1-release" `
    --engine-version 5.1.1 `
    --out-dir   "..\" `
    --target-folder GAME_UE5_1

# UE 5.4 (FModel EGame name = GAME_UE5_4)
python gen_ub_metadata.py `
    --engine-src "D:\GameStudy\UnrealEngine-5.4.4-release" `
    --engine-version 5.4.4 `
    --out-dir   "..\" `
    --target-folder GAME_UE5_4

# Validate-only (no writes; compares hash + resource count vs existing seeds)
python gen_ub_metadata.py `
    --engine-src "D:\GameStudy\UnrealEngine-5.1.1-release" `
    --engine-version 5.1.1 `
    --validate "..\GAME_UE5_1"

# List only (discover which UBs the engine declares; no JSON written)
python gen_ub_metadata.py --engine-src ... --engine-version ... --list-only

# Single UB by name regex
python gen_ub_metadata.py --engine-src ... --engine-version ... --list-only `
    --ub-filter "^(View|LocalVF|LumenCardScene)$"

# Re-emit all 8 versions (UE 5.0-5.7) in one shot
python regen_all.py
```

The loader keys on `(UBName, LayoutHash)` so re-running on top of
existing files never overwrites anything except files whose hash you
re-compute exactly. Mismatched hashes produce a new file under the
same `UBName` -- both coexist; the runtime picks whichever matches
the cook's `ResourceTableLayoutHashes[i]`.

## What it covers

Confirmed against `UE_SYMBOL_SOURCES.md` §6 + the cook-verified seed
JSONs:

- `BEGIN_UNIFORM_BUFFER_STRUCT[_WITH_CONSTRUCTOR]` and
  `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT[_WITH_CONSTRUCTOR]` blocks.
- `SHADER_PARAMETER`, `_EX`, `_ARRAY`, `_ARRAY_EX`, `_SCALAR_ARRAY`
  (with `FUintVector4` / `FIntVector4` / `FVector4f` packing).
- All resource macros: `_TEXTURE`, `_TEXTURE_ARRAY`, `_SRV`,
  `_SRV_ARRAY`, `_UAV`, `_UAV_ARRAY`, `_SAMPLER`, `_SAMPLER_ARRAY`,
  `_RDG_TEXTURE[_ARRAY]`, `_RDG_TEXTURE_SRV[_ARRAY]`,
  `_RDG_TEXTURE_UAV[_ARRAY]`, `_RDG_BUFFER_SRV[_ARRAY]`,
  `_RDG_BUFFER_UAV[_ARRAY]`, `_RDG_UNIFORM_BUFFER`.
- `SHADER_PARAMETER_STRUCT` (nested) and `SHADER_PARAMETER_STRUCT_INCLUDE`
  (recursively expanded across files).
- Binding-flag bits + static-slot bit pulled from the
  `IMPLEMENT_*_STRUCT` macro in the .cpp (Shader / Static /
  StaticAndShader). These bits XOR into hash bit 8 and bit 0
  respectively, so reading the wrong one quietly drifts the hash.
- Macro tables like `VIEW_UNIFORM_BUFFER_MEMBER_TABLE` (and the
  alias macros `VIEW_UNIFORM_BUFFER_MEMBER` /
  `VIEW_UNIFORM_BUFFER_MEMBER_EX` /
  `VIEW_UNIFORM_BUFFER_MEMBER_ARRAY`) -- expanded textually before
  parsing.
- Constants in array dims (`[TVC_MAX]`,
  `[GlobalDistanceField::MaxClipmaps]`,
  `[FCustomPrimitiveData::NumCustomPrimitiveDataFloat4s]`, etc.) --
  resolved from a sweep of `#define`s, enum members, and
  `constexpr int Foo = N;` in namespace scope.

## Validation summary (committed seed JSONs)

| Engine    | Seeds | Hash match | Notes                                |
| ---       | ---:  | ---:       | ---                                  |
| UE 5.1.1  | 41    | 38         | 3 nested-struct heavy passes diverge |
| UE 5.4.4  | 14    | 1          | 5.4 layouts differ -- see below      |

UE 5.4 -- the existing seed JSONs were hand-authored and not
cook-verified for most layouts. The generator's hashes are
recomputed directly from source via the same `ComputeHash` formula
the engine runs, so they should be the actual cook hashes for an
unmodified 5.4.4 build. **If a layout doesn't match what the
decompiler observes, just regenerate**; the runtime is hash-keyed,
not name-keyed, so wrong files are silently ignored.

## What it doesn't cover

- Plugins outside `Engine/Plugins`. (Project-specific UBs go in
  `EngineUbMetadata/<EGame>/` as hand-authored overrides.)
- `SHADER_PARAMETER_STRUCT_REF` (referenced UBs) -- these are
  resources that point at other UBs, not data the layout walker
  expands. Marked `UBMT_REFERENCED_STRUCT` in the resources list.
- Custom forks of UE with extra resource types beyond
  `EUniformBufferBaseType_Num=23`.
- `RENDER_TARGET_BINDING_SLOTS` -- skipped (ShaderParameterStruct only,
  not legal inside `BEGIN_UNIFORM_BUFFER_STRUCT`).

## Files

- `gen_ub_metadata.py` -- the generator.
- `README.md` -- this file.

No build step. Drop-in Python 3.10+.
