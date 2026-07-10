# Ruri-RipperHook — 框架参考

关于 Ruri.Hook + AssetRipper 内部机制的具体事实。每当你需要写一个 hook、追一条流水线、或修一个回归时，读这份。硬性规则 / 限制住在 [CLAUDE.md](CLAUDE.md) 里。

> **给未来 AI 会话的维护指令** —— 这份文件是一张快照，不是圣经。AssetRipper（冻结的子模块）会定期更新，Ruri.Hook 本身也在演进。当你调查某件事、发现：
> - **这里某条说法现在错了**（类型改名、方法移除、默认值不同、流水线顺序变了、插入了新处理器，等等）—— **在同一次编辑里就把这份文件里的说法改对**。别把过期的事实留给下一个会话去踩。
> - **某件事有更干净 / 更标准的做法**，而这份文件目前把它描述成一个 workaround（例如现在已经有官方 AR API 能做我们绕着 hook 的事）—— **用更好的方法替换掉那个 workaround 章节**，并删掉旧的「笨」路径。别为了感情因素把两者都留着。
> - **一个让你花了调试时间的新坑** —— 加上去。下面「继承发现坑」+「postfix-continue 误触发」这两条，每条都是 ≥1 小时的痛；记录一次就能为下一个会话省下同样的时间。
>
> 把这份文件当作对框架的活体 code review。错误 + 过期的文档比没有文档更糟。

---

## 1. 构建 / 运行速查

| 需求 | 命令 |
|---|---|
| 编译单个 Ruri 项目 | `dotnet build Source/Ruri.RipperHook/Ruri.RipperHook.csproj -c Debug --nologo` |
| CLI exe 路径 | `AssetRipper/Source/0Bins/AssetRipper/Debug/Ruri.RipperHook.CLI.exe` |
| GUI exe 路径 | 同目录，`Ruri.RipperHook.GUI.exe` / `Ruri.FModelHook.GUI.exe` |
| 列出 hook | `Ruri.RipperHook.CLI.exe --list-hooks`（返回 JSON，退出码 3） |
| 无头导出 | `--hook <Id> --load <path> --export <dir> [--fail-fast false] [--log-level Info]`（CLI 在写入前删除 `--export` 目录） |
| GUI 锁住 DLL | `Get-Process Ruri.RipperHook.GUI -EA SilentlyContinue \| Stop-Process -Force` —— 仅当拷贝失败时，绝不投机执行 |
| GameType 定义 | `Source/Ruri.RipperHook/Core/GameType.cs` —— 新的 AR_/game hook 需要在这里加一条 |

---

## 2. Ruri.Hook attribute 速查表（`Source/Ruri.Hook/Attributes/*`）

| Attribute | 用途 |
|---|---|
| `[RetargetMethod(typeof(T), name, isBefore, isReturn)]` | 对 `T.name` 做 IL 注入。`isBefore=true,isReturn=true` = prefix-replace（跳过原方法）。`isBefore=true,isReturn=false` = prefix-continue。`isBefore=false,isReturn=false` = postfix-continue —— **在只有单个 Ret 的实例 `void` 方法上别信它；那个 `while(TryGotoNext(Before, Ret))` 循环实测会误触发。行为等价时改用 prefix-continue。** |
| `[RetargetMethodCtorFunc(typeof(T))]` | patch 无参 ctor。方法签名：`static bool Foo(ILContext il)`。用来改默认字段值，例如 AR_BundledAssetsExportMode_Hook 在最后的 Ret 之前把 ProcessingSettings.BundledAssetsExportMode=2。 |
| `[RetargetMethodFunc(typeof(T), name)]` | 完整 IL manipulator。方法签名：`static bool Foo(ILContext il)`。用于复杂的 IL 重写。 |

**继承发现坑**（花掉了半个会话）：`Registry.ApplyTypeHooks(GetType())` 调用 `Type.GetMethods(BindingFlags.Public|NonPublic|Instance|Static)`。没有 `BindingFlags.FlattenHierarchy`，**继承来的 `public static` 方法不会被返回**。所以基类 / 公共 partial 类上的 `[RetargetMethod]` 不会被派生的版本化 hook 拾取，除非你在版本化 hook 的 `InitAttributeHook()` 里显式 `Registry.ApplyTypeHooks(typeof(MyCommon_Hook));`。参考：`Arknights_2_7_31_Hook.InitAttributeHook`。

**Hook 生命周期**：`Bootstrap.ApplyHooks(config)` → `RuriHook.ApplyHooks` 遍历可用的 `[GameHookAttribute]` 类型，实例化每个匹配 `config.EnabledHooks` 的，调用 `Initialize()` → `RipperHookCommon.Initialize` 先调 `InitAttributeHook()` 然后向 `RuriRuntimeHook` 注册。

---

## 3. AssetRipper 数据流（冻结，只列流水线）

```
SchemeReader.LoadFile(path)
  → FileBase (FileStreamBundleFile / SerializedFile / ResourceFile / ...)，FilePath 由 Scheme<T>.Read 设置
  → 对 bundle，ReadFileStreamData 添加 ResourceFile，FilePath=bundle.FilePath, Name=entry.Path (CAB)
  → FileContainer.ReadContents 经 SchemeReader.ReadFile 把 ResourceFile → SerializedFile 提升，保留 FilePath
GameBundle.FromPaths
  → 遍历 FileBase 栈：每个 FileContainer 一次 SerializedBundle.FromFileContainer(container, factory, defaultVersion)
    → 每个 SerializedFile 一次 bundle.AddCollectionFromSerializedFile(file, factory)（在这个接缝处丢掉 container.FilePath）
      → SerializedAssetCollection.FromSerializedFile(bundle, file, factory) 构造 collection（设置 Name=file.NameFixed
        但从不设 collection.FilePath —— hook ReadData postfix 来传播它）
        → ReadData(collection, file, factory) 遍历 file.Objects (ObjectInfo[]: FileID, byteSize, ObjectData byte[], Type)
          → factory.ReadAsset(assetInfo, objectInfo.ObjectData, type) → IUnityObjectBase
          → collection.AddAsset(asset)
ExportHandler.Process(gameData)（在 Load 之后调用）
  → foreach IAssetProcessor in GetProcessors(): processor.Process(gameData)
    SceneDefinitionProcessor → OriginalPathProcessor → MainAssetProcessor → AnimatorControllerProcessor
    → AudioMixerProcessor → EditorFormatProcessor → LightingDataProcessor → PrefabProcessor
    → SpriteProcessor → ScriptableObjectProcessor
ExportHandler.Export(gameData, outputPath) —— 用 ExportCollections，写 YAML / 贴图 / 等等。
```

**关键死胡同（dead-ends）**：
- `ObjectInfo.ObjectData`（磁盘上的原始字节）和 `byteSize` 在 load 之后会被 GC —— 只能通过 `SerializedAssetCollection.ReadData` postfix 上的 hook 拿到。AR **不会**把它们保存在 asset / collection 的任何地方。
- `AssetCollection.FilePath` 可以设置，但 AR 从不设它。hook `ReadData` postfix 去做 `collection.FilePath = file.FilePath`。
- YAML 输出可用（在解析后的内存树上做 walker）；二进制 `asset.Write(AssetWriter)` 对 source-generated 类可用（Pass101 生成真正的序列化）但对 size/hex 用途来说很慢。不要在 BuildAssetList 期间调它。
- Bundle 重建（`.bundle` → 修改 → `.bundle`）**不支持**：`FileStreamBundleFile.WriteFileStreamData` 抛 `NotImplementedException`；`ArchiveBundleFile.Write` 抛 `NotSupportedException`；没有 YAML reader。那种场景下 `AssetsTools.NET`（已在 `Ruri.RipperHook.csproj` 里 PackageReference）才是对的工具，不是 AR。

---

## 4. Path / OriginalPath 行为

- `IUnityObjectBase.OriginalPath` setter 存储原始 fullPath；`OriginalDirectory`/`OriginalName`/`OriginalExtension` 经 `Path.*` 派生（Windows：派生字段上是反斜杠，**存储的 fullPath 里保留正斜杠** —— getter 返回存储的值，所以显示保持干净）。
- `GetBestDirectory()` 优先级：`OverrideDirectory > OriginalDirectory > "Assets/{ClassName}"`。
- `OriginalPathProcessor.Process(GameData)` 遍历每个 `IAssetBundle.Container`（`AccessDictionaryBase<Utf8String, IAssetInfo>`），为 `Asset.FileID == 0` 的条目（即本地 asset）设置 `OriginalPath`。
- `BundledAssetsExportMode`（当前 AR 默认 **DirectExport**）：DirectExport 只是经 `OriginalPathHelper.EnsureStartsWithAssets` 前缀上 `Assets/`，保留斜杠。GroupByBundleName 做 `Path.Join("Assets/AssetBundles/<bundle>", assetPath)` → 反斜杠。

**合成 Container 条目**（被 Arknights 路径修复使用，`Source/Ruri.RipperHook/AssetRipperGameHook/UnityHypergryph/Arknights/CommonHook/`）：
```csharp
AccessPairBase<Utf8String, IAssetInfo> pair = bundle.Container.AddNew();
pair.Key = new Utf8String(myForwardSlashPath);
pair.Value.Asset.SetAsset(bundle.Collection, asset as IObject);  // SetAsset 需要 IObject（不是 IUnityObjectBase）
```
把 `OriginalPathProcessor.Process` 作为 **prefix-continue** 来 hook，这样 AR 自己的 Process 会通过标准流水线消费我们的条目。跳过已经在 Container 里的 asset，跳过 `IAssetBundle` 本身，跳过非 `IObject`。

---

## 5. Source-generated 参考

`Ruri.SourceGenerated.dll` 从 `Source/Ruri.RipperHook/Libraries/` 经 HintPath 引用。由 `Ruri.AssemblyDumper` 流水线生成。

**只读源码镜像**（用于 grep / 参考）：`D:\Ruri\Git\FractalTools\AssemblyDumper\AssetRipper\SourceGenerated\` —— 类接口在 `Classes/ClassID_<N>/I<Class>.cs`，生成的实现在 `<Class>_<version>.cs`。用它来核对方法签名（例如 `IMonoBehaviour.GameObjectP`、`IAssetBundle.Container`）。

---

## 6. 自定义 IAssetProcessor 注入（被 AR_PrefabOutlining、AR_StaticMeshSeparation 使用）

`Source/Ruri.RipperHook/Utils/Hook/ExportHandlerHook.cs` 是一个 module，它 hook `ExportHandler.Process`，用 `GetProcessors()` 的一份重新实现替换掉整条流水线，并在 `EditorFormatProcessor` 和 `LightingDataProcessor` 之间提供一个自定义插入点：

```csharp
public delegate IEnumerable<IAssetProcessor> AssetProcessorDelegate(FullConfiguration Settings);
public static List<AssetProcessorDelegate> CustomAssetProcessors = new();
```

要加一个 processor：在你的 `[RipperHook]` 类的 `InitAttributeHook` 里：
```csharp
RegisterModule(new ExportHandlerHook());
ExportHandlerHook.CustomAssetProcessors.Add(MyDelegate);
```
其中 `MyDelegate(FullConfiguration s) => /* yield return */`。

**注意事项**：
- `ExportHandlerHook.GetProcessors()` 是 AR `ExportHandler.GetProcessors()` 的一份手工镜像拷贝。**当前缺少 `OriginalPathProcessor`** —— 如果你的 hook 需要 OriginalPath 被填充，要么把它加回镜像里，要么在这些 hook 下别依赖它。
- 多个 Ruri hook 注册 `ExportHandlerHook` 是没问题的（module 的 `OnApply` 是幂等的，因为它是同一个静态委托列表）。

---

## 7. AR_* hook 与原生设置的取舍策略

**规则**：AR_* hook ID 保留给*对 AR 原生支持之上的扩展*。如果某个特性已经作为一个原生 `ProcessingSettings` / `ExportSettings` / `ImportSettings` 属性存在、且有合理的默认值，**就别再发一个并行的 AR_* hook** —— 这种重复只会把配置搞乱。

- 作为 hook 实现存活下来的 AR_* hook：`AR_SkipStreamingAssetsCopy`、`AR_SkipProcessingAnimation`、`AR_ShaderDecompiler`（自定义反编译器）、`AR_PrefabOutlining`（恢复了被删的处理器）、`AR_StaticMeshSeparation`（恢复了被删的处理器）、`AR_Il2CppMethodDump`（IL2CPP 原生方法体反汇编 —— AR 根本没有原生 asm 导出；见 §11）。
- 因为**原生设置 + 默认值已经够了**而被删的 AR_* hook：`AR_BundledAssetsExportMode`（`ProcessingSettings.BundledAssetsExportMode` 已经默认 `DirectExport`）。

**GUI 呈现**：
- 游戏 hook（Arknights、EndField、GirlsFrontline2、…）→ Hooks 树（每个游戏互斥，选一个版本）。
- AR_* hook → Settings 对话框「Features」组。用复选框开关，存在同一个 `HookConfig.EnabledHooks` 集合里，只是从树里隐藏。
- 首次运行默认值（例如 `AR_SkipStreamingAssetsCopy_` 开）：在 `Ruri.RipperHook.GUI/Program.cs:Main` 里种下，门控在 `!File.Exists(configPath)`。用户通过 Settings 保存一次之后，选择就永远归他们了。

**新增一个 AR_* hook 时**：写代码之前，grep `AssetRipper.Processing.Configuration.ProcessingSettings` / `AssetRipper.Export.Configuration.ExportSettings` / `AssetRipper.Import.Configuration.ImportSettings`，找等价属性。如果存在一个带所需默认值的，就用 `[RetargetMethodCtorFunc]` 在设置类上翻一下默认值（看 `BundledAssetsExportMode` *以前*是怎么做的）—— 或者更好，把它作为一个 Settings 对话框控件暴露出来就收工。只为 AR 没有原生旋钮的行为保留一个新 hook ID。

---

## 8. 从旧 AR 恢复的代码危险清单

当用户恢复被删的 AR API（例如 PrefabOutlining）：
- 需要替换的被移除扩展方法：
  - `IMonoBehaviour.IsSceneObject()` → `monoBehaviour.GameObjectP is not null`（ScriptableObject 的 GameObjectP 为 null）。
- 需要完全限定的类型名冲突：
  - `AssetRipper.Processing.PrefabProcessor`（用户恢复的）vs `AssetRipper.Processing.Prefabs.PrefabProcessor`（当前 AR 内置的）—— 在 `ExportHandlerHook` 镜像里用 FQN。
- 配置类改名：`LibraryConfiguration` → `FullConfiguration`。
- Hook 基类：旧代码用 `: RipperHook`（一个*命名空间*，不是类型）+ `AddExtraHook(...)`（当前 Ruri.Hook 里不存在）。移植到 `: RipperHookCommon` + `[RipperHook(GameType.X)]` + `RegisterModule(new ExportHandlerHook())`（镜像 `AR_StaticMeshSeparation_Hook`）。

---

## 9. Logger sink

`AssetRipper.Import.Logging.Logger` 是一个全局静态、带 `List<ILogger>` sink —— 如果没有 sink 被 `Logger.Add`，它**什么都不做**。
- `Ruri.RipperHook.CLI/Cli/HeadlessRunner.cs:165` 接上了 `StderrLogger` + `FileLogger`。可用。
- `Ruri.RipperHook.GUI/Program.cs` 在 `Bootstrap.InstallAssemblyResolver()` 之后接上 `new ConsoleLogger()`。没有它，文件加载期间所有 `Logger.Info(LogCategory.Import, ...)` 都会静默 —— 只有 hook 输出（它直接用 `Console.WriteLine`）会漏出来。
- `Ruri.FModelHook.GUI/ConsoleLogSinkHook.cs` 在 `App.OnStartup` 之后重新配置 Serilog（FModel 只在 `#if DEBUG` 里加 Console sink）。

---

## 10. AssemblyDumper 流水线 + TypeTree（`Ruri.SourceGenerated.dll` 是怎么构建的）

`Ruri.SourceGenerated.dll` 是每个 AR 游戏 hook 都消费的 Unity 类型模型（`ClassID_<N>` 类 + Read/Write/YAML/Walk 方法）。两半：

| 部件 | 角色 |
|---|---|
| `AssetRipper.AssemblyDumper`（冻结子模块） | 生成器。~60 个有序 pass（`Program.cs`）把一个 `type_tree.tpk` 变成 `AssetRipper.SourceGenerated` assembly。入口：`Pass000_ProcessTpk.IntitializeSharedState("type_tree.tpk")`。 |
| `Source/Ruri.AssemblyDumper`（可编辑） | 编排器：构建 tpk，用反射跑每个 AR pass，给 assembly 改名，emit + 反编译 + 重新构建 + 部署 DLL。 |

**`build` 流程**（`Program.RunBuild`，默认 / 无参数）：① `TypeTreeTpkBuilder.WriteFromJsonDirectory(output/, type_tree.tpk)` ② `EnsureRequiredArtifacts` 从 `0Bins/AssetRipper.AssemblyDumper/{Release|Debug}/` 拷贝 `consolidated.json`/`native_enums.json`/`engine_assets.tpk`/`assemblies.json` ③ `new ArAssemblyDumperHook().Initialize()` ④ `PassRunner.RunAllExceptSave`（passes 000-941 经反射，与 AR `Program.cs` 1:1）⑤ `PostProcess.RenameAssemblyAndNamespaces`（`AssetRipper.SourceGenerated`→`Ruri.SourceGenerated`；这个名字是个 `const`，不可 hook）⑥ `PassRunner.RunSave`（Pass998）emit `Ruri.SourceGenerated.dll` ⑦ `RecompileStage` 反编译到 `Source/Ruri.SourceGenerated/Ruri/SourceGenerated`，`dotnet build`，`<CopyAfterBuild>` 把 DLL 部署到 `Source/Ruri.RipperHook/Libraries/`。其它模式：`docs`（PDB→consolidated.json）、`hook`（ClassHookGenerator）。
- 构建工具：`dotnet build Source/Ruri.AssemblyDumper/Ruri.AssemblyDumper.csproj -c Debug`。运行：`…/0Bins/Ruri.AssemblyDumper/Debug/Ruri.AssemblyDumper.exe`（无参数 ⇒ 输入 = `D:\Ruri\Git\FractalTools\TypeTree\output`）。

**输入**
- `D:\Ruri\Git\FractalTools\TypeTreeDumps` —— 官方 Unity dump，**1384 个版本**，`InfoJson/<ver>.json` = `{Version, Strings[], Classes[]}`（每个类：`TypeID, Name, Base, IsAbstract, EditorRootNode, ReleaseRootNode`）。规范的真实 Unity 来源。
- `D:\Ruri\Git\FractalTools\TypeTree` —— 自定义的分叉引擎树。文件夹以 `CustomEngineType` id 命名（`1`=Houkai, `2`=StarRail, `5`=EndField）；每个 `<gamever>/info.json`。`RazTreeConverter.py` → 扁平的 `output/` 文件 `{maj}.{min}.{build}x{id}`（`x` ⇒ `UnityVersionType.Experimental`，`TypeNumber`=引擎 id）+ 拷贝 `Common/*.json`（真实 Unity 锚点）。**`output/` 是 dumper 输入且被 gitignore；`Common/` + `1,2,5/` 是 source-of-truth —— 所以「补全数据集」意味着填充 `Common/`。**
- `CustomEngineType`（`Source/Ruri.RipperHook/Core/CustomEngineType.cs`）—— 引擎→id，作为版本 `TypeNumber` 存储（byte，≤255）。

**版本模型 / 关键 API**
- `AssetRipper.Primitives.UnityVersion`：`Major.Minor.Build` + `Type`(`UnityVersionType`) + `TypeNumber`；`StripType/StripBuild/StripMinor/StripTypeNumber`。真实 dump = `Final`/`Beta`；**自定义覆盖层 = `Experimental`**（可靠的判别依据）。
- `Pass000_ProcessTpk`：`MinimumVersion=3.5.0`。`MakeVersionRedirectDictionary` 按每个版本相对前一个的差异把它吸附到一个边界（major→`StripMinor`，minor→`StripBuild`，build→`StripType`，type→`StripTypeNumber`）——「把版本移到推断出的边界」。丢弃 ID 100000-100011 和 `129`（PlayerSettings）。
- `TypeTreeTpkBuilder.Create`：版本按排序顺序；`CommonString` = 只追加、前缀一致的并集（索引不匹配就抛）；一个类只在它的 dump 变化时才 emit（奇点压缩）；一个类**在某版本中缺席 = 被 null 标记 = 「在此处被移除」**。
- `SharedState`：`SourceVersions[]`、`Min/MaxVersion`、`ClassInformation`（id→`VersionedList<UniversalClass>`）、`ClassGroups`（id→`ClassGroup`；`GeneratedClassInstance.VersionRange`）、`NameToTypeID`、`HistoryFile`（= `consolidated.json`，enum/member/doc 历史，PDB 派生，**版本无关**）。`GetGeneratedInstanceForObjectType` / `ClassGroupBase.GetInstanceForVersion`/`GetTypeForVersion` 做**精确**的版本范围匹配，没有覆盖该版本就抛。

**自定义引擎是覆盖层（OVERLAY），不是快照** —— 一个分叉游戏在某个基础 Unity 版本之上发布一棵*部分*树（只有它用到的类）。EndField（id 5，基础 2021.3）是 ECS：发布叶子组件 + `MonoBehaviour(114)` 但**丢掉整条抽象链**（`GameObject(1)`,`Transform(4)`,`Component(2)`,`Behaviour(8)`,`Renderer`,`Collider`,`Joint`,`Effector2D`,…；~15 个基类，被 100+ 叶子类引用）。StarRail（id 2）在 2019.4.210+ 发布一个**精简的 `UnityConnectSettings(310)`**（6 个字段）。

**`ArAssemblyDumperHook` —— 对旧的 6-hook / 9-site diff 的根因分析：**
- *移除 —— 被覆盖层规则修复*：`Pass005.GetClass` 最近版本回退之所以存在，只是因为 EndField 丢掉的祖先链让 `Pass005.AssignInheritance` 的基类解析落空。有了 `TypeTreeTpkBuilder` 里的覆盖层规则（Experimental 版本不对省略的类做 null 标记），祖先会前向携带，每个基类都精确解析。⇒ 删掉。
- *移除 —— 被数据修复*：`Pass555` 期望 **113** 个 common string；数据集顶到 **112**（最新 dump `6000.4.0f1`）。加入 `6000.5.0a8`（全部 1384 个 dump 里第一个有 113 个 string 的）直接满足它。⇒ 删掉。
- *保留 —— 多覆盖层数据集的内在属性*：`SharedState.GetGeneratedInstanceForObjectType` + `ClassGroupBase.GetTypeForVersion` 上的最近版本回退。自定义**子类**（例如 VFX 入口结构体）是按引擎不相交地定义的 —— StarRail `[2019.4.100,2020.0)` 和 EndField `[2021.3.527,2022.0)` —— 中间隔着一段真实 Unity 的空隙。在中间边界解析字段类型的 pass（`Pass015`→`GenericTypeResolver.ResolveNode`→`GetTypeForVersion`；还有 `Pass100/101`、`UniqueNameFactory`）会撞进空隙并抛 `No instance found`；回退吸附到最近的覆盖实例。**没有任何一组真实「奇点」版本能填上这些 —— 它们是自定义数据本身的洞。**
- *保留 —— 自定义数据适配器*：`Pass506` no-op（StarRail 精简过的 310 没有 `m_CrashReportingSettings`/`m_UnityPurchasingSettings` 插入地标；AR *确实*给生成字段加 `m_` 前缀，所以完整变体本来能用 —— 是游戏的精简树破坏了它）；`Pass039` prune（doc 注入引用了不完整 dump 里缺失的 enum 成员；doc 与代码生成无关）。

**净结果**：6 个 hook → **4** 个。移除了 `Pass005.GetClass`（EndField 继承阻塞器 —— 现在在 tpk builder 里被正确修复）和 `Pass555`。这个文件**不能**删：最近版本回退是不相交的逐引擎覆盖层的正确通用机制，而 Pass506/Pass039 适配特定的自定义 dump。用户「拷贝所有奇点版本 ⇒ 删掉 hook」的前提只对 `Pass555`（一个真正缺失最新 dump 的情况）和 `Pass005`（被覆盖层模型修复，不是靠加版本）成立；其余都不是稀疏性 bug。

**最小 Common（奇点）集 —— 保持它小。** `Common/` 故意不是每一个 Unity minor（那会让 tpk 构建 + 整个生成膨胀，并撑大被跟踪的仓库）。因为最近版本回退容忍空隙，唯一*必需*的真实版本是：每个自定义引擎的**基础**（`2017.4.0f1` Houkai、`2019.4.0f1` StarRail、`2021.3.0f1` EndField —— 引擎的 `Experimental` 版本所坐落其上的覆盖层前向携带源）、**113-string 上限**（`6000.5.0a8`，给 Pass555）、以及一个**下限 + 早期锚点**（`3.5.7`、`4.1.0`、`5.0.0f4`、`5.6.0b5` —— `MinVersion`=3.5.0 + diff 稳定性）。**8 个文件，~75 MB。** **不要**重新加入中间的 minor（2018.x/2020.x/2022.x/6000.1-4 …）—— 在回退下它们是冗余的，只会拖慢生成。只有当某个*非自定义*游戏需要精确建模那个确切的 Unity 类型树时，才加一个真实版本。`RazTreeConverter.py` 重新生成 `output/`（gitignore）= `Common/*.json` 原样 + 转换后的 `1,2,5/` 覆盖层。

---

## 11. IL2CPP 原生方法反汇编（`AR_Il2CppMethodDump`）

对 IL2CPP 游戏，AssetRipper 经 `AssetRipper.Cpp2IL.Core` 包（SamboyCoding/Cpp2IL 的一个分叉）把 `GameAssembly.dll` 变成**哑（dummy）** .NET assembly（桩方法体），然后 ILSpy 把这些哑 assembly 反编译成 `ExportedProject/Assets/Scripts/.../*.cs`。`AR_Il2CppMethodDump` 搭同一趟 Cpp2IL 分析的车，把每个方法的**原生**（x86/ARM）方法体反汇编出来，并把它**作为 `//` 注释注入到那些反编译 C# 脚本中匹配的方法体里** —— AR 否则只导出空桩。源：`Source/Ruri.RipperHook/AssetRipperHook/Il2CppMethodDump/`。

**模型从哪来**：`IL2CppManager.Initialize`（`AssetRipper.Import`，冻结）在*加载*期间运行：`Cpp2IlApi.InitializeLibCpp2Il(...)` 解析 metadata+binary；之后静态的 `Cpp2IL.Core.Cpp2IlApi.CurrentAppContext`（`ApplicationAnalysisContext`）持有完整模型，并**贯穿 export 一直存活**（它的 `Il2CppBinary` 就是原始字节被重新读取的来源）。GUI 每次加载经 `IL2CppManager.ClearStaticState` + 一次新的 `InitializeLibCpp2Il` 重置它。**别在 DllPostExporter / 哑 DLL 保存阶段 dump** —— 那会把原始 DLL 写到 `AuxiliaryFiles/GameAssemblies/`，不是用户读的 C#。C# 是之后由 ILSpy 产出的。

**Hook 点**：ILSpy 的逐文件反编译器工厂 `WholeProjectDecompiler.CreateDecompiler(DecompilerTypeSystem)`（`AssetRipper.ICSharpCode.Decompiler` 10.1.0.8388 —— AR 分叉，版本不在 nuget.org 上）。AR 的 `ScriptDecompiler.DecompileWholeProject` 构建一个 `CustomWholeProjectDecompiler : WholeProjectDecompiler`，它**没有**override `CreateDecompiler`，所以基（可 hook 的）方法会运行。我们用 **`[RetargetMethodFunc]`**（完整 IL manipulator）hook 它：在 `ret` 之前，`dup` 返回的 `CSharpDecompiler` 并 `call AddTransform(decompiler)`，它把我们的 `IAstTransform` 追加到 `decompiler.AstTransforms`（幂等）。`AddTransform` 必须是 `public static` —— 被注入的调用住在 ILSpy assembly 里，否则会因可见性失败。

**AST 变换**（`Il2CppAsmCommentTransform : IAstTransform`，ILSpy `Run(rootNode, context)`）：对每个带方法体的 `EntityDeclaration`（`MethodDeclaration`/`ConstructorDeclaration`/`OperatorDeclaration`/`Accessor`），`decl.GetSymbol() as IMethod` → 查反汇编 → 经 `body.InsertChildBefore(firstStatement, …, Roles.Comment)` 每条 asm 行插一个 `Comment(line, SingleLine)`。**坑：**（1）在改树之前先把 `DescendantsAndSelf.OfType<…>().ToList()` 物化。（2）`GetSymbol()` 住在命名空间 `ICSharpCode.Decompiler.CSharp` —— 没有那个 `using`，它会解析到错误的 `TypeSystemExtensions.GetSymbol(ResolveResult)` 并编译失败。（3）**空方法体**（`{ }`，无语句）：ILSpy 把注释 emit 在 `}` *之后* —— 锚到 `Roles.RBrace`/`Roles.LBrace` **不能**修复它。加一个 `EmptyStatement`（渲染成一个孤零零的 `;`）并 `InsertChildBefore` 它，这样 asm 就落在里面了。

**关联 ILSpy `IMethod` ↔ Cpp2IL `MethodAnalysisContext`**（`Il2CppAsmLookup`）：从 `CurrentAppContext` 构建一个 `Dictionary<key, List<MethodAnalysisContext>>`；key = `CleanAssemblyName | Normalize(Type.FullName) :: Name / paramCount`。`Normalize` 把嵌套分隔符 `+ / \` → `.` **并剥掉泛型 arity** `` `\d+ ``（`CyclicalList`1` → `CyclicalList`）—— ILSpy 的 `FullName` 既不带分隔符也不带 arity，Cpp2IL 带。key 里有 assembly + arity，匹配就精确：在测试游戏上 **Assembly-CSharp 里 3832/3832 个方法，0 漏**。ILSpy `method.ParentModule.Name` == Cpp2IL `CleanAssemblyName`（"Assembly-CSharp"）。查找是**非消耗 + 幂等的**（重新导出安全），当 `CurrentAppContext` 变化时重建。

**反汇编**：两条路。**x86（32/64）** → `Il2CppX86Listing.Render` 用 **Iced** 自己解码 `method.RawBytes`（所以它有每条指令的 `IP`），收集方法内近跳转目标，在每个目标处 emit 一行 `loc_<IP>:` 标签 —— 一份真正的汇编 listing，你能看到每个跳转落在哪。每条指令用一个本地 `MasmFormatter` 格式化，但格式化器现在挂了一个**指令感知的 `Il2CppSymbolResolver`（Iced `ISymbolResolver`）** —— 分支/调用目标与绝对数据全局**就地**替换成符号，立即数/寄存器相对位移**保持原值**；并叠一层 `Il2CppRegisterFlow` 数据流恢复出的**符号注释**（`; this.field` 等，见下）。**其它一切（ARM/Disarm、WASM）** → 扁平的 `appContext.InstructionSet.PrintAssembly(method)` + `Il2CppAsmAnnotator.Annotate`（纯文本正则回退，无标签、无字段恢复；x86 已不再走 `AnnotateLine`）。经 `app.InstructionSet is X86InstructionSet` 分支。当 `UnderlyingPointer == 0`（抽象/extern）时逐方法跳过。
> **双 Iced 坑**：Ruri.RipperHook 引用了**两个**暴露 `Iced.Intel` 的 assembly —— 真 `Iced`（经 `AssetRipper.Cpp2IL.Core` 传递）和 `MonoMod.Iced`（经 `MonoMod.RuntimeDetour` 传递）。所以 `using Iced.Intel;` 会 `CS0433` 歧义。修法：显式 `<PackageReference Include="Iced" Version="1.21.0" Aliases="icedreal" />` + 在 `Il2CppX86Listing.cs` 里 `extern alias icedreal; using icedreal::Iced.Intel;`，并且那里别 `using System.Text`（它的 `Decoder`/`StringBuilder` 会再次冲突）—— 完全限定 `System.Text.StringBuilder`。**`X86InstructionSet.PrintAssembly` 用一个 `static MasmFormatter`/`StringOutput` → 非线程安全，而 `WholeProjectDecompiler` 并行反编译文件** → 把每次 `PrintAssembly` 串行化在一把锁下（持于 `Il2CppAsmLookup.GetDisassembly`）。只有 AR 实际*反编译*的 assembly（预定义的、Hybrid 下如 `Assembly-CSharp`；`Decompiled` 下的一切）才拿到 asm；`Save` 模式的 assembly 原样 emit 成 DLL。仅 IL2CPP（由 `CurrentAppContext != null` 守卫），opt-in，Settings→Features 复选框 `AR_Il2CppMethodDump_`。

**符号解析核心**（`Il2CppAsmAnnotator.ResolveAddress`，被 x86 的指令感知 `Il2CppSymbolResolver` 与 ARM 回退的文本 `Annotate` 共用）：原始地址毫无意义（`call 10278DB0h`），所以每个**地址操作数**就地替换成符号（不保留裸地址；用户要纯符号、省 token）。x86 侧由 Iced 告知精确操作数种类：只解析**分支/调用目标**（`inBrackets=false`）和**绝对数据全局**（RIP 相对或裸 `[disp]`，`inBrackets=true`）；**立即数一律不碰**（修掉旧纯文本正则把 `add eax,5E593F7Ah` 误标成 `sub_5E593F7A` 代码标签的 bug），**寄存器相对位移**留给下方寄存器数据流恢复成字段。解析器，按顺序：① `appContext.MethodsByAddress[addr]`（精确起始）→ 托管方法（`call Cloth__base::checkRequirements`）；② **PE 导出表**（权威）—— 反射 `LibCpp2IlMain.Binary.LoadPeExportTable()` + `GetExportedFunctions()`（返回 `KeyValuePair<string,ulong>` name→VA；测试游戏上 **242 条**）成一张 addr→name 表；③ 关键函数 —— 反射 `appContext.GetOrCreateKeyFunctionAddresses()` 的 `ulong` 成员成一张 addr→name 表（`il2cpp_codegen_initialize_method`、`il2cpp_runtime_class_init_export`、…；这些 `il2cpp_codegen_*` wrapper **不**在导出表里 —— 已验证 —— 所以这个 Cpp2IL 启发式是它们唯一的来源）；③ `LibCpp2IlMain.GetLiteralByAddress(addr)` → 字符串字面量（实际的游戏文本），然后 `GetAnyGlobalByAddress(addr)` → `MetadataUsage`（`.Type`/`.Value`）拿 TypeInfo（`ds:[UnityEngine.Debug_TypeInfo]`）/ method / field global。对于没命中任何 metadata 的地址：先由 **PE 段表**（`ParsePeSections` —— 从 `GetByteAtRawAddress` 读头部解析各段 VA 范围 + 可执行/可写/是否已落盘）归类后决定标签：① **寄存器相对位移**（`[rcx+18h]`、`[r14+r8*8+46AF0h]` 里的位移）是字段/结构偏移、不是全局地址 —— x86 侧 Iced 直接按操作数种类判定（base 是 GPR），**要么被下方寄存器数据流恢复成 `; this.field` 注释、要么保留原样**，绝不误标 `g_`（ARM 文本回退路用 `IsRegisterRelativeDisplacement`：同一 `[...]` 内含 `+`/`*` 的同义识别）；② **常量池解引用**：X86 列表层（`Il2CppX86Listing.CollectDataConstants`）用 Iced 解出直接寻址内存操作数的元素类型+大小（浮点标量、向量、**及标量整数**），注解层经 `ConstantAddressAllowed` 仅放行落在**只读且已落盘段**（`.rdata`）的地址——`TryMapVirtualAddressToRaw`+`GetByteAtRawAddress` 把文件字节读成**实际值**（`movss xmm0,[360f]`、`mulsd [1.5d]`、整数 `[5h]`、`andps [{7FFFFFFFh x4}]` 位掩码），作为**最低优先级**的 `dataConstants` 传入（任何元数据命中一律优先）；③ 落在**可执行段**的括号地址（`lea` / 以数据形式引用的代码指针、跳转表项）→ `loc_`（方法体内）/ `sub_`（区域外），而非数据全局；④ **只读且已落盘段里的 C 字符串常量**（`TryReadCString`：NUL 结尾、全可打印 ASCII、长度≥2）→ 引号字符串 —— 把 il2cpp 存的 icall 签名（`lea rcx,["UnityEngine.Time::get_time()"]`）、版本串（`"Unity IL2CPP (Oct 23 …)"`）、调试串救回来（**非托管字面量，`GetLiteralByAddress` 命不中**，只能直读文件字节）；⑤ **已落盘数据槽里存着指向可执行段的指针**（`TryResolveCodePointer`：il2cpp 运行时 API 的函数指针表项 / vtable）→ `->目标符号`（`call qword ptr [->sub_1802178D0]`）；⑥ 其余（**运行期才填充、文件里无值**的 `.data`/`.bss` 槽 —— icall 缓存 / 元数据 once-flag / TypeInfo 缓存）→ `g_XXXX`（匿名 codegen global；**已实测**：其值不在文件、`GetAnyGlobalByAddress` 也命不中，是可用元数据的真实边界，绝不臆造）。④⑤ 均以 `ClassifyAddress` 段分类 + `TryMapVirtualAddressToRaw` 真实字节为据，结果按地址缓存（`_dataCache`）。非括号的代码目标照旧 `loc_`/`sub_`（例如 `1016BFE0` —— 被 ~46% 的方法引用，未命名的 il2cpp 运行时 helper，甚至不在 PE 导出表里）。两个**最常见**的匿名数据 global 被 `Il2CppX86Listing.DetectMetadataInitIdiom` 升级为语义名：逐方法 metadata-init 守卫 `cmp byte ptr [X],0 … mov byte ptr [X],1` → `method_init_flag`，以及 `call il2cpp_codegen_initialize_method` 之前压入的 token → `method_init_token`（作为逐方法 `overrides` 传给 `AnnotateLine`）。识别用的 `IsDirectMemoryOperand` 同时接受 32 位绝对 `[disp]` 与 64 位 RIP 相对两种直接寻址（64 位 il2cpp 实际用 RIP 相对；两者都以 Iced `MemoryDisplacement64` 解析出的绝对地址为键，与格式化器打印的绝对地址、常量池解引用一致）。对照 LibCpp2IL 确认过：它的 `Get*GlobalByAddress` 解析器**没有一个**能命名这些（它们不是 metadata usage），所以惯用法识别是唯一的把手。守卫 `addr < 0x10000` 跳过寄存器相对偏移和 8 位寄存器名（`ah`/`bh`）。注意 PE 导出表只携带公开的 `il2cpp_*` C API —— 内部 codegen helper（即便是 key-function 那些）**不**被导出，所以 `IsExportedFunction` 没法命名它们；这就是为什么 `sub_` 是诚实的标签。

**符号恢复（寄存器数据流，`Il2CppRegisterFlow` + `Il2CppTypeModel`）** —— 这是「更丰富的基本符号」的核心：把 IL2CPP 元数据里**本就已知**的东西（字段偏移、静态类、返回类型）从裸偏移还原成 `this.xxx`，而不是留一堆 `[rcx+18h]`。对每个 x86 方法做一趟**前向抽象解释**：① 按 IL2CPP/MSVC-x64（或 SysV）调用约定**给参数寄存器播种**（镜像 Cpp2IL 的 `Cpp2IL.Core\Utils\X64CallingConventionResolver.cs`：实例方法 `rcx=this`、`rdx/r8/r9`=前三个整型/指针参、浮点参走 xmm 但占同一 slot、**超 8 字节的值类型返回**走隐藏返回指针使 `rcx=返回缓冲`、`this`→`rdx`、尾随 `MethodInfo*`）；② 以**基本块数据流 + meet（前驱一致则保留、否则 Unknown）**在寄存器间传播 `TrackedValue`（`ManagedRef(type)` / `TypeInfo(Il2CppClass*)` / `StaticBase` / `Klass`）；③ 逐指令产出**尾注释**。恢复出的符号：**实例字段** `[rcx+18h] → ; this.groundDetector`（偏移经 `FieldAnalysisContext.Offset` 反查，走继承链、含 0x10 对象头），**链式** `; this.groundDetector.<SlopeAngle>k__BackingField`，**静态字段** `mov rax,[X_TypeInfo]; mov rcx,[rax+B8h]; mov edx,[rcx+off] → ; Type.staticField`（`offsetof(Il2CppClass, static_fields)` **由反汇编样本自动发现**、非硬编码 —— Raot/metadata-v24 上 = `0xB8`），**数组** `.Length`（+0x18）/ `[i]`（+0x20+i*8），**调用返回类型**（`rax` = 被调托管方法返回类型），**虚/接口调用** `mov klass,[obj]; call [klass+N] → ; -> Type::VirtualMethod`（用该类型自己的 `Il2CppTypeDefinition.VTable`，比 Cpp2IL legacy 的全局 slot 表更准；`offsetof(Il2CppClass, vtable)` 同样**自动发现** = `0x150`，已对 `Object.Equals`=slot0 核对；**一致性回撤 `RetractInconsistentArrows`**——元数据 `Il2CppTypeDefinition.VTable` 槽序与运行期内存 vtable 对某些偏移不符（接口/mis-mapped 槽）会把名串到别的方法，`method.slot==i` 过滤挡不住这批「自信错」，故加一趟**后处理**：按命名槽的**返回种类**（`Il2CppTypeModel.GetVirtualReturnKind`/`ClassifyReturn`：void/标量/结构/引用/IntPtr）挑一个**绝不会命中正确名**的矛盾测试——void|标量的 `rax` 被解引用 `[rax…]` 或整宽 `mov [m],rax` 存（非指针值做不到），或引用返回的偏移 0 被 `movsd xmm,[rax]` 读浮点（真对象槽 0 是 `Il2CppClass*`）——命中即证该 (类型,槽) 名错，**撤名降级** `T::class[0xNN]`（结构/IntPtr 返回 rax 本是合法指针→不测，真结构 getter `get_position→Vector3` 名保留）。两种分派形态都认（`mov reg,[klass+disp]; call reg` 与直接 `call [klass+disp]`，`FlowControl.IndirectCall`/`IndirectBranch`），连带撤配对的 MethodInfo 载入（`+8` 同槽）、按 (类型,槽) 全方法一致撤。全库扫（`_dumpprobe -- arrowcheck`）：**0 残留矛盾**（无过撤）——修的正是外部 AI 报的 `BurstTestingController`/`CharacterSelectionMenu`（void `RecalculateClipping` 结果却存进 string 字段）+ 标量/引用变体。**信号集经多轮对抗 agent 审计扩全**（按 ABI 结果位置分治,`ReturnKind` 拆 Void/ScalarInt/ScalarFloat/Bool/Ref/Struct/Pointer + 每槽形参数 + 每槽 ref 结果打 origin 标签):void/int/bool 的 rax 被解引用/整宽存/**全 64 位捕获** `mov reg64,rax`/当 `this`;float 名却 `movsd xmm,[rax]`;**xmm0 被读**(非 float 名=真返回 float);**`test al,al`**(非 bool 名=真返回 bool,真 bool 豁免);**MethodInfo 载 r8/r9**(0 形参名却有整型实参);**引用结果存进不相关具体引用字段**(`AreUnrelatedRefClasses` 保守,含直接 `call [klass+disp]` 形的 origin 追踪)。**全局根治**:`Il2CppTypeModel.CondemnedVtableSlots` + `Il2CppX86Listing.EnsureCondemnationScan` 一次性预扫全 Assembly-CSharp 填满再渲染,某 (类型,槽) 被任一站点证伪即全方法一致撤(确定性,与渲染序无关)。`Object.*` 基槽误标 553→~281,余下无可观测矛盾(结果丢弃/尾jmp)=诚实极限。**残余上游边界**(agent 确认非本 hook):折叠 RVA(两方法共享一 VA、ASM 忠实但 C# 声明配错=Cpp2IL 方法→RVA 分配) + `[StructLayout((LayoutKind)N)]` 枚举渲染 + 特性构造参数(v24 是 generator 函数,全在 Cpp2IL 包)。），**icall 惰性缓存槽** `[icall<UnityEngine.Time::get_time()>]`（`Il2CppX86Listing.DetectIcallCacheIdiom`：缓存槽 g_ 经近旁签名 C 字符串 + 同一槽读/写 once-cache 不变量命名），**对象分配** `mov rcx,[T_TypeInfo]; call il2cpp_codegen_object_new → ; rax = new T()`（并把 `rax` 标成 `new T` 供下游字段初始化解析），**虚调用返回类型**（`mov reg,[klass+N]`→`Callee(返回类型)`、`call reg`→`rax`=该引用返回类型，链下去），**Il2CppClass 结构读**（类初始化守卫 `test [klass+12Fh]`、`[klass+E0h]` 等）→ `; T::class[0xNN]`（TypeInfo/Klass base 兜底命名 owning 类型），**泛型实例字段**（`List<T>`/`Dictionary<K,V>` 无 Definition→`Unwrap` 到 `.GenericType` 取布局），**数组元素类型传播**（`arr[i]` 得元素引用类型，`arr[i].field` 链得下去）。**PE 镜像基址** `lea reg,[image_base]`（RIP 相对寻址的模基址计算）单列成 `image_base`，不再误标 `g_<base>`（Assembly-CSharp 里约 47 处）。**编译器异常抛出助手**（`Il2CppHelperNamer` —— 第三条符号层，`Il2CppAsmAnnotator.CodeLabel` 在 `sub_` 兜底前调用、结果按地址缓存）：il2cpp 为 `throw new XException(...)` 生成的按类型抛出小助手是 codegen 内部 C 函数、**无 global-metadata 身份**故本会永留 `sub_`；但每个助手把异常类型名作 C 字符串内嵌（`lea r8,["IndexOutOfRangeException"]; lea rdx,["System"]`）并经 il2cpp `object_new`/raise（`IsAllocOrRaiseFunction` 按关键函数语义名子串判、版本稳健）或**尾调用共享构造器**处置它。反汇编目标一趟、读出 bare `*Exception`/`*Error` 标识符（含空格的消息串被排除）、**仅当 body 也 alloc/raise/尾调用时**才命名 `il2cpp_throw_<Type>`（有据于助手自身字节、绝不猜）；泛型/共享助手（类型来自运行期 `[rcx]` 数据、无内嵌串，如 `sub_18023A080` 把类型名当参数收）正确留 `sub_`。全库对抗扫（`_dumpprobe -- throwscan`）：4973 个 `sub_` 助手、恰 3 个内嵌类型名（IndexOutOfRange/ArrayTypeMismatch/MissingMethod）**全部命中，0 误报 0 漏报**（独立 oracle `Disasm.EmbeddedExceptionName` 复核证无漏）。`inc`/`dec [field]` 渲染成 `; field++`/`--`，store 检测精确匹配操作数自身内存（`call [m]` 的返回地址压栈不算 store）。**回写失效用 Iced `InstructionInfoFactory` 精确计算写寄存器集** + 调用点叠 ABI volatile 集，所以残留旧类型**绝不会误标后续访问**（错标比漏标更糟 —— 宁缺毋滥）。值类型字段偏移是 0-based（`Vector3` x@0/y@4/z@8）、引用类型含 0x10 头，二者一致处理。整套只依赖 Cpp2IL 模型（**无 AR/ILSpy 依赖**），故能被隔离探针直接编译测试。**恢复天花板（实测,随机抽样审计 `_dumpprobe -- audit <seed> <N> [cs]`）**：global-metadata 的所有*符号种类*已全恢复；候选 managed-field 命中率 ~52%，残留是 (a) il2cpp 运行期 C 结构访问（`Il2CppClass`/`MethodInfo`,现已命名 owning 类型）、(b) 值类型/SIMD 批量拷贝/native 跳转表（本就非托管符号）、(c) 寄存器对象身份经任意计算（间接/icall 返回、值类型中转、控制流 join）丢失 —— (c) 是**静态数据流分析的固有极限**（反编译器同病、需 SSA/值编号/过程间分析），非 global-metadata 符号缺失。**栈槽跟踪试过 0 收益已回滚**（MSVC 用 callee-saved 寄存器存 this/局部，不 spill 到栈）。

**隔离验证**（不跑完整 AR 也能快速迭代）：一个 `net9.0` 控制台，它（a）复刻 `IL2CppManager` 的静态 ctor（注册 instruction set + `LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport()`）→ `DetermineUnityVersion` → `InitializeLibCpp2Il` 拿到 `CurrentAppContext`，并（b）`new WholeProjectDecompiler` 子类 override `CreateDecompiler` 来加变换，用一个文件夹 `IAssemblyResolver` 反编译一个哑 `Assembly-CSharp.dll`。从 Windows PowerShell 5.1 反射这些包会失败（它是 .NET-Framework；包面向 net9）—— 用一个 `dotnet run` 探针。经 HintPath 引用 `ICSharpCode.Decompiler` 指向构建输出 DLL（.8388 构建不在 nuget.org 上）。**符号恢复的更轻迭代探针**（不需要 ILSpy）：因为 `Il2CppX86Listing` / `Il2CppAsmAnnotator` / `Il2CppTypeModel` / `Il2CppRegisterFlow` / `Il2CppSymbolResolver` **只依赖 Cpp2IL 模型**，一个 `net10.0` 控制台可以直接 `<Compile Include>` 这五个真源文件 + `PackageReference AssetRipper.Cpp2IL.Core` / `Iced`(`Aliases="icedreal"`)，`InitializeLibCpp2Il` 后对挑出的 `MethodAnalysisContext` 直接调 `Il2CppX86Listing.Render(app, method)`，秒级看到某个类的字段/静态恢复结果（改源即重编真源、非拷贝）。这是打磨字段恢复的主循环；ILSpy 探针只在验证 AST 注入接缝时才需要。

---

## 12. CAB 虚拟文件 —— 名字索引 + bundle-granular 加载（按名导出资源+全依赖）

把整个游戏当作一张 **CAB 依赖图**来按需取用，而不是一次性把 21 GB 全载进内存。**一件自包含磁盘产物**（旧两件套仍可读），**两套并行的读写器（CLI `Ruri.RipperHook.CLI/Cli/CabMap.cs`（static、权威） + GUI `Ruri.RipperHook.GUI/Services/ExportCabMap.cs`）跨程序集各一份，格式 magic 必须同步**：

| 产物 | 格式 | 内容 |
|---|---|---|
| **CAB map** `<game>.cabmap` | **RCM3 `0x52434D33`（现行，自包含）** | CAB 名(hash) → (相对 .chk 路径, **chunk 条目文件名**, 依赖 CAB[], ClassID[], **AssetBundle Container 可读寻址路径[]**)。依赖图 + 名字一个文件全有——读一个文件即可用虚拟文件浏览器访问所有依赖。`--build-cab-map` 单趟合并扫描生成（RCM2 的死字段 offset 已删）。 |
| 旧格式兼容 | RCM2 `0x52434D32` + RNM2 `0x524E4D32` sidecar / 无头格式 | 读取端全兼容：RCM2 读到的 offset 被消费丢弃、名字回退 `.names` sidecar（CLI 在 `--names` 时对旧 map 自动补建一次）；无头格式无 ClassID。`--build-name-index` 仅为旧 RCM2 服务。 |

**为什么名字要进 map**：CAB map 全按内容 hash 索引，可读名（`assets/beyond/…/chr_0004_pelica/…`）只活在每个 bundle 的 **AssetBundle(142) 对象的 Container** 里——必须实际加载、解析 bundle 才看得到。EndField 每个 CAB 100% 含一个 142 对象。旧方案存成 `.names` sidecar（两件套、两次扫描）；RCM3 把名字并进 map 本体、单趟合并扫描一次拿全。

**合并扫描（廉价、有界内存）**：`GameBundleHook.AssetBundleOnlyFactory` 是个只物化 ClassID 142、其它类一律返 `null` 的 `AssetFactoryBase`，于是 `SerializedAssetCollection.FromSerializedFile`（反射调）只读那一个小对象，跳过 Mesh/AnimationClip/Texture 重负载。`GameBundleHook.ReadFullMetadata(sf, fileName)` = `ReadSerializedMetadata`（deps+ClassID）+ `ReadContainerNames`（条目名+Container 路径）单趟双投影，需要 `GameBundleHook.NameScanVersion`（EndField hook 设为 `endFieldClassVersion`）解析 142 的 source-gen 布局。`VirtualFileSystem.ScanChunk<T>(chkPath, project)` 是**单一**有界并行流式扫描器（逐 bundle 解密+解析+投影+即弃），`ScanChunkMetadata`/`ScanChunkNames`/`ScanChunkFull` 都是它的薄包装；EndField hook 接 `GameBundleHook.ScanChunk/ScanChunkNames/ScanChunkFull`。258k CAB 全扫峰值内存 ~3.5 GB。

**★最关键的坑：chunk 条目文件名 ≠ CAB 名，无法互转。** chunk 条目名 `fileInfo.fileName` = bundle 归档路径 `Data/Bundles/Windows/<initial|main>/<24位hex>.ab`；CAB 名 = `cab-<32位hex>`，来自 bundle **内部目录**里那个 SerializedFile 的 `NameFixed`（= `SpecialFileNames.FixFileIdentifier(内部名)`，小写）。二者是两套独立标识，`FixFileIdentifier(条目名)` 得到的是 `.ab` 路径不是 CAB。**所以名字索引必须给每个 CAB 记录它的 chunk 条目文件名**（连 Container 为空的 CAB 也记，否则 load 过滤拿不到它的条目名）——这是 RNM2 相比早期只存路径的关键加项。

**bundle-granular 加载（EndField 必须，否则 OOM）**：AR 加载一个 `.chk` 会经 `VirtualFileSystem.TryLoadChunkFiles → ExtractChunkFiles` 解出**该 chunk 里所有 bundle**——而 EndField 把 161k bundle 塞进单个 `.chk`（68B3B9B8…，1.8 GB）。一个角色的依赖闭包只要几千 bundle，整块加载必爆内存（13 GB 空闲下 24 GB+）。解法 = `GameBundleHook.LoadIncludeFile`（`Func<string,bool>?`，`null`=全载、不影响常规加载）；`ExtractChunkFiles` 用它在**解密前**按 chunk 条目名过滤，只解出闭包里那几千个。调用方：把闭包每个 CAB 经名字索引映射回 chunk 条目名，组成集合，`LoadIncludeFile = name => set.Contains(name)`，load 前置、`finally` 清。EndField 用例实测：pelica 闭包 4240 CAB 跨 20 chunk，big chunk 只取 ~2297/161113。

**CLI 流水**（`HeadlessRunner`）：`--cab-map <map> --names <regex>`（叠 `--hook EndField_1.2.4`）→ 载 CAB map → 若 `.names` sidecar 缺失/旧格式（`IsNameIndexCurrent` 查 magic）则自动 `BuildNameIndex` 一次 → `ResolveByNames`：Container 路径匹配 regex 的 CAB = 种子 → CAB map 前向 BFS 求依赖闭包 → 输出 (要加载的 .chk[], 闭包每 CAB 的 chunk 条目名集合) → 设 `LoadIncludeFile` → `handler.Load`（bundle-granular）→ 导出**整个闭包**。**名字驱动时导出侧不再叠 `--names` 的逐资产名过滤**（否则会把没带 pelica 名的依赖贴图/材质/网格丢掉）——要的是"pelica + 它的全部依赖"。`--names` 不配 `--cab-map` 时保持老语义（导出按 collection 名过滤）。

**GUI 虚拟文件预览（合并进单一 Asset List，不开新窗口）**：**虚拟文件 = Asset List 的另一种行，与已载实体资产等价**——别再起独立窗口（旧 `CabFileBrowser` + AssetMap `AssetBrowser` 已删）。MainForm 的 `assetListView` 是**单个虚拟模式 ListView，两种 backing**：`_listMode` ∈ {Assets, CabMap}，`assetListView_RetrieveVirtualItem` 按模式渲染。列 = Name/Container/Type/PathID/Source/Deps（**Size 列删**——CAB 无 size 读取，顺手省掉每资产 YAML 序列化估算）。同一个搜索框/类型过滤/排序/多选(ctrl/shift/跨行 + **Ctrl+A 走 native `LVM_SETITEMSTATE` iItem=-1**，虚拟模式无托管全选)/右键菜单服务两种模式（`assetListContextMenuStrip_Opening` 按模式切可见项）。`MainForm「Load CABMap」` → 载 map（RCM3 名字内联直接进 `_nameIndex`；旧 RCM2 才回退自动载同名 `.names` sidecar）→ `_allCabRows` 缓存 + `EnterCabMapMode()`（直接在主窗口 Asset List 显示全部 CAB，焦点切 Asset List tab）。CabMap 模式：单选在 Preview 面板显示 CAB 信息（hash/source/deps/容器路径，无 3D 预览）；右键「Load selected」=`LoadCabsScopedAsync`（bundle-granular 载闭包→切 Assets 模式，实体资产可 3D 预览/YAML；清搜索框显示全部已载；append 跨多次累积 `_scopedLoadFilter`）、「Export with dependencies」=`ExportCabsWithDepsAsync`（`ResolveScopedClosure`→设 `LoadIncludeFile`→复用 `RunFilteredExportAsync` 真导出→导出完 `EnterCabMapMode()` 回到浏览）。Assets 模式：右键「Export selected」(Converted/YAML) + 「Export with dependencies」(选中资产的源 CAB→闭包)。Scene tree 经 `_assetIndexByObjectKey`/`_nodeByObjectKey`(objectKey→index/node) 双向联动(虚拟模式无持久 AssetItem)。GUI 侧 `ExportCabMap`(Services) 加 `LoadNames`/`EnumerateCabRows`/`ResolveScopedClosure`，CAB-mode 逻辑在 `MainForm.AssetList.cs`。**名字缺失则做不了 bundle-granular**（闭包条目名为空→`LoadIncludeFile` 留 null→整块加载），大游戏会 OOM——RCM3 map 天生带名字没这个问题；只有旧 RCM2 map 需要 `.names` sidecar（CLI `--names` 会自动补建）。

**导出格式**：是一个**可重新导入的 Unity 工程**（`ExportedProject/Assets/…` 按原始寻址路径布局），不是 glTF：网格 → Unity 原生 `.asset`（`S_`静态/`SK_`骨骼），prefab → `.prefab`，贴图 → `.png`，材质 → `.mat`，动画 → `.anim` + AnimatorController 的 `.state/.transition/.statemachine/.blendtree`。别拿 `*.glb/*.fbx/*.mesh` 去找网格。`HeadlessRunner` 默认 `ShaderExportMode.Decompile`——大闭包里上百个着色器逐个反编译是导出耗时与内存的主要尖峰。

---

## 13. GlbExporter —— prefab/Animator 完整模型 GLB 导出（骨架/蒙皮/材质/morph/动画 + humanoid 烘焙）

`Source/Ruri.RipperHook/AssetRipperHook/GlbExporter/`（GameType `AR_GlbExporter`，hook id `AR_GlbExporter_`）：
替换 `GlbModelExporter.ExportModel`（PrimaryContent 路径）为 `RuriGlbSceneBuilder.Build` —— AR 原生 GLB 只有静态刚体网格，这里补全套。入口 = 选中的 prefab 或 Animator（GUI「Export selected (Converted)」经 `RipperPrimaryAssetExportService`；CLI `--export-glb <dir>` 自动启用本 hook）。**单 anim 不可用**：脱离 prefab/Avatar 无法还原 path_hash。

- **数据来源全是 AR 处理后的纯净模型**：曲线路径已被 `PathChecksumCache`（Avatar TOS + 层级 CRC32 反查，`AssetRipper.Processing/AnimationClips/`）在 EditorFormatProcessor 阶段还原；muscle 曲线已被 AR 的 `AnimationClipConverter` 命名成标准属性串（与 VibeStudio MuscleHelper 同源同串）。导出端零重新解码。
- **网格**：复用 AR internal `GlbSubMeshBuilder`（法线/切线/8UV/顶点色/Joints4/全拓扑 + 每 submesh 材质）。internal 访问 = csproj `InternalsAssemblyNames` + **`CompileUsingReferenceAssemblies=false`**（坑：SDK 对 ProjectReference 默认用 ref assembly 编译，会绕过 IgnoresAccessChecksToGenerator 重写的实现程序集 → CS0122）。
- **材质**：`RuriGlbMaterialCache` 镜像 AR GlbLevelBuilder 的私有材质半边（TextureConverter→PNG→MemoryImage，缓存），纹理属性名表数据驱动扩到 URP/游戏命名（_BaseMap/_AlbedoMap/…）。
- **morph**：Unity blendshape 通道→glTF morph target（每通道取末帧），`extras.targetNames` 带名字（Blender 直接出同名 shape key）；`blendShape.*` 曲线→morph 权重轨道（/100，按 SampleRate 采样）。
- **★SharpGLTF 骨架铁律：`AddSkinnedMesh` 要求骨架树内节点名唯一**（`IsValidArmature`；报错只有空消息 `ArgumentException (Parameter 'joints')`）。游戏层级必有重名节点 → 建树时全局唯一化命名（`_1` 后缀）。joint 重复、零缩放、根节点进 joints 都合法；**跨两棵根才非法**。蒙皮索引按 joint 数组、动画绑定按 Unity 路径字典，改名无损。
- **被 strip 的骨骼补全**：prefab 层级缺的骨骼从 Avatar 骨架长回来（`SynthesizeMissingAvatarBones`：TOS 路径 + DefaultPose 本地 TRS，父先子后顺序遍历）—— VibeStudio `ModelConverter.DeoptimizeTransformHierarchy`（ModelConverter.cs:1338）的移植。这就是"导出时获取所有动画依赖骨骼"。
- **humanoid 烘焙**（`HumanoidSolver/`）：`AvatarMuscleReferential`（Avatar Axes：PreQ/PostQ/Sgn/Limit + TOS 路径 + 全 95 肌肉 DOF 表：55 身体含眼颚 + 40 手指经 `Hand.HandBoneIndex` finger-major）+ `HumanoidClipBaker`（muscle 曲线按 SampleRate 采样 → `preQ*swingTwist*postQ⁻¹` 直接就是该骨骼**绝对** local rotation，不是 delta，不跟 rest 复合；hips = RootT−MotionT + RootQ 前乘 rest（已知错，见下）；MotionT/Q 烘到根节点，复合恒等于完整 RootT 轨迹）。
  - **★公式坑第一轮（真实事故，2026-07-10）**：最初两轮实现都用 `postQ*swingTwist*postQ⁻¹`（对称共轭）——数值自检（L/R 镜像对称、无 NaN、帧间平滑）全部通过，视觉上也"像"在走路，但**用真实 Unity Editor（unityMCP `execute_code` + `AnimationMode.SampleAnimationClip` 在活的 Avatar+clip 上采样真实骨骼旋转）核对后发现整条腿关节方向全反**——SaionNanae 测试角色膝盖肉眼可见反向弯曲。真相：`preQ` 和 `postQ` 在真实 rig 上根本不是同一个东西（同一骨骼二者可差 60°~280°），公式必须是**非对称**的 `preQ * swingTwist * postQ⁻¹`。

  - **★公式坑第二轮（真实事故，2026-07-10，同日，事后证伪）**：当时误以为还需要统一加一个 `normRest⁻¹` 前缀，且这个前缀是否需要因骨骼而异（`_NEEDS_NORM_REST_CORRECTION` 表）。用户反馈"手完全错的，穿模到身体里"后扩大验证到 15 根骨骼 × 11 帧 × Unity 真值，"表面上"验证通过（187 样本平均误差 6.78°），**但这份验证本身是假的**：Unity 侧取真值的 C# 脚本前后写了两版，早期一版打印的是 `cur`（骨骼绝对 local rotation），后期几版打印的是 `delta`（`rest⁻¹ @ cur`）——四根左侧四肢骨骼用的是 `cur` 真值，其余全部骨骼用的是 `delta` 真值，两种真值混在一张对比表里，"某些骨骼需要 normRest 修正、某些不需要"这个结论从数学上就不可能有意义。是另一个模型实例（Fable 5）复核这轮对话记录时发现的矛盾点，而不是靠更多现场测试查出来的——**验证脚本本身也需要被审计，不能默认"取真值的代码"没有 bug**。

  - **★公式坑第三轮（真实事故，2026-07-10，同日，最终定论）**：用正确、统一的 `delta` 真值重新采样后发现：`preQ*swingTwist*postQ⁻¹`（不加任何修正、不除以任何 rest）**直接就是骨骼的绝对 local rotation**，跟 `cur` 直接匹配，190 样本平均误差仅 4.93°，不需要任何逐骨骼表。中间还踩过一个更隐蔽的坑：曾经实现过"除以角色自身 rest 得到 delta，调用方再乘回 rest 复合"的写法，这个写法在**独立单元测试里验证通过**，但接入 `rest_char @ (rest_char⁻¹ @ raw)` 这个真实调用点后**代数上恒等于 raw 本身**——除法和调用方的乘法互相抵消，等于白做两次四元数运算；测试之所以"验证通过"，是因为"拿 `rest_char⁻¹@raw` 去比 `delta` 真值"在数学上等价于"直接拿 `raw` 去比 `cur` 真值"（两边同时左乘同一个 `rest_char⁻¹` 不改变两个四元数之间的夹角），所以测试测的其实还是对的东西，只是实现绕了一个没用的圈子。**教训：一个公式"孤立测试通过"不代表接进真实调用点后做的还是同一件事，必须连着调用点一起复核。** 最终形态：`AvatarMuscleReferential.LocalRotation`/`humanoid_retarget.py` 的 `local_quat`/`_axes_local` 无需传入任何 rest 参数，直接返回绝对旋转。`Left/Right Forearm Twist In-Out` 仍需对 twist 角度额外取反（双侧一致，根因未查明，按经验值保留）。残留已知缺口：`RightUpperArm` 仍平均 ~15° 偏差（大幅好于前两轮报告的 ~44°），穷举摆动合成顺序/符号/preQ-postQ 互换均未能进一步收敛，未查明根因，未修补。

  - **★根运动坑（发现于 2026-07-10，已于同日修复）**：用真实 prefab 摆放骨骼验证时发现——即使公式部分完全修好、每根骨骼**独立**的 local rotation 都已核对匹配 Unity 真值，渲染出的姿势仍然是手/腿甩到头顶上方，穿模。逐层排查（rest pose 校验、hierarchy.py 世界矩阵校验、Blender bone.matrix_local 校验，全部逐项对照 Unity 数值，逐一验证为零误差吻合）后定位到：**Hips 自己的世界位置/旋转就是错的**——旧版 `body_transform()` 直接把 `RootT`/`RootQ` 当成"Hips 骨骼自身的 local rotation"喂给 Hips 骨骼，但实测 Unity 里 Hips 骨骼自己的 rest local rotation 是精确单位阵、动画中的 local 变化也很小，`RootT.y` 却读到明显更大的值——两者对不上。
    - **根因（IDA Pro 反编译 Unity.dll 的 `mecanim::human::RetargetTo`/`HumanComputeBoneMassCenter`/`HumanComputeOrientation`/`HumanSetupAxes` 确认）**：`RootT`/`RootQ` 根本不是 Hips 自己的变换，而是 Unity 内部的"根参考系"——`RootT` 是全身 25 根人体骨骼按 `m_HumanBoneMass` 加权的**质心**（`HumanComputeBoneMassCenter` 逐骨骼取相邻骨骼中点/自身位置，质心再按质量加权平均），`RootQ` 是由肩心/胯心世界位置构造的**朝向系**（`up = normalize(肩心-胯心)`，`right = normalize(肩宽向量+胯宽向量)`，`forward = cross(right,up)`），且都是相对于 Avatar 自己 rest pose 下同一套计算结果（`m_RootX`，`HumanSetupAxes` 里只算一次）的差值。
    - **修复思路**：搭一个"假设 Hips 在原点、单位旋转"的 provisional FK（其余骨骼仍用各自当帧的 muscle 绝对 local rotation），对这个 provisional pose 算出质心与朝向系；把 Hips 当刚体的参考点，`质心(真实) = T + R @ 质心(provisional)`、`朝向(真实) = R @ 朝向(provisional)`，跟当帧 `RootT`/`RootQ`（以及 avatar 自己的 `m_RootX.q`）联立解出 `R`（Hips 真实旋转）与 `T`（Hips 真实位置）：`R = RootQ @ m_RootX.q @ 朝向(provisional)⁻¹`，`T = RootT - R @ 质心(provisional)`。
    - **验证**：朝向系公式对 avatar 自己的 rest pose 复算，与序列化的 `m_RootX.q` 误差仅 0.00002°（几乎位级吻合）；对 SaionNanae 一条真实 WalkLoop клип（10 帧，直接用 `AnimationClip.SampleAnimation` + `Animator.GetBoneTransform` 取 Hips 真值）多关节多帧核对，旋转平均误差 4.4°、Y/X 位置误差数厘米量级——同一档量级的改进幅度和 humanoid 肌肉公式的 4.93° 一致，视为已验证、可发布。
    - **已知残留限制（非本次回归）**：没有独立 `MotionT`/`MotionQ` 曲线的 clip（如测试用的 WalkLoop）仍会把角色整体前进的世界位移直接烘进 `RootT`，而这部分位移逻辑上应该记在动画根节点而非 Hips 身上；`body_transform()` 里已有的 `RootT - MotionT` 减法解决的就是这类问题，只是没有 `MotionT` 曲线的 clip 目前没法把这部分位移分离出来（沿走路方向出现残留漂移）——这是先于本次修复就存在的限制，不是本次公式引入的倒退。
    - Python (`humanoid_retarget.py` 的 `_provisional_fk`/`_mass_center_of`/`_compute_mass_center`/`_compute_orientation`/`body_transform`) 与 C# (`AvatarMuscleReferential` 同名方法 + `HumanoidClipBaker.BakeHips`) 已同步修复；`animation_builder.py`/`HumanoidClipBaker.BakeHips` 的调用方也同步去掉了"`body_transform` 返回值需要再乘 hips rest 朝向"的旧逻辑——新公式返回的就是 Hips 的最终绝对 local transform，和其余骨骼走同一套"直接用，不复合 rest"的契约。

Python (`RuriRipperImporter/humanoid_retarget.py`) 与 C# (`AvatarMuscleReferential.LocalRotation`) 两份实现已同步第三轮（最终）肌肉公式与根运动修复。**教训（四轮事故共同指向同一件事）：数值自洽（对称性/连续性/无 NaN）、单一测试骨骼吻合、甚至"扩大到 15 根骨骼验证通过"都不能证明 humanoid 解算全局正确——取真值的脚本本身、公式接入真实调用点之后的实际效果、以及公式背后的真实引擎语义（这次靠 IDA 反编译 Unity 自己的 native 实现确认，而不是继续经验拟合），都要跟结果本身一样被怀疑、被复核，多关节、多帧对 Unity 真值核对没有捷径。**
- **EndField 事实**：全部角色 avatar 是 generic（名字带 `_genericAvatar`，无 Human）——身体动画=逐骨骼 generic 曲线，muscle 空间只有 Root/Motion 14 通道；humanoid 烘焙对 EndField 正确休眠，将在真 Mecanim humanoid 游戏数据上首次生效（届时顺手核对 `HandBoneIndex[15]` finger-major 假设——对抗审计置信 medium-high 的唯一遗留）。角色模型入口 = `…/prefabs/uimodels/chr_XXXX_<name>_uimodel.prefab`（1.3.3 共 29 个）。
- **CLI**：`--export-glb <dir>`（配 `--cab-map`+`--names` 闭包加载；`--names` 同时过滤要写的 prefab；**绝不删目标目录**，只加文件）。`GlbBatchExporter` 遍历 `MainAsset is PrefabHierarchyObject`（注意 FQN：现行 AR 的 `Processing.Prefabs` 版，别撞恢复版旧类，FRAMEWORK §8）。
