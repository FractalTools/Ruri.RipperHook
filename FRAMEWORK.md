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

- AR_* hooks that survive as hook implementations: `AR_SkipStreamingAssetsCopy`, `AR_SkipProcessingAnimation`, `AR_ShaderDecompiler` (custom decompiler), `AR_PrefabOutlining` (restored deleted processor), `AR_StaticMeshSeparation` (restored deleted processor).
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
