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

**反汇编**：两条路。**x86（32/64）** → `Il2CppX86Listing.Render` 用 **Iced** 自己解码 `method.RawBytes`（所以它有每条指令的 `IP`），收集方法内近跳转目标，在每个目标处 emit 一行 `loc_<IP>:` 标签 —— 一份真正的汇编 listing，你能看到每个跳转落在哪。每条指令用一个本地 `MasmFormatter` 格式化（Cpp2IL 的是 `static` + 非线程安全；我们的是逐调用的）然后过 `Il2CppAsmAnnotator.AnnotateLine`。**其它一切（ARM/Disarm、WASM）** → 扁平的 `appContext.InstructionSet.PrintAssembly(method)` + `Il2CppAsmAnnotator.Annotate`（无标签）。经 `app.InstructionSet is X86InstructionSet` 分支。当 `UnderlyingPointer == 0`（抽象/extern）时逐方法跳过。
> **双 Iced 坑**：Ruri.RipperHook 引用了**两个**暴露 `Iced.Intel` 的 assembly —— 真 `Iced`（经 `AssetRipper.Cpp2IL.Core` 传递）和 `MonoMod.Iced`（经 `MonoMod.RuntimeDetour` 传递）。所以 `using Iced.Intel;` 会 `CS0433` 歧义。修法：显式 `<PackageReference Include="Iced" Version="1.21.0" Aliases="icedreal" />` + 在 `Il2CppX86Listing.cs` 里 `extern alias icedreal; using icedreal::Iced.Intel;`，并且那里别 `using System.Text`（它的 `Decoder`/`StringBuilder` 会再次冲突）—— 完全限定 `System.Text.StringBuilder`。**`X86InstructionSet.PrintAssembly` 用一个 `static MasmFormatter`/`StringOutput` → 非线程安全，而 `WholeProjectDecompiler` 并行反编译文件** → 把每次 `PrintAssembly` 串行化在一把锁下（持于 `Il2CppAsmLookup.GetDisassembly`）。只有 AR 实际*反编译*的 assembly（预定义的、Hybrid 下如 `Assembly-CSharp`；`Decompiled` 下的一切）才拿到 asm；`Save` 模式的 assembly 原样 emit 成 DLL。仅 IL2CPP（由 `CurrentAppContext != null` 守卫），opt-in，Settings→Features 复选框 `AR_Il2CppMethodDump_`。

**符号注释**（`Il2CppAsmAnnotator`，在同一把锁下对每个 `PrintAssembly` 结果运行）：原始地址毫无意义（`call 10278DB0h`），所以我们**就地替换每个 hex token**（MASM `…h` 或 `0x…`）成它的符号 —— 不保留地址（用户明确想要纯符号、省 token 的输出）。解析器，按顺序：① `appContext.MethodsByAddress[addr]`（精确起始）→ 托管方法（`call Cloth__base::checkRequirements`）；② **PE 导出表**（权威）—— 反射 `LibCpp2IlMain.Binary.LoadPeExportTable()` + `GetExportedFunctions()`（返回 `KeyValuePair<string,ulong>` name→VA；测试游戏上 **242 条**）成一张 addr→name 表；③ 关键函数 —— 反射 `appContext.GetOrCreateKeyFunctionAddresses()` 的 `ulong` 成员成一张 addr→name 表（`il2cpp_codegen_initialize_method`、`il2cpp_runtime_class_init_export`、…；这些 `il2cpp_codegen_*` wrapper **不**在导出表里 —— 已验证 —— 所以这个 Cpp2IL 启发式是它们唯一的来源）；③ `LibCpp2IlMain.GetLiteralByAddress(addr)` → 字符串字面量（实际的游戏文本），然后 `GetAnyGlobalByAddress(addr)` → `MetadataUsage`（`.Type`/`.Value`）拿 TypeInfo（`ds:[UnityEngine.Debug_TypeInfo]`）/ method / field global。对于没命中任何 metadata 的地址：一个**方括号检查**（token 之前的 `[...]` 深度）区分数据 vs 代码 —— 未解析的**数据**操作数先尝试**常量池解引用**：X86 列表层（`Il2CppX86Listing.CollectDataConstants`）用 Iced 解出该内存操作数的元素类型+大小，对**浮点标量与向量常量**（`andps`/`xorps` 等位运算的操作数是位掩码、按十六进制渲染）经 `Binary.TryMapVirtualAddressToRaw`+`GetByteAtRawAddress` 把文件字节读成**实际值**（`movss xmm0,[360f]`、`mulsd [1.5d]`、`andps [{7FFFFFFFh x4}]`），作为**最低优先级**的 `dataConstants` 传给 `AnnotateLine`（任何元数据命中一律优先）；只有还原不出（映射不到文件实体字节，或**标量整数**——其字节可能是运行期才填充的全局指针而非常量）才退回 `g_XXXX`（匿名 codegen global —— metadata 没有它的名字）；未解析的**代码**目标，如果地址严格落在两个连续方法起始之间（方法内跳转目标）就变成 `loc_XXXX`，否则 `sub_XXXX`（未命名的 il2cpp 运行时 helper，例如 `1016BFE0` —— 被 ~46% 的方法引用，不是托管方法，甚至不在 PE 导出表里）。两个**最常见**的匿名数据 global 被 `Il2CppX86Listing.DetectMetadataInitIdiom` 升级为语义名：逐方法 metadata-init 守卫 `cmp byte ptr [X],0 … mov byte ptr [X],1` → `method_init_flag`，以及 `call il2cpp_codegen_initialize_method` 之前压入的 token → `method_init_token`（作为逐方法 `overrides` 传给 `AnnotateLine`）。识别用的 `IsDirectMemoryOperand` 同时接受 32 位绝对 `[disp]` 与 64 位 RIP 相对两种直接寻址（64 位 il2cpp 实际用 RIP 相对；两者都以 Iced `MemoryDisplacement64` 解析出的绝对地址为键，与格式化器打印的绝对地址、常量池解引用一致）。对照 LibCpp2IL 确认过：它的 `Get*GlobalByAddress` 解析器**没有一个**能命名这些（它们不是 metadata usage），所以惯用法识别是唯一的把手。守卫 `addr < 0x10000` 跳过寄存器相对偏移和 8 位寄存器名（`ah`/`bh`）。注意 PE 导出表只携带公开的 `il2cpp_*` C API —— 内部 codegen helper（即便是 key-function 那些）**不**被导出，所以 `IsExportedFunction` 没法命名它们；这就是为什么 `sub_` 是诚实的标签。

**隔离验证**（不跑完整 AR 也能快速迭代）：一个 `net9.0` 控制台，它（a）复刻 `IL2CppManager` 的静态 ctor（注册 instruction set + `LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport()`）→ `DetermineUnityVersion` → `InitializeLibCpp2Il` 拿到 `CurrentAppContext`，并（b）`new WholeProjectDecompiler` 子类 override `CreateDecompiler` 来加变换，用一个文件夹 `IAssemblyResolver` 反编译一个哑 `Assembly-CSharp.dll`。从 Windows PowerShell 5.1 反射这些包会失败（它是 .NET-Framework；包面向 net9）—— 用一个 `dotnet run` 探针。经 HintPath 引用 `ICSharpCode.Decompiler` 指向构建输出 DLL（.8388 构建不在 nuget.org 上）。
