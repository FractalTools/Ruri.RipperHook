# Ruri-RipperHook — Working Brief

Entry point every iteration. Read this before touching anything else.

When updating documentation, summaries, or external-facing descriptions, do not include any description of the private `AssetRipperGameHook` game decryption portion. This project should be described as a normal, legitimate unpacking / export / data-processing tool.

---

## 1. Hard rules (do not violate)

| Rule | Detail |
|---|---|
| Editable area | `Source/Ruri.*/**` (Ruri.RipperHook, Ruri.AssemblyDumper, Ruri.Hook, Ruri.SourceGenerated, Ruri.ShaderDecompiler, …). |
| Frozen area | `AssetRipper/**` and all submodules. |
| Temporary probing | OK to edit a submodule transiently to confirm "which method is the right hook target", **then `git checkout` it back to upstream**. The final implementation must live in `Source/Ruri.*/**` as Ruri.Hook attribute hooks. |
| AOP only | Game-specific behavior is added via `[RipperHook(GameType.X, version)]` classes (or equivalent for non-game tools) that install method hooks. Do **not** subclass / monkey-patch base types in submodules, do **not** embed `if (game == X)` branches in shared code, do **not** ProjectReference-and-edit upstream. |
| **Hooks via Ruri.Hook only** | Every `Source/Ruri.*` project MUST install method hooks via the `Ruri.Hook` framework — `[RetargetMethod]`, `[RetargetMethodFunc]`, `[RetargetMethodCtorFunc]` attributes on a class derived from `RuriHook`, with `Initialize()` invoked at startup. Do **not** call `new MonoMod.RuntimeDetour.Hook(target, detour)` / `new ILHook(target, manipulator)` directly — go through Ruri.Hook so attribute-based discovery, hook registration, and cleanup stay consistent. The only place raw MonoMod is acceptable is inside `Ruri.Hook` itself (`ReflectionExtensions.RetargetCall*`). |
| Reference exemplar | `Source\Ruri.RipperHook\AssetRipperHook` (game hooks) and `Source\Ruri.AssemblyDumper\Pipeline\ArAssemblyDumperHook.cs` (build-time hooks) show the canonical Ruri.Hook attribute pattern (`AddMethodHook`, `[RetargetMethod]`, `[RetargetMethodCtorFunc]`). |
| Engine-wide hook install | Per-engine cross-version setup goes in the *Common* hook class's `InitAttributeHook`, not per-version. EndField installs its shader-binding post-processor in `EndFieldCommon_Hook.InitAttributeHook`; `EndFieldShaderBindingHook.Install()` is idempotent so re-entry is harmless across the 5 versions. |
| Test loop output | Always export to `TestLoopOutput/` at the workspace root. The CLI auto-clears that directory each run — do not stage extra folders. Kill any stale `Ruri.RipperHook.CLI.exe` before launching a new run. |
| Iteration timeouts | Long-running runs go via `run_in_background` + `Monitor` until-loop. Don't chain short sleeps to bypass the deadlock guard; pick one budget that fails the run loudly when exceeded. |
