# Ruri.FModelHook — UE Shader Decompiler progress notes

Living document. Update on every meaningful refactor.

---

## 1. Pipeline shape (current)

Two pipelines; both required, in this order, when FModel exports a `.ushaderbytecode` file:

```
FModel ExportData(entry)  →  hooked by UE_ShaderDecompiler_Hook
  ├── Pass 010 (called directly from hook)
  │     SaveShaderArchive  →  writes .ushaderlib bytes
  ├── ExportPipeline.Run(_exportState)   ← cumulative state across hook fires
  │     Pass 020  ScanMaterialPackages           (cached: runs once total)
  │     Pass 030  ResolveHashedNames             (utility used by Pass 060)
  │     Pass 040  ExtractIoStoreShaderMapHashes  (cached: runs once total)
  │     Pass 050  BuildShaderLibraryMetadata     (per library)
  │     Pass 060  BuildStableShaderRecords       (per library)
  │     Pass 070  WriteAssetInfoSidecar          (per library)
  │     Pass 080  WriteStableInfoSidecar         (per library)
  │     Pass 090  WriteUnifiedMetadataJson       (cached: runs once total)
  └── DecompilePipeline.Run(options)     ← per library, CLI-runnable
        Pass 110  ReadShaderLibrary
        Pass 120  LoadAssetInfoSidecar
        Pass 130  LoadStableInfoSidecar
        Pass 140  LoadUnifiedMetadataIndex
        Pass 150  BuildShaderMapView
        Pass 160  LoadSymbolSources
        Pass 170  BuildShaderLabProperties
        Pass 180  PrepareShaderBinaries          (Phase 1: strip wrapper)
        Pass 190  RunEngineDecompile             (Phase 2: ShaderDecompilerEngine)
        Pass 200  EmitShaderLabFiles             (Phase 3: write .shader)
```

Pass numbers reflect **execution order end-to-end**: 010 < 020 < … < 200.
Export comes before Decompile because Decompile reads sidecars Export
produces. 10-increment gaps leave room to insert without renumbering.

Pass 030 (`Pass030_ResolveHashedNames`) is a stateless CityHash utility
class consumed by Pass 060 — not a sequencing step in the orchestrator.

---

## 2. Folder layout

```
Game/SBUE/ShaderDecompiler/
├── DecompilePipeline.cs        (orchestrator for Pass 110-200)
├── ExportPipeline.cs           (orchestrator for Pass 020-090)
├── ExportPipelineState.cs      (state for export side; persists across hook fires)
├── PipelineState.cs            (state for decompile side; per-library)
├── UE_ShaderDecompiler_Hook.cs (FModel-hook entry point)
└── Passes/
    ├── Pass010_SaveShaderArchive.cs
    ├── Pass020_ScanMaterialPackages.cs
    ├── Pass030_ResolveHashedNames.cs
    ├── Pass040_ExtractIoStoreShaderMapHashes.cs
    ├── Pass050_BuildShaderLibraryMetadata.cs
    ├── Pass060_BuildStableShaderRecords.cs
    ├── Pass070_WriteAssetInfoSidecar.cs
    ├── Pass080_WriteStableInfoSidecar.cs
    ├── Pass090_WriteUnifiedMetadataJson.cs
    ├── Pass110_ReadShaderLibrary.cs
    ├── Pass120_LoadAssetInfoSidecar.cs
    ├── Pass130_LoadStableInfoSidecar.cs
    ├── Pass140_LoadUnifiedMetadataIndex.cs
    ├── Pass150_BuildShaderMapView.cs
    ├── Pass160_LoadSymbolSources.cs            ← still big (~72KB); reader implementations
    ├── Pass170_BuildShaderLabProperties.cs
    ├── Pass180_PrepareShaderBinaries.cs        ← big (~57KB); UE-binary-strip helpers inlined
    ├── Pass190_RunEngineDecompile.cs
    ├── Pass200_EmitShaderLabFiles.cs           (~26KB; shaderlab emission helpers inlined)
    └── UnifiedShaderMetadataDtos.cs            (DTO schemas shared by Pass 020-090)
```

All passes share the parent namespace `Ruri.FModelHook.Game.SBUE.ShaderDecompiler`.

---

## 3. Design rules

- **Self-contained passes.** Each pass owns its own private DTOs / helpers
  and writes to typed slots on `PipelineState` / `ExportPipelineState`.
  Other passes consume those slots; they never call into another pass's
  private helpers.
- **Helper code lives with its only consumer.** Anything used by exactly
  one pass goes inside that pass's file. Cross-pass utilities — currently
  only `Pass030_ResolveHashedNames` and `UnifiedShaderMetadataDtos` —
  live as their own file but are NOT in the orchestrator's pass sequence.
- **10-increment renumbering on insert.** When a pass needs to be split,
  renumber later passes to free a 10-step slot rather than using `Pass025`
  / `Pass2a` style sub-numbers.
- **Pass numbers reflect execution order globally.** Export = 010-090,
  Decompile = 110-200. New steps slot in by inserting at the next free
  10-step boundary (e.g. SubShader-Tags extraction would be Pass 100 or
  Pass 105 if it must run between Export and Decompile).

---

## 4. UE → shaderlab mapping (so far)

### Properties (Pass 170)

Source: `MaterialInterfaces[<x>].LoadedShaderMaps[*].MaterialShaderMapContent.UniformExpressionSet`
in `UnifiedShaderMetadata.json`.

```
UE                                                  shaderlab
─────────────────────────────────────────────────   ─────────────────────────
UniformNumericParameters[i].ParameterType
  Engine/Source/Runtime/Engine/Public/MaterialTypes.h:188-206
    Scalar                                          Float
    Vector            (FLinearColor on the wire)    Color
    DoubleVector                                    Vector  (no 0..1 clamp)
    StaticSwitch                                    [Toggle] _X (..., Float) = 0|1

UniformTextureParameters[Type][i] outer-array index
  Engine/Source/Runtime/Engine/Public/MaterialShared.h:464-475
    0 Standard2D                                    2D         "white" {}
    1 Cube                                          Cube       "" {}
    2 Array2D                                       2DArray    "" {}
    3 ArrayCube                                     CubeArray  "" {}
    4 Volume                                        3D         "" {}
    5 Virtual                                       2D         "white" {}
```

### Variants (Pass 200)

- One Pass per shader-map. Variants stay **inside** that single Pass.
  UE shader-maps don't have a Unity-LIGHTMODE-style splitting axis at
  the cooked level — distinct (ShaderType, VertexFactoryType, Permutation)
  tuples are cells of one multi-compile matrix.
- Variant identifier order:
  1. `VARIANT_<ShaderType>_<VF>_PERM_<n>` when type names recovered
  2. `VARIANT_PERM_<n>` when only permutation id recovered
  3. `VARIANT_H_<shaderHashShort>` when only shader hash recovered
  4. `VARIANT_IDX_<shaderIndex:D6>` final fallback
- `#pragma multi_compile_local <V1> <V2> ...` declares the matrix.
- Each program lives under `#if defined(SHADER_STAGE_X) #if defined(VARIANT_…)`.

### FHashedName equivalence (Pass 030)

`FHashedName::FHashedName(const FName&)`
(`Engine/Source/Runtime/Core/Private/Serialization/MemoryImage.cpp:1159-1214`)
hashes UPPERCASED UTF-8 / ASCII bytes with `CityHash64WithSeed(...,
InternalNumber)`. The C# port wraps every CityHash mul/sub in
`unchecked { }` because `Source/Directory.Build.props` enables
`<CheckForOverflowUnderflow>true</...>` and CityHash relies on natural
ulong wraparound.

Type-name recovery feeds two paths (Pass 060 walks both):
- (a) Hash every `TypeDependencies[*].Name` in the cooked metadata and
  match against `Shaders[*].TypeHash`. Preferred — purely metadata-driven.
- (b) Scan UE source `IMPLEMENT_*_SHADER_TYPE` macros and hash the
  captured names. Fallback for shaders whose owning shader-map has no
  TypeDependencies entry.

---

## 5. State slots

### `ExportPipelineState`  (held statically by `UE_ShaderDecompiler_Hook` so cumulative state survives across hook fires)

| Slot | Filled by | Read by | Notes |
|------|-----------|---------|-------|
| `Vm`, `Entry`, `ExportBasePath` | hook before `ExportPipeline.Run` | every export pass | per-call context |
| `Root.MaterialInterfaces` | Pass 020 | Pass 060, Pass 090 | UMaterial scan; cached |
| `Root.PackageShaderMapHashes` | Pass 040 | Pass 060, Pass 090 | IoStore container hashes; cached |
| `Root.ShaderCodeArchives[lib]` | Pass 050 | Pass 060 | per-library archive view |
| `AssetInfo` | Pass 060 | Pass 070 | current library's `.assetinfo.json` shape |
| `StableInfo` | Pass 060 | Pass 080 | current library's `.stableinfo.json` shape |
| `MaterialsScanned`, `IoStoreHashesExtracted`, `UnifiedMetadataWritten` | their respective passes | gates | one-shot guards |

### `PipelineState`  (per-library; created fresh on each `DecompilePipeline.Run` call)

| Slot | Filled by | Read by | Notes |
|------|-----------|---------|-------|
| `Library` | Pass 110 | 150, 180, 190 | parsed `.ushaderlib` |
| `ShaderMapToAssets` | Pass 120 | 150 | `.assetinfo.json` map-hash → asset list |
| `ShaderHashToAssetsByFreq` | Pass 130 | 150 | `.stableinfo.json` hash-level fan-out |
| `ContainerByShaderIndex`, `ContainersByMapAndIndex` | Pass 130 | 200 | per-shader / per-map container info |
| `HashToMaterialsFromUnified` | Pass 140 | 150 | `UnifiedShaderMetadata.json` bridge |
| `ShaderMaps` | Pass 150 | 170, 180, 200 | per-map view (assets + members) |
| `UsageByShaderIndex`, `NameByShaderIndex` | Pass 150 | 180 | shader → material attribution |
| `UnifiedMaterialReader`, `MaterialJsonSymbolReader` | Pass 160 | 170, 180 | JSON readers |
| `ShaderMapInfo.PropertiesBlock` | Pass 170 | 200 | pre-rendered `Properties { ... }` block |
| `ShaderPrepByIndex` | Pass 180 | 190, 200 | stripped DXBC + EngineDecompileOptions |
| `DecompileResultByIndex` | Pass 190 | 200 | engine.Decompile result per binary |

---

## 6. Test loop

- Build: `dotnet build Source/Ruri.FModelHook -c Debug`
- Run:   `FModel/FModel/bin/Debug/net8.0-windows/win-x64/Ruri.FModelHook.exe --auto-export-cook --shader-only --skip-global --ready-timeout-sec 240`
- Output: `<FModel-bin>/Output/Exports/<Project>/Content/Decompiled/<archive-name>/SM<hash>_<material>.shader`
- `--skip-global` skips engine-only shader archives (3800+ binaries with
  no asset-side data). Keep on for material-iteration loops.

---

## 7. Open work

| Topic | Status | Notes |
|-------|--------|-------|
| `Pass 160 LoadSymbolSources` (~72KB) | DEFER | held by reader-implementation classes (UnifiedMaterialReader, MaterialJsonSymbolReader, SymbolBuilder, etc.) — these are state-cached service objects, not pass logic. Pass 160 entry itself is small (~50 lines). Splitting means moving readers to separate non-pass files; awaiting user direction. |
| `Pass 180 PrepareShaderBinaries` (~57KB) | OK for now | bulk is `UnrealShaderParser` binary-strip + SRT decode — used only by this pass, inline matches the design rule. |
| Pass 190 streaming behavior | TRADE-OFF | current split runs full `engine.Decompile` upfront then emits in Pass 200, losing the previous per-shader-map streaming. Documented in Pass 190 header. Re-introduce streaming only if perf becomes a UX issue. |
| SubShader `Tags { RenderType / Queue }` | TODO | needs material-asset attribute extraction (BlendMode/ShadingModel/MaterialDomain). UE source: `Engine/Source/Runtime/Engine/Classes/Materials/MaterialInterface.h`. New pass would slot in between Pass 020 (scan) and Pass 050 (build library metadata) — e.g. Pass 025, or renumber and use Pass 030 (currently the resolver) → Pass 040. |
| LOD / Pass-level `Tags { LIGHTMODE=… }` | TODO | derive from ShaderType name (e.g. `TBasePassPS…` → ForwardBase) inside Pass 200 emission. |
| Engine UB cbuffer field-name back-fill (`View_m0[42]` → `View_TranslatedWorldToClip`) | DEFER | needs generated data file mapping each engine UB struct's HLSL byte offsets to field names; UE 5.2 source path `Engine/Source/Runtime/Renderer/Public/SceneView.h:858-979` lists the View struct (~280 fields with stereo replication). |

---

## 8. Recent changes

- **Renumbered passes by global execution order.** Export = Pass 010-090,
  Decompile = Pass 110-200. Old Pass 100/110/120 numbering misled
  readers into thinking export ran after decompile.
- **Split `Pass 020 BuildUnifiedShaderMetadata` (1620 lines)** into 7 focused passes
  (020 / 040 / 050 / 060 / 070 / 080 / 090) plus the shared DTO file
  `UnifiedShaderMetadataDtos.cs`. New `ExportPipeline.cs` orchestrator
  + `ExportPipelineState.cs` state class. Gating flags on the state
  ensure cross-library passes (020 / 040 / 090) only run once total.
- **Split `Pass 180 DecompileShaders` (1955 lines)** into Pass 180 PrepareShaderBinaries
  / Pass 190 RunEngineDecompile / Pass 200 EmitShaderLabFiles. Helpers stay inline
  in their owning pass (UnrealShaderParser et al. → 180; HLSL emission
  helpers → 200).
- All pass files moved under `Passes/`. Top-level `Game/SBUE/ShaderDecompiler/`
  now only holds the two orchestrators + state types + hook entry.
- Properties pass renders shaderlab `Properties { ... }` from each
  shader-map's primary material's UniformExpressionSet — verified on
  `MI_GrassLarge02` (18 numeric params + 6 textures populated).
