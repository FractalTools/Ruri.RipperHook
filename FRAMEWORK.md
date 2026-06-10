# Ruri-RipperHook — Framework Reference

Concrete facts about Ruri.Hook + AssetRipper internals. Read this whenever you need to write a hook, trace a pipeline, or fix a regression. Hard rules / restrictions live in [CLAUDE.md](CLAUDE.md).

> **Maintenance directive for future AI sessions** — this file is a snapshot, not gospel. AssetRipper (frozen submodule) updates land regularly and Ruri.Hook itself evolves. When you investigate something and find:
> - **A claim here that's now wrong** (renamed type, removed method, different default, changed pipeline order, new processor inserted, etc.) — **fix the claim in this file in the same edit**. Don't leave stale facts to bite the next session.
> - **A cleaner/canonical way** to do something this file currently describes as a workaround (e.g. an official AR API now exists for what we hook around) — **replace the workaround section with the better approach**, and delete the old "dumb" path. Don't preserve both for sentimental reasons.
> - **A new gotcha that cost you debugging time** — add it. The "Inheritance discovery gotcha" + "postfix-continue misfire" entries below were each ≥1hr of pain; documenting them once saves the next session the same time.
>
> Treat this file as living code review of the framework. Wrong + outdated docs are worse than no docs.

---

## 1. Build / run quick reference

| Need | Command |
|---|---|
| Compile a single Ruri project | `dotnet build Source/Ruri.RipperHook/Ruri.RipperHook.csproj -c Debug --nologo` |
| CLI exe path | `AssetRipper/Source/0Bins/AssetRipper/Debug/Ruri.RipperHook.CLI.exe` |
| GUI exe path | same dir, `Ruri.RipperHook.GUI.exe` / `Ruri.FModelHook.GUI.exe` |
| List hooks | `Ruri.RipperHook.CLI.exe --list-hooks` (returns JSON, exit 3) |
| Headless export | `--hook <Id> --load <path> --export <dir> [--fail-fast false] [--log-level Info]` (CLI deletes `--export` dir before writing) |
| GUI locking DLL | `Get-Process Ruri.RipperHook.GUI -EA SilentlyContinue \| Stop-Process -Force` — only when copy fails, never speculative |
| GameType definition | `Source/Ruri.RipperHook/Core/GameType.cs` — new AR_/game hook needs entry here |

---

## 2. Ruri.Hook attribute cheat-sheet (`Source/Ruri.Hook/Attributes/*`)

| Attribute | Use |
|---|---|
| `[RetargetMethod(typeof(T), name, isBefore, isReturn)]` | IL injection on `T.name`. `isBefore=true,isReturn=true` = prefix-replace (skip original). `isBefore=true,isReturn=false` = prefix-continue. `isBefore=false,isReturn=false` = postfix-continue — **don't trust on instance `void` methods with a single Ret; the `while(TryGotoNext(Before, Ret))` loop has misfired empirically. Use prefix-continue instead when behaviour-equivalent.** |
| `[RetargetMethodCtorFunc(typeof(T))]` | Patch parameterless ctor. Method signature: `static bool Foo(ILContext il)`. Used to mutate default field values, e.g. AR_BundledAssetsExportMode_Hook sets ProcessingSettings.BundledAssetsExportMode=2 before the final Ret. |
| `[RetargetMethodFunc(typeof(T), name)]` | Full IL manipulator. Method signature: `static bool Foo(ILContext il)`. Use for complex IL rewrites. |

**Inheritance discovery gotcha** (cost half a session): `Registry.ApplyTypeHooks(GetType())` calls `Type.GetMethods(BindingFlags.Public|NonPublic|Instance|Static)`. Without `BindingFlags.FlattenHierarchy`, **inherited `public static` methods are NOT returned**. So `[RetargetMethod]` on a base/common partial class won't be picked up by the derived versioned hook unless you explicitly do `Registry.ApplyTypeHooks(typeof(MyCommon_Hook));` in the versioned hook's `InitAttributeHook()`. Reference: `Arknights_2_7_31_Hook.InitAttributeHook`.

**Hook lifecycle**: `Bootstrap.ApplyHooks(config)` → `RuriHook.ApplyHooks` iterates available `[GameHookAttribute]` types, instantiates each matching `config.EnabledHooks`, calls `Initialize()` → `RipperHookCommon.Initialize` calls `InitAttributeHook()` then registers with `RuriRuntimeHook`.

---

## 3. AssetRipper data flow (frozen, just-the-pipeline)

```
SchemeReader.LoadFile(path)
  → FileBase (FileStreamBundleFile / SerializedFile / ResourceFile / ...) with FilePath set by Scheme<T>.Read
  → for bundles, ReadFileStreamData adds ResourceFiles with FilePath=bundle.FilePath, Name=entry.Path (CAB)
  → FileContainer.ReadContents promotes ResourceFile → SerializedFile via SchemeReader.ReadFile, preserving FilePath
GameBundle.FromPaths
  → loops FileBase stack: SerializedBundle.FromFileContainer(container, factory, defaultVersion) per FileContainer
    → bundle.AddCollectionFromSerializedFile(file, factory) per SerializedFile (drops container.FilePath at this seam)
      → SerializedAssetCollection.FromSerializedFile(bundle, file, factory) constructs collection (sets Name=file.NameFixed
        but never sets collection.FilePath — hook ReadData postfix to propagate)
        → ReadData(collection, file, factory) iterates file.Objects (ObjectInfo[]: FileID, byteSize, ObjectData byte[], Type)
          → factory.ReadAsset(assetInfo, objectInfo.ObjectData, type) → IUnityObjectBase
          → collection.AddAsset(asset)
ExportHandler.Process(gameData) (called after Load)
  → foreach IAssetProcessor in GetProcessors(): processor.Process(gameData)
    SceneDefinitionProcessor → OriginalPathProcessor → MainAssetProcessor → AnimatorControllerProcessor
    → AudioMixerProcessor → EditorFormatProcessor → LightingDataProcessor → PrefabProcessor
    → SpriteProcessor → ScriptableObjectProcessor
ExportHandler.Export(gameData, outputPath) — uses ExportCollections, writes YAML / textures / etc.
```

**Crucial dead-ends**:
- `ObjectInfo.ObjectData` (raw on-disk bytes) and `byteSize` are GC'd after load — only available via a hook on `SerializedAssetCollection.ReadData` postfix. AR does **not** preserve them anywhere on the asset / collection.
- `AssetCollection.FilePath` is settable but AR never sets it. Hook `ReadData` postfix to do `collection.FilePath = file.FilePath`.
- YAML output works (walker over parsed in-memory tree); binary `asset.Write(AssetWriter)` works for source-generated classes (Pass101 generates real serialisation) but is slow for size/hex use cases. Don't call it during BuildAssetList.
- Bundle rebuild (`.bundle` → modify → `.bundle`) **not supported**: `FileStreamBundleFile.WriteFileStreamData` throws `NotImplementedException`; `ArchiveBundleFile.Write` throws `NotSupportedException`; no YAML reader exists. `AssetsTools.NET` (already PackageReference'd in `Ruri.RipperHook.csproj`) is the right tool for that scenario, not AR.

---

## 4. Path / OriginalPath behaviour

- `IUnityObjectBase.OriginalPath` setter stores raw fullPath; `OriginalDirectory`/`OriginalName`/`OriginalExtension` derived via `Path.*` (Windows: backslashes on the derived fields, **forward slashes preserved in the stored fullPath** — getters return the stored value, so display stays clean).
- `GetBestDirectory()` priority: `OverrideDirectory > OriginalDirectory > "Assets/{ClassName}"`.
- `OriginalPathProcessor.Process(GameData)` iterates each `IAssetBundle.Container` (`AccessDictionaryBase<Utf8String, IAssetInfo>`) and sets `OriginalPath` for entries with `Asset.FileID == 0` (i.e. local assets).
- `BundledAssetsExportMode` (default **DirectExport** in current AR): DirectExport just prepends `Assets/` via `OriginalPathHelper.EnsureStartsWithAssets`, slashes preserved. GroupByBundleName does `Path.Join("Assets/AssetBundles/<bundle>", assetPath)` → backslashes.

**Synthesizing Container entries** (used by Arknights path-repair, `Source/Ruri.RipperHook/AssetRipperGameHook/UnityHypergryph/Arknights/CommonHook/`):
```csharp
AccessPairBase<Utf8String, IAssetInfo> pair = bundle.Container.AddNew();
pair.Key = new Utf8String(myForwardSlashPath);
pair.Value.Asset.SetAsset(bundle.Collection, asset as IObject);  // SetAsset requires IObject (not IUnityObjectBase)
```
Hook `OriginalPathProcessor.Process` as **prefix-continue** so AR's own Process consumes our entries via the standard pipeline. Skip assets already in Container, skip `IAssetBundle` itself, skip non-`IObject`.

---

## 5. Source-generated reference

`Ruri.SourceGenerated.dll` is HintPath'd from `Source/Ruri.RipperHook/Libraries/`. Generated by `Ruri.AssemblyDumper` pipeline.

**Read-only source mirror** for grep / reference: `D:\Ruri\Git\FractalTools\AssemblyDumper\AssetRipper\SourceGenerated\` — class interfaces at `Classes/ClassID_<N>/I<Class>.cs`, generated impls at `<Class>_<version>.cs`. Use this for verifying method signatures (e.g. `IMonoBehaviour.GameObjectP`, `IAssetBundle.Container`).

---

## 6. Custom IAssetProcessor injection (used by AR_PrefabOutlining, AR_StaticMeshSeparation)

`Source/Ruri.RipperHook/Utils/Hook/ExportHandlerHook.cs` is a module that hooks `ExportHandler.Process` and replaces the whole pipeline with a re-implementation of `GetProcessors()` plus a custom-insertion point between `EditorFormatProcessor` and `LightingDataProcessor`:

```csharp
public delegate IEnumerable<IAssetProcessor> AssetProcessorDelegate(FullConfiguration Settings);
public static List<AssetProcessorDelegate> CustomAssetProcessors = new();
```

To add a processor: in your `[RipperHook]` class's `InitAttributeHook`:
```csharp
RegisterModule(new ExportHandlerHook());
ExportHandlerHook.CustomAssetProcessors.Add(MyDelegate);
```
where `MyDelegate(FullConfiguration s) => /* yield return */`.

**Caveats**:
- `ExportHandlerHook.GetProcessors()` is a hand-mirrored copy of AR's `ExportHandler.GetProcessors()`. **Currently missing `OriginalPathProcessor`** — if your hook needs OriginalPath populated, either add it back to the mirror or don't rely on it under these hooks.
- Multiple Ruri hooks registering `ExportHandlerHook` is fine (the module's `OnApply` is idempotent because it's the same static delegate list).

---

## 7. AR_* hook vs native setting policy

**Rule**: AR_* hook IDs are reserved for *extensions over what AR natively supports*. If a feature already exists as a native `ProcessingSettings` / `ExportSettings` / `ImportSettings` property with a sensible default, **don't ship a parallel AR_* hook** — the duplication just confuses configuration.

- AR_* hooks that survive as hook implementations: `AR_SkipStreamingAssetsCopy`, `AR_SkipProcessingAnimation`, `AR_ShaderDecompiler` (custom decompiler), `AR_PrefabOutlining` (restored deleted processor), `AR_StaticMeshSeparation` (restored deleted processor), `AR_Il2CppMethodDump` (IL2CPP native method-body disassembly — AR has no native-asm export at all; see §11).
- AR_* hooks **deleted because the native setting + default is enough**: `AR_BundledAssetsExportMode` (`ProcessingSettings.BundledAssetsExportMode` defaults to `DirectExport` already).

**GUI surfacing**:
- Game hooks (Arknights, EndField, GirlsFrontline2, …) → Hooks tree (mutually exclusive per game, picks one version).
- AR_* hooks → Settings dialog "Features" group. Toggled by checkbox, stored in the same `HookConfig.EnabledHooks` set, just hidden from the tree.
- First-run defaults (e.g. `AR_SkipStreamingAssetsCopy_` on): seeded in `Ruri.RipperHook.GUI/Program.cs:Main` gated on `!File.Exists(configPath)`. After the user saves once via Settings the choice is theirs forever.

**When adding a new AR_* hook**: before writing code, grep `AssetRipper.Processing.Configuration.ProcessingSettings` / `AssetRipper.Export.Configuration.ExportSettings` / `AssetRipper.Import.Configuration.ImportSettings` for an equivalent property. If one exists with the desired default, just flip the default via `[RetargetMethodCtorFunc]` on the settings class (see how `BundledAssetsExportMode` *used* to do it) — or, better, expose it as a Settings dialog control and call it done. Reserve a new hook ID only for behaviour AR has no native knob for.

---

## 8. Restored-from-old-AR code danger list

When user restores deleted AR APIs (e.g. PrefabOutlining):
- Removed extension methods to substitute:
  - `IMonoBehaviour.IsSceneObject()` → `monoBehaviour.GameObjectP is not null` (ScriptableObjects have null GameObjectP).
- Type-name conflicts to fully-qualify:
  - `AssetRipper.Processing.PrefabProcessor` (user-restored) vs `AssetRipper.Processing.Prefabs.PrefabProcessor` (current AR built-in) — use FQN in `ExportHandlerHook` mirror.
- Config-class rename: `LibraryConfiguration` → `FullConfiguration`.
- Hook base: old code used `: RipperHook` (a *namespace*, not a type) + `AddExtraHook(...)` (non-existent in current Ruri.Hook). Port to `: RipperHookCommon` + `[RipperHook(GameType.X)]` + `RegisterModule(new ExportHandlerHook())` (mirror `AR_StaticMeshSeparation_Hook`).

---

## 9. Logger sinks

`AssetRipper.Import.Logging.Logger` is a global static with `List<ILogger>` sinks — does **nothing** if no sink is `Logger.Add`'d.
- `Ruri.RipperHook.CLI/Cli/HeadlessRunner.cs:165` wires `StderrLogger` + `FileLogger`. Working.
- `Ruri.RipperHook.GUI/Program.cs` wires `new ConsoleLogger()` after `Bootstrap.InstallAssemblyResolver()`. Without it, all `Logger.Info(LogCategory.Import, ...)` during file loading goes silent — only hook output (which uses `Console.WriteLine` directly) leaks through.
- `Ruri.FModelHook.GUI/ConsoleLogSinkHook.cs` post-`App.OnStartup` re-configures Serilog (FModel only adds the Console sink in `#if DEBUG`).

---

## 10. AssemblyDumper pipeline + TypeTree (how `Ruri.SourceGenerated.dll` is built)

`Ruri.SourceGenerated.dll` is the Unity type model every AR game hook consumes (`ClassID_<N>` classes + Read/Write/YAML/Walk methods). Two halves:

| Piece | Role |
|---|---|
| `AssetRipper.AssemblyDumper` (frozen submodule) | The generator. ~60 ordered passes (`Program.cs`) turn a `type_tree.tpk` into the `AssetRipper.SourceGenerated` assembly. Entry: `Pass000_ProcessTpk.IntitializeSharedState("type_tree.tpk")`. |
| `Source/Ruri.AssemblyDumper` (editable) | Orchestrator: builds the tpk, runs every AR pass by reflection, renames the assembly, emits + decompiles + rebuilds + deploys the DLL. |

**`build` flow** (`Program.RunBuild`, default / no args): ① `TypeTreeTpkBuilder.WriteFromJsonDirectory(output/, type_tree.tpk)` ② `EnsureRequiredArtifacts` copies `consolidated.json`/`native_enums.json`/`engine_assets.tpk`/`assemblies.json` from `0Bins/AssetRipper.AssemblyDumper/{Release|Debug}/` ③ `new ArAssemblyDumperHook().Initialize()` ④ `PassRunner.RunAllExceptSave` (passes 000-941 by reflection, 1:1 with AR `Program.cs`) ⑤ `PostProcess.RenameAssemblyAndNamespaces` (`AssetRipper.SourceGenerated`→`Ruri.SourceGenerated`; the name is a `const`, un-hookable) ⑥ `PassRunner.RunSave` (Pass998) emits `Ruri.SourceGenerated.dll` ⑦ `RecompileStage` decompiles to `Source/Ruri.SourceGenerated/Ruri/SourceGenerated`, `dotnet build`, `<CopyAfterBuild>` deploys DLL to `Source/Ruri.RipperHook/Libraries/`. Other modes: `docs` (PDB→consolidated.json), `hook` (ClassHookGenerator).
- Build the tool: `dotnet build Source/Ruri.AssemblyDumper/Ruri.AssemblyDumper.csproj -c Debug`. Run: `…/0Bins/Ruri.AssemblyDumper/Debug/Ruri.AssemblyDumper.exe` (no args ⇒ input = `D:\Ruri\Git\FractalTools\TypeTree\output`).

**Inputs**
- `D:\Ruri\Git\FractalTools\TypeTreeDumps` — official Unity dumps, **1384 versions**, `InfoJson/<ver>.json` = `{Version, Strings[], Classes[]}` (each class: `TypeID, Name, Base, IsAbstract, EditorRootNode, ReleaseRootNode`). Canonical real-Unity source.
- `D:\Ruri\Git\FractalTools\TypeTree` — custom forked-engine trees. Folders named by `CustomEngineType` id (`1`=Houkai, `2`=StarRail, `5`=EndField); each `<gamever>/info.json`. `RazTreeConverter.py` → flat `output/` files `{maj}.{min}.{build}x{id}` (the `x` ⇒ `UnityVersionType.Experimental`, `TypeNumber`=engine id) + copies `Common/*.json` (real-Unity anchors). **`output/` is the dumper input and is gitignored; `Common/` + `1,2,5/` are source-of-truth — so "complete the dataset" means populating `Common/`.**
- `CustomEngineType` (`Source/Ruri.RipperHook/Core/CustomEngineType.cs`) — engine→id, stored as the version `TypeNumber` (byte, ≤255).

**Version model / key APIs**
- `AssetRipper.Primitives.UnityVersion`: `Major.Minor.Build` + `Type`(`UnityVersionType`) + `TypeNumber`; `StripType/StripBuild/StripMinor/StripTypeNumber`. Real dumps = `Final`/`Beta`; **custom overlays = `Experimental`** (the reliable detector).
- `Pass000_ProcessTpk`: `MinimumVersion=3.5.0`. `MakeVersionRedirectDictionary` snaps each version to a boundary by what differs from the previous (major→`StripMinor`, minor→`StripBuild`, build→`StripType`, type→`StripTypeNumber`) — "moves versions to inferred boundaries". Drops IDs 100000-100011 and `129` (PlayerSettings).
- `TypeTreeTpkBuilder.Create`: versions in sorted order; `CommonString` = append-only, prefix-consistent union (throws on index mismatch); a class is emitted only when its dump changes (singularity compression); a class **absent from a version is null-marked = "removed here"**.
- `SharedState`: `SourceVersions[]`, `Min/MaxVersion`, `ClassInformation` (id→`VersionedList<UniversalClass>`), `ClassGroups` (id→`ClassGroup`; `GeneratedClassInstance.VersionRange`), `NameToTypeID`, `HistoryFile` (= `consolidated.json`, enum/member/doc history, PDB-derived, **version-independent**). `GetGeneratedInstanceForObjectType` / `ClassGroupBase.GetInstanceForVersion`/`GetTypeForVersion` do **exact** version-range matching and throw if nothing covers the version.

**Custom engines are OVERLAYS, not snapshots** — a forked game ships a *partial* tree (only classes it uses) on a base Unity version. EndField (id 5, base 2021.3) is ECS: ships leaf components + `MonoBehaviour(114)` but **drops the whole abstract chain** (`GameObject(1)`,`Transform(4)`,`Component(2)`,`Behaviour(8)`,`Renderer`,`Collider`,`Joint`,`Effector2D`,…; ~15 bases, referenced by 100+ leaf classes). StarRail (id 2) ships a **stripped `UnityConnectSettings(310)`** (6 fields) at 2019.4.210+.

**`ArAssemblyDumperHook` — root-cause analysis of the old 6-hook / 9-site diff:**
- *Removed — fixed by the overlay rule*: `Pass005.GetClass` closest-version fallback existed only because EndField's dropped ancestor chain made `Pass005.AssignInheritance` base-class resolution miss. With the overlay rule in `TypeTreeTpkBuilder` (Experimental versions don't null-mark omitted classes), ancestors carry forward and every base resolves exactly. ⇒ deleted.
- *Removed — fixed by data*: `Pass555` expects **113** common strings; the dataset topped out at **112** (newest dump `6000.4.0f1`). Adding `6000.5.0a8` (first of all 1384 dumps with 113 strings) satisfies it directly. ⇒ deleted.
- *Kept — intrinsic to a multi-overlay dataset*: closest-version fallback on `SharedState.GetGeneratedInstanceForObjectType` + `ClassGroupBase.GetTypeForVersion`. Custom **subclasses** (e.g. VFX entry structs) are defined disjointly per engine — StarRail `[2019.4.100,2020.0)` and EndField `[2021.3.527,2022.0)` — with a real-Unity gap between. Passes resolving a field type at an in-between boundary (`Pass015`→`GenericTypeResolver.ResolveNode`→`GetTypeForVersion`; also `Pass100/101`, `UniqueNameFactory`) hit the gap and throw `No instance found`; the fallback snaps to the nearest covering instance. **No set of real "singularity" versions closes these — they're holes in the custom data itself.**
- *Kept — custom-data adapters*: `Pass506` no-op (StarRail's stripped 310 has no `m_CrashReportingSettings`/`m_UnityPurchasingSettings` insertion landmark; AR *does* `m_`-prefix generated fields, so full variants would work — it's the game's stripped tree that breaks it); `Pass039` prune (doc-injection references enum members missing from incomplete dumps; docs are irrelevant to code-gen).

**Net**: 6 hooks → **4**. Removed `Pass005.GetClass` (the EndField inheritance blocker — now fixed properly in the tpk builder) and `Pass555`. The file **cannot** be deleted: closest-version fallback is the correct general mechanism for disjoint per-engine overlays, and Pass506/Pass039 adapt to specific custom dumps. The user's "copy all singularity versions ⇒ delete the hook" premise holds only for `Pass555` (a genuine missing-newest-dump) and `Pass005` (fixed by the overlay model, not by adding versions); the rest are not sparsity bugs.

**Minimal Common (singularity) set — keep it small.** `Common/` is intentionally NOT every Unity minor (that bloats tpk-build + the whole generation, and inflates the tracked repo). Because the closest-version fallback tolerates gaps, the only *required* real versions are: each custom engine's **base** (`2017.4.0f1` Houkai, `2019.4.0f1` StarRail, `2021.3.0f1` EndField — the overlay carry-forward source the engine's `Experimental` versions sit on top of), the **113-string ceiling** (`6000.5.0a8`, for Pass555), and a **floor + early anchors** (`3.5.7`, `4.1.0`, `5.0.0f4`, `5.6.0b5` — `MinVersion`=3.5.0 + diff stability). **8 files, ~75 MB.** Do **not** re-add the intermediate minors (2018.x/2020.x/2022.x/6000.1-4 …) — they're redundant under the fallback and only slow generation. Add a real version only if a *non-custom* game needs that exact Unity type tree modeled precisely. `RazTreeConverter.py` regenerates `output/` (gitignored) = `Common/*.json` verbatim + converted `1,2,5/` overlays.

---

## 11. IL2CPP native method disassembly (`AR_Il2CppMethodDump`)

For IL2CPP games AssetRipper turns `GameAssembly.dll` into **dummy** .NET assemblies (empty method bodies) via the `AssetRipper.Cpp2IL.Core` package (a fork of SamboyCoding/Cpp2IL). `AR_Il2CppMethodDump` rides that same Cpp2IL analysis to also emit each method's **native** (x86/ARM) method body, which AR otherwise never exports. Source: `Source/Ruri.RipperHook/AssetRipperHook/Il2CppMethodDump/`.

**Where the dummy build happens**: `IL2CppManager.Initialize` (`AssetRipper.Import`, frozen) runs during *load*: `Cpp2IlApi.InitializeLibCpp2Il(...)` parses metadata+binary, then `AsmResolverDllOutputFormat.BuildAssemblies(appContext)` builds the AsmResolver dummy `AssemblyDefinition`s. After this, the static `Cpp2IL.Core.Cpp2IlApi.CurrentAppContext` (an `ApplicationAnalysisContext`) holds the full model and **stays alive through export** (its `Il2CppBinary` is what raw bytes are re-read from). The GUI resets it per load via the `IL2CppManager.ClearStaticState` event + a fresh `InitializeLibCpp2Il`.

**Hook point**: `DllPostExporter.DoPostExport(GameData, FullConfiguration, FileSystem)` (`AssetRipper.Export.UnityProjects.Scripts`) — the "Saving game assemblies…" post-exporter that writes the dummy DLLs to `{settings.AuxiliaryFilesPath}/GameAssemblies/`. It is yielded **unconditionally** by `ExportHandler` so it always runs. We hook it **prefix-continue** (`isBefore:true, isReturn:false`) — exact same signature shape as `AR_SkipStreamingAssetsCopy` (an instance `void(GameData,FullConfiguration,FileSystem)` post-exporter), so the injected `Ldarg_0..3; call` lands our (bogus) `this`=the exporter and our real params=the source params. Guard `gameData.AssemblyManager is IL2CppManager` to skip Mono games (where `CurrentAppContext` is null/stale). Never touch `this` in the hook body.

**Cpp2IL API surface actually used** (all public on the 1.0.1 package):
- Enumerate: `appContext.Assemblies` → `AssemblyAnalysisContext.Types` (flat list incl. nested; `.CleanAssemblyName` for the file name) → `TypeAnalysisContext.Methods` → `MethodAnalysisContext`.
- Per method: `.UnderlyingPointer` (ulong VA — **0 ⇒ abstract/extern/no body, skip**), `.Rva` (ulong), `.RawBytes` (`Memory<byte>`, `.EnsureRawBytes()` to force), `.FullNameWithSignature` / `.DefaultName`.
- Disassemble: **`appContext.InstructionSet.PrintAssembly(method)` returns the native asm text directly** — no need to drive Iced/Disarm yourself. The active `Cpp2IlInstructionSet` is auto-selected (`X86InstructionSet` for the 32/64-bit x86 case). **`X86InstructionSet.PrintAssembly` uses a `static MasmFormatter`/`StringOutput` → NOT thread-safe; call sequentially.**

**Output**: `{AuxiliaryFilesPath}/Il2CppMethodDump/` — one streamed `{CleanAssemblyName}.asm` per assembly (header block + per-method `; VA=… RVA=…` comment + disassembly) plus a global `MethodPointers.csv` (`Assembly,VA,RVA,Method`, RFC-4180-quoted) for IDA/Ghidra import. Whole pass is cheap: ~33 k methods on a 17 MB x86 GameAssembly disassembled in ~2 s. Every `PrintAssembly` is wrapped per-method so one bad method can't abort the file; the whole dump is wrapped so it can't break the normal dummy-DLL export. Surfaced as a Settings→Features checkbox (`AR_Il2CppMethodDump_`); IL2CPP-only, opt-in.

**To validate the Cpp2IL API in isolation** (without the whole AR pipeline): a tiny `net9.0` console with `PackageReference AssetRipper.Cpp2IL.Core 1.0.1` that replicates `IL2CppManager`'s static ctor (register instruction sets + `LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport()`), then `DetermineUnityVersion` → `InitializeLibCpp2Il` → walk `CurrentAppContext`. Reflecting the package from Windows PowerShell 5.1 fails (it's .NET-Framework; the package targets net9 `System.Runtime`) — use a `dotnet run` probe on the net9 runtime instead.
