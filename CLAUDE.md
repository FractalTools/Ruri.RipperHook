# Ruri-RipperHook — Working Brief

Entry point every iteration. Read this before touching anything else.

When updating documentation, summaries, or external-facing descriptions, do not include any description of the private `AssetRipperGameHook` game decryption portion. This project should be described as a normal, legitimate unpacking / export / data-processing tool.

---

## 0. Engineering doctrine — top-tier or nothing

The universal core of the Ruri optimization contract, specialized to this repo. §1 is the mechanical "what not to touch"; §0 is the quality bar everything must clear: **every feature is implemented with the best-known algorithm, and always designed as an extension point in a globally-optimal framework.** If a change must deviate from this — or you find the doctrine itself is wrong — fix this section first, then write the code.

### A. Attitude — no degradation, ever

- **Top-tier algorithm, always.** Build at the current best-known solution for the problem. No "good enough for now" placeholder you plan to upgrade later. Hit a bad design → propose the better one and refactor; don't pile onto it.
- **Never simplify a specified design.** Complexity is not a license to cut scope. If the target is genuinely large, stage it as explicit phased TODOs — but each landed phase ships full-strength, no compromise version.
- **Lossless refactor.** Propose a warranted refactor immediately; the new code must be behaviour-equivalent and **must not regress performance**. Equivalence by construction, not by hope.
- **Don't litigate effort.** Default to the no-compromise build; don't ask scope/effort tradeoff questions.

### B. 1:1 port discipline — overrides everything else while porting

> A published, correct, runnable reference is ground truth. A faithful 1:1 port of it is correct *by construction*.

- **The only design is the reference's.** "Port from X" forbids simplify / invent / compromise / "working version first". Copy the algorithm, data structures, concurrency model, bit widths, branch boundaries, constants, and magic numbers line-for-line.
- **Read the real source first** — never port from memory or a paraphrase. Go method-by-method, field-by-field, `if`-by-`if`, loop-by-loop; `<` vs `<=` is load-bearing, copy it exactly.
- **Host-language synonym substitution is not deviation.** Re-expressing an idiom in the host object model (AssetStudio `AnimationClip` → AssetRipper `IAnimationClip`; a native SWIG call → its C# binding equivalent) is expected; the *logic* — control flow, ordering, constants, bit offsets — stays byte-identical. Only genuine I/O adaptation (how data is fed in/out) may change, annotated with the source `file:line`.
- **No oracle tests for a faithful port.** A zero-logic-change replica of a verified reference cannot hold an algorithm bug; confirm it compiles and runs. Test only the parts that *intentionally* deviate once the user later asks for new behaviour.
- **No "spirit-port".** Either it is 1:1, or it is an explicit unimplemented TODO — never a quietly simplified stand-in. Report as "1:1 ported, source = `file:line`" so it is git-blame auditable.

### C. Extensibility — design the framework, not the case

- **Build the extension point, not the special case.** Every feature is one member of a family; design for the family. New game / format / exporter support must drop in without editing shared code.
- **No hardcoded branches in shared paths.** `if (game == X)` / `if (format == Y)` buried in shared code is a design smell. Dispatch through data — a registry, a delegate list, an attribute-discovered handler. This is §1's AOP-only rule generalized; the canonical seams here are `ExportHandlerHook.CustomAssetProcessors` and `RegisterModule(...)` (FRAMEWORK.md §6). Adding a case = adding a registration, never editing the dispatcher.
- **Zero-variant dispatch.** One data-branched path beats N compile-time-forked copies — fewer copies, fewer places a fix gets forgotten.
- **Frozen upstream is sacred.** Behaviour over AssetRipper / submodules is added by hooks/modules only (§1), never by editing the frozen tree. Don't-touch and design-for-extension are the same coin.

### D. Code style — language-agnostic core (this repo writes English)

- **No abbreviations** in names — full words (`Animator` not `Anim`, `Skeleton` not `Skel`).
- **One cohesive unit per file** — don't stack unrelated classes/enums in one file (a type plus its tightly-bound helper/enum counts as one unit).
- **No single-line stacking** — don't crush multi-field structs or multi-statement bodies onto one line; reserve `=>` for genuinely trivial one-shots.
- **Match the surrounding file** — comments and logs follow the file's existing language (this repo = English; commit style per §1). Fix non-conforming names in code you already touch; don't bulk-rewrite for style alone.
- **Logs go through the project logger** with an explicit category (FRAMEWORK.md §9), not ad-hoc `Console.WriteLine`.

### E. Performance kernel

- **Measure, don't guess.** Optimize from real timing / profiler data, never from intuition or a hand-rolled per-call sampler.
- **Zero waste in hot loops.** No avoidable allocation / LINQ / string formatting on a per-item hot path; cache and reuse. Parallelize independent work, serialize only what shares non-thread-safe state (e.g. the per-decompile lock in FRAMEWORK.md §11).
- **Optimize spikes, not averages.** A p99 stall outweighs a good mean.

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
| **Never build `Ruri.SourceGenerated`** | It's a `<Reference HintPath>` to a prebuilt DLL (regenerated only by `Ruri.AssemblyDumper` pipeline). Building the slnx triggers it and burns minutes. Use `dotnet build Source/Ruri.<X>/Ruri.<X>.csproj -c Debug --nologo` for everything else. |
| **Commit at milestones** | When a logically-complete chunk of work lands (a hook is wired and builds clean, a UI feature is plumbed end-to-end, a bug is fixed and tested, a docs section is added), commit it locally without being asked. **Local only — never push.** Stage just the relevant files (`git add path/...`, not `-A`/`.`), no Co-Authored-By trailer. If the change touches a submodule (anything under `Source/Ruri.ShaderDecompiler` etc.), commit IN the submodule first; the parent's submodule-pointer bump is the user's call. Don't commit speculative WIP, broken builds, or trivial reverts. **Message style depends on what changed:** code → one short English line matching existing log style (e.g. `flip SplitVariantsToHlslFiles default to false`, `delete redundant BundledAssetsExportMode hook`); **`.md` / docs → multi-line body that names which sections were added/restructured and the *reason* (the structural / behavioural shift, not the literal text edits) — e.g. `add §7 AR_* hook vs native setting policy + flag when to delete a hook because the native default already covers it`. Skip prose-level diffs; capture intent in 2–4 lines max.** |

---

## 2. Framework reference

Hooks, AR pipeline, path handling, source-generated lookup, custom processor injection, logger sinks → **[FRAMEWORK.md](FRAMEWORK.md)**. Read that before writing or debugging hook code, not this file.
