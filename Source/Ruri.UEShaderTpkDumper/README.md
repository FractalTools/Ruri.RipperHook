# Ruri.UEShaderTpkDumper

C# port of the Python `EngineUbMetadata/_generator/gen_ub_metadata.py`
that drives uniform-buffer layout extraction directly from UE source.
Independent project — does NOT depend on Ruri.FModelHook so it can be
run as a one-shot CLI per engine version.

## Run

```powershell
# Default: scan D:\GameStudy\UE\* for first-level subdirs whose names
# contain a <X.Y.Z> version, emit per-version JSONs to the committed
# EngineUbMetadata folder.
dotnet run --project Source/Ruri.UEShaderTpkDumper

# Limit to one engine
dotnet run --project Source/Ruri.UEShaderTpkDumper -- --filter "5\.1\.1"

# Just discover, don't write
dotnet run --project Source/Ruri.UEShaderTpkDumper -- --list

# Custom roots
dotnet run --project Source/Ruri.UEShaderTpkDumper -- \
    --ue-root "D:\custom\path" \
    --out-root "C:\custom\out"
```

## Source layout

Auto-discovery treats every first-level subdir of `--ue-root` whose
folder name contains a `<X>.<Y>(.<Z>)?` version as an engine. The
default convention is the raw `UnrealEngine-<X.Y.Z>-release` names UE
ships under — no renaming required. Inside each, `Engine/Source/{Runtime,Developer,Editor,Plugins}`
are walked for `*.h` / `*.cpp` / `*.inl`.

## What it extracts

* **Uniform-buffer layouts**: BEGIN_UNIFORM_BUFFER_STRUCT[_WITH_CONSTRUCTOR],
  BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT[_WITH_CONSTRUCTOR], and
  BEGIN_SHADER_PARAMETER_STRUCT.
* **Layout hashes**: byte-identical to
  `FRHIUniformBufferLayoutInitializer::ComputeHash`. Picks the right
  UE 5.0-5.4 vs UE 5.5+ EUniformBufferBaseType integer mapping per
  engine version.
* **IMPLEMENT_*_STRUCT scan**: recovers the shader-side cbuffer
  binding name (`"View"`, `"Material"`, …) + BindingFlags + StaticSlot
  bit. All three feed the layout hash, so this is what unlocks
  runtime symbol recovery.
* **Macro-table expansion**: `VIEW_UNIFORM_BUFFER_MEMBER_TABLE` etc.
  are recursively substituted into struct bodies before the layout
  walker sees them.

## Schema

Output mirrors the existing Python generator's schema byte-for-byte
(the runtime decompiler reads via `System.Text.Json` with
`PropertyNameCaseInsensitive=true`):

```
<out>/<X.Y.Z>/<BindingName>_<LayoutHash:X8>_MetaData.json
```

## Known gaps (vs Python parity)

Tracking on the Stage-38 + Stage-39 commits. UE 5.1.1 smoke test:
138/141 UBs found, 112 exact hash matches.

| Gap                                                | Status |
| --------                                           | ------ |
| BEGIN_*_STRUCT block enum                          | done   |
| IMPLEMENT_*_STRUCT scan (binding name + flags)     | done   |
| UE 5.0-5.4 / 5.5+ UBMT integer mapping             | done   |
| Macro-table expansion (single level)               | done   |
| Macro-table expansion (deeper levels, View etc.)   | partial — 6/11 tables found vs Python's 11 |
| SHADER_PARAMETER_STRUCT_INCLUDE recursive walk     | partial — recursion in place but registry lookup occasionally misses nested children |
| ShaderType seed (LAYOUT_FIELD scan)                | pending |
| Hash-to-name index emission                        | pending |
| VertexFactoryType / ShaderPipelineType indexes     | pending |
| USF-level loose-param scan (`_Globals` recovery)   | pending |
| Runtime exact-version lookup (replace GAME_UE5_X)  | pending |

## Why this isn't done overnight

The Python generator is ~3300 lines. The faithful port preserves
every macro-handling subtlety the Python accumulated over 30+ stages
of UE-quirks debugging. Tonight's commit lands the structural
foundation (parser, layout walker, hash math, IMPLEMENT scan, macro
tables) and proves end-to-end hash parity for the simple-shape UBs
(112 out of 138). The deeper gaps (nested struct includes, multi-
level macro tables, shader-type seed scan) are mechanical follow-up
ports — straightforward to add incrementally now that the spine is
in place.
