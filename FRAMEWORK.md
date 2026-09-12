# Ruri-RipperHook — 框架事实

> 约束/铁律 = [CLAUDE.md](CLAUDE.md)。本文只放**事实与坑**：类型名、方法签名、流水线顺序、数值、判据。
> **维护指令**：这是快照不是圣经。发现说法过期 → 同一次编辑里改对；发现某段描述的 workaround 已有官方 API → 换掉并删旧路径；踩到新坑 → 加一条。错的/过期的文档比没有更糟。写事实,不写经过。

---

## 1. 构建 / 运行

| 需求 | 命令 |
|---|---|
| 编译 | `dotnet build -c Debug --nologo`（整解决方案；单项目 `dotnet build Source/Ruri.RipperHook/Ruri.RipperHook.csproj` 只用于迭代，见下） |
| exe 目录 | `Source/0Bins/Debug/`（`Ruri.RipperHook.CLI/.GUI.exe`、`Ruri.FModelHook.CLI/.GUI.exe`、`FModel.exe`、`Ruri.Tpk.exe` 全在这一个目录） |
| 为何整编 | 两个读取器共用这一个输出目录，共享包取哪个版本由**构建顺序**定：FModelHook 引用内核，restore 图是超集，整编按引用图排序后十个共享包全落在最高版本；单独再编内核项目会把它自己较窄的那几个覆盖回去 |
| 列 hook | `Ruri.RipperHook.CLI.exe --list-hooks`（JSON，退出码 3） |
| 无头导出 | `--hook <Id> --load <path> --export <dir> [--fail-fast false] [--log-level Info]`（写入前清空 `--export`） |
| GUI 锁 DLL | `Get-Process Ruri.RipperHook.GUI -EA SilentlyContinue \| Stop-Process -Force` —— 仅拷贝失败时 |
| GameType | `Source/Ruri.RipperHook/Core/GameType.cs`，成员名 = 该游戏 player 的 productName（§7）；特性不进此枚举 |

---

## 2. Ruri.Hook attribute

| Attribute | 用途 |
|---|---|
| `[RetargetMethod(typeof(T), name, isBefore, isReturn)]` | IL 注入。`true,true`=prefix-replace(跳过原方法)；`true,false`=prefix-continue；`false,false`=postfix-continue |
| `[RetargetMethodFunc(typeof(T), name)]` | 完整 IL manipulator，签名 `static bool Foo(ILContext il)` |
| `[RetargetMethodCtorFunc(typeof(T))]` | patch 无参 ctor，签名同上；用于翻默认字段值 |

- **postfix-continue 在只有单个 `Ret` 的实例 `void` 方法上不可信**：`while(TryGotoNext(Before, Ret))` 实测误触发。行为等价时改用 prefix-continue。
- **继承发现坑**：`Registry.ApplyTypeHooks(GetType())` 用的 `GetMethods` 没有 `FlattenHierarchy` → **继承来的 `public static` 方法不返回**。基类/公共 partial 上的 `[RetargetMethod]` 不会被派生 hook 拾取，需在派生的 `InitAttributeHook()` 里显式 `Registry.ApplyTypeHooks(typeof(MyCommon_Hook));`（范例 `Arknights_2_7_31_Hook`）。
- **生命周期**：`Bootstrap.ApplyHooks(config)` → `RuriHook.ApplyHooks` 实例化匹配 `config.EnabledHooks` 的类型 → `Initialize()` → `RipperHookCommon.Initialize` 先 `InitAttributeHook()` 再注册 `RuriRuntimeHook`。

### ⚠️ 上游改了、这边不报错 —— hook 怪问题一律先查这条

整体替换 = 留下**写 hook 当天的行为快照**。上游往原方法加一步后三道防线全不响：签名没变、方法还在、行为照旧。**实付代价**：上游给 block 读取加了一次虚拟文件系统分配，替换版没有，不报错，直接 OOM。

**闸门**（`HookTargetFingerprint` + `HookBaselines`）：装 detour 前算目标方法 IL 指纹，比对 `Source/Ruri.Hook/HookBaselines.json`。指纹 = opcode 序列（跳过 `nop`）+ 每条指令引用的成员名，**不含 metadata token 与分支偏移** → 上游重编译但没动该方法不误报。

**★基线按构建配置分开存**（`{"optimized":{…},"unoptimized":{…}}`）：同一份源码 Debug 与 Release 的 IL 不同（Debug 留 `nop`、多余 `br`、`stloc/ldloc` 往返，Release 优化掉）。实测拿 Debug 基线跑 Release 构建**41 条里 21 条误报**——这种闸门必被无视，比没有更糟。配置**按声明程序集逐个判**（`DebuggableAttribute.IsJITOptimizerDisabled`），因为一次运行里 NuGet 交付的程序集恒优化、源码构建的随解决方案配置走（当前 41 个目标：17 个只有 optimized、24 个两套都有）。**换配置跑之前先 `RURI_HOOK_BASELINE=1` 录一遍那个配置**，否则只是 `have no baseline` 提示、不是警告。

| 黄字 | 处置 |
|---|---|
| `Upstream rewrote <方法> since the hook '<id>' was written` | 方法体变了，带新旧指纹 |
| `N hooked upstream method(s) have no baseline` | 该目标从未录基线 |
| `X.Y has N overloads and the hook names none of them` | 绑哪个重载由反射返回顺序决定 → attribute 补 `typeof(...)` |
| `'A' was relaxed to 'B'` | 省略方法名被放松匹配，上游改名会静默绑到别处 → 显式写目标名 |

**顺序不可颠倒**：先 `git -C AssetRipper log -p <上游文件>` 读该方法 diff、判断要不要跟，再 `RURI_HOOK_BASELINE=1` 重录基线并提交。**反了 = 把真 bug 盖章成正常。**

---

## 3. AssetRipper 数据流（冻结）

```
SchemeReader.LoadFile → FileBase（FilePath 由 Scheme<T>.Read 设）
  bundle: ReadFileStreamData 加 ResourceFile（FilePath=bundle.FilePath, Name=entry.Path=CAB）
  FileContainer.ReadContents → ResourceFile 提升为 SerializedFile，保留 FilePath
GameBundle.FromPaths → SerializedBundle.FromFileContainer → bundle.AddCollectionFromSerializedFile（此处丢 container.FilePath）
  → SerializedAssetCollection.FromSerializedFile（设 Name=file.NameFixed，从不设 FilePath）
    → ReadData 遍历 file.Objects → factory.ReadAsset(assetInfo, ObjectData, type, refTypes) → collection.AddAsset
ExportHandler.Process → GetProcessors() 顺序：
  SceneDefinition → OriginalPath → MainAsset → AnimatorController → AudioMixer
  → EditorFormat → LightingData → Prefab → Sprite → ScriptableObject
ExportHandler.Export → ExportCollections 写 YAML/贴图
```

**死胡同**：
- `ObjectInfo.ObjectData` / `byteSize` load 后即 GC，只能在 `SerializedAssetCollection.ReadData` postfix 拿；AR 不保存到任何地方。
- `AssetCollection.FilePath` 可设但 AR 从不设 → hook `ReadData` postfix 做 `collection.FilePath = file.FilePath`。
- 二进制 `asset.Write(AssetWriter)` 对 source-gen 类可用但很慢，**禁在 BuildAssetList 期间调**。
- **Bundle 重建不支持**（`FileStreamBundleFile.WriteFileStreamData` 抛 `NotImplementedException`、`ArchiveBundleFile.Write` 抛 `NotSupportedException`、无 YAML reader）→ 那种场景用 `AssetsTools.NET`（已 PackageReference）。

---

## 4. Path / OriginalPath

- `OriginalPath` setter 存 fullPath；`OriginalDirectory/Name/Extension` 经 `Path.*` 派生（Windows 派生字段是反斜杠，**存储值保留正斜杠**）。
- `GetBestDirectory()` = `OverrideDirectory > OriginalDirectory > "Assets/{ClassName}"`。
- `OriginalPathProcessor` 遍历每个 `IAssetBundle.Container`，给 `Asset.FileID == 0` 的条目设 `OriginalPath`。
- `BundledAssetsExportMode` 默认 `DirectExport`（只经 `OriginalPathHelper.EnsureStartsWithAssets` 加 `Assets/` 前缀，保留斜杠）；`GroupByBundleName` 走 `Path.Join` → 反斜杠。

**合成 Container 条目**（Arknights 路径修复）：
```csharp
AccessPairBase<Utf8String, IAssetInfo> pair = bundle.Container.AddNew();
pair.Key = new Utf8String(myForwardSlashPath);
pair.Value.Asset.SetAsset(bundle.Collection, asset as IObject);  // 需 IObject，不是 IUnityObjectBase
```
把 `OriginalPathProcessor.Process` 作 **prefix-continue** hook，让 AR 自己消费我们的条目。跳过已在 Container 里的、跳过 `IAssetBundle` 本身、跳过非 `IObject`。

---

## 5. Source-generated 参考

只读源码镜像（grep 用）：`D:\Ruri\Git\FractalTools\AssemblyDumper\AssetRipper\SourceGenerated\` —— 接口在 `Classes/ClassID_<N>/I<Class>.cs`，实现在 `<Class>_<version>.cs`。核对签名（`IMonoBehaviour.GameObjectP`、`IAssetBundle.Container`）用它。

---

## 6. 自定义 IAssetProcessor 注入

`Utils/Hook/ExportHandlerHook.cs` 对 `ExportHandler.GetProcessors` 下 ILHook，在每个 `ret` 处 `EmitDelegate` 接管返回值 —— **上游流水线原样跑，本仓库一行不复制**，只在返回序列上插入。`ExportHandler.Process` 完全不碰。

```csharp
RegisterModule(new ExportHandlerHook());
ExportHandlerHook.Register(new AssetProcessorRegistration
{
    InsertBefore = typeof(LightingDataProcessor),
    Factory = MyDelegate,   // MyDelegate(FullConfiguration s) => yield return ...
});
```

- **禁抄上游 processor 表**：曾用手工镜像，漏了 `OriginalPathProcessor` → 全闭包资产 `OriginalPath` 为 null → `ImportReachable` 的 seed→guid join 全断，整场景静默导入为空（base01_lv001：1619 个 placement 全 UNRESOLVED、0 source）。镜像 = 第二真源。
- **位置是注册方的事实**：`InsertBefore` 由注册方声明，共享代码 grep 不出任何具体 processor 类型。当前三个 hook 都锚 `LightingDataProcessor`（= 上游 `//Static mesh separation goes here` 处）。
- 锚点消失时 `Splice` **抛异常并点名注册方**，绝不静默丢弃。
- `Settings` 按类型反查（唯一那个 `FullConfiguration` 属性），不用属性名字符串。
- `Register` 按 `Factory` 委托幂等 —— hook 停用后重启用会再跑 `InitAttributeHook`（Blender 切游戏 tab 就会），否则同一 processor 越堆越多。

---

## 7. 解码器与特性（正交）

| | 解码器 decoder | 特性 feature |
|---|---|---|
| attribute | `[RipperHook(GameType.X, "游戏版本", "引擎版本")]` | `[RipperFeature("Name")]` |
| 语义 | 一个游戏 | 宿主能力，与游戏无关 |
| id | `产品名_版本` | 就是那个名字（无 `AR_` 前缀、无尾下划线） |
| 并存 | 一个进程只活一个（`RuriHook.ApplyHooks` 保证） | 互不排斥 |

区别是 **attribute 类型**，不是 flag+前缀 —— 没有宿主再 `StartsWith("AR_")`。唯一反射目录 = `HookCatalog`（一次扫描，`AssemblyLoad` 时失效重建）。

**特性只保留给 AR 原生支持之上的扩展。** 新增前先 grep `ProcessingSettings`/`ExportSettings`/`ImportSettings` 找等价属性；已有带合理默认值的属性就用 `[RetargetMethodCtorFunc]` 翻默认值或暴露成 Settings 控件，别发并行特性。
- 活着的特性：`SkipStreamingAssetsCopy`、`SkipProcessingAnimation`、`ShaderDecompiler`、`PrefabOutlining`、`StaticMeshSeparation`、`Il2CppMethodDump`（§12）。
- 已删（原生默认够用）：`BundledAssetsExportMode`。

**宿主呈现**：WinForms GUI 解码器进 Hooks 树（互斥）、特性进 Settings「Features」组（同一个 `HookConfig.EnabledHooks`，只是从树里隐藏），首次运行默认值在 `GUI/Program.cs:Main` 种下、门控 `!File.Exists(configPath)`；Blender 插件**特性不出现在 UI**，由 `RipperBlenderBridge.HostFeatures` 一处常量无条件启用（当前只有 `HumanoidToGeneric`），解码器按安装身份自动解析（§8），每 tab 一个。名字写错 = 启动即抛。

---

## 8. 身份探测与解码器解析

真源是**游戏自己发布的那一个文件**，整合包目录名/cabmap 文件名/用户勾选一律不算。

| 字段 | 来自 |
|---|---|
| `Company`/`Product`/`GameVersion` | `globalgamemanagers` 里 **ClassID 129 (PlayerSettings)** 对象的自有字节 |
| `EngineVersion` | 同一文件的序列化头 |

**`app.info` 不参与身份**（引擎写的副本，实测会改写真值：`illusion\Koikatu`→`illusion__Koikatu`；Endfield 公司在那里是 `Gryphline`，PlayerSettings 是 `Hypergryph`）。

- `Core.Install.InstallProbe`：`Read(root)` 每 player 一行；`Project(root)` 挑出本安装是哪个 player —— 规则只用安装自己的字段（companyName 以哪个 product 结尾、哪个 product 被别的当前缀扩展），**不查已知游戏名单**。一个安装常带多个 player（本体+VR+Studio）。
- 定位 PlayerSettings：用 AR 的 `SchemeReader`/`SerializedFile` 解析头+类型表+对象表，取 `ClassID==129` 的 `ObjectData` —— **精确字节，不是搜文件**。AR 不生成 ClassID 129 的类，故按写入顺序读长度前缀字符串：companyName、productName、其后第一个版本形状的是 bundleVersion。实测 5.6.2/5.6.3/2019.4.9/2019.4.40/2021.3.x 共 10 个 player 全中。**这是唯一一处按顺序取值**；彻底结构化需给 class 129 备类型树（stock tpk 有）。
- **自己文件被变换过的 build**：类打 `[RipperEngineFile]` + `public static bool TryDecrypt(byte[] data)`（认自己的 magic、原地还原、返回 true）。由已有的 `HookCatalog` 扫描顺带收集，**不按游戏索引**（读它就是为了知道是哪个游戏，按游戏索引成环），generic 解析失败时逐个问。探测早于任何 hook 应用，故是纯静态方法。打了属性没那个方法 = 第一次探测就抛。
- `HookCatalog.Resolve(product, gameVersion, engineVersion)`：该 product 的解码器里**最新且 `Version` ≤ 本 build 游戏版本**的那个，排除声明了别的引擎版本的。解码器 `Version` = **它从哪个游戏版本起适用** → 补丁没改坏就不加类。任一侧未知不构成约束。手动指定永远压过解析。
- 桥出口 `ReadInstall(root)`/`ListDecoders()`/`ResolveDecoder(...)` 只要 CLR，不要 session/cabmap/hook。

**EXILIUM 变换已 1:1 移植**（`EXILIUMCommon_EngineFileDecryptor`）：只变换开头 **0x3EA (1002)** 字节（头+引擎版本串+类型表+对象表），其后磁盘上本就是明文（所以从前能裸读到 `SunBorn`/`EXILIUM`/`2.7` 却解析不了头）。算法：认 magic（`4E 50` 或 `3D A?`，盖掉的正是恒为 0 的 metadataSize 高两字节）→ 按首个 body dword 低 2 位四选一算 seed → **改造版 RC4**（每密钥字节先 `ror8(K,2)` 再 `+0x3A`）→ 自定义 CRC（步进 `(c^0x09823D6E)>>1`）从尾 8 字节推 key → 前 5 个 32 字节块按 `blk%3` 三种变换，每块拿上一块第 0x1C 字节当下块 key。
- **入口怎么找的**：`NEP2.dll` 唯一导出 `NEP_Unknown` 是 `ret` 桩；游戏靠它在 **stock `UnityPlayer.dll` RVA `0x822280`（`FileStream::Read`）** 上打 inline jmp。解密函数在 **NEP2 RVA `0x4B0880`**，它和被调用者是未混淆静态代码（磁盘映像==运行时映像）。静态搜常量永远搜不到（那些 AES/SM4/MD5 表是静态链接的 OpenSSL 1.1.1 + Lua VM，属 FairGuard 授权层，与文件密码无关）。移植结果与 DLL 输出全文件逐字节一致。
- **引擎文件与 bundle 是两个编辑器版本打的**：引擎文件 `2019.4.40f1`（与 exe 版本资源一致），LocalCache bundle `2019.4.29f1`（StreamingAssets 的 bundle revision 被抹成 `0.0.0`）。身份取前者，hook 内解析 bundle 的 `ImportSettings` 版本用后者，**两者是不同事实，别互相覆盖**。

---

## 9. 恢复旧 AR 代码的危险清单

- `IMonoBehaviour.IsSceneObject()` 已移除 → `monoBehaviour.GameObjectP is not null`（ScriptableObject 的为 null）。
- 类型名冲突需 FQN：`AssetRipper.Processing.PrefabProcessor`（恢复的）vs `AssetRipper.Processing.Prefabs.PrefabProcessor`（当前内置）。
- `LibraryConfiguration` → `FullConfiguration`。
- 旧代码的 `: RipperHook`（那是**命名空间**不是类型）+ `AddExtraHook(...)` 都不存在 → 改 `: RipperHookCommon` + `[RipperHook(GameType.X)]` + `RegisterModule(new ExportHandlerHook())`（镜像 `StaticMeshSeparationHook`）。

---

## 10. Logger sink

`AssetRipper.Import.Logging.Logger` 是全局静态 + `List<ILogger>`，**没 sink 就什么都不做**。
- CLI：`HeadlessRunner` 接 `StderrLogger`+`FileLogger`。
- GUI：`Program.cs` 在 `Bootstrap.InstallAssemblyResolver()` 之后接 `ConsoleLogger()`；缺它则加载期所有 `Logger.Info` 静默，只有 hook 的 `Console.WriteLine` 漏出来。
- FModel GUI：`ConsoleLogSinkHook` 在 `App.OnStartup` 后重配 Serilog（FModel 只在 `#if DEBUG` 加 Console sink）。

---

## 11. 运行时类型树（`RuriTypeTree.tpk`）

游戏的 Unity 类型模型是**数据**不是生成 assembly：`Source/Ruri.RipperHook/Libraries/RuriTypeTree.tpk`（0.15 MB，`EmbeddedResource`），由 `Core.TypeTree` 运行时解释直接读进 stock `AssetRipper.SourceGenerated` 对象。（旧方案是跑 AssemblyDumper 60+ pass 生成 53 MB 孪生 DLL + 逐资产 deep-copy。）

| 部件 | 角色 |
|---|---|
| `Source/Ruri.Tpk` | 打包器：TypeTree JSON 输出目录 → `RuriTypeTree.tpk` |
| `Core.TypeTree` | 解释器：`TypeTreeDatabase` 载 tpk，`TypeTreeReadPlan` 为每个 (class, 引擎版本, AR 类型) 编译缓存读取计划 |

- **读取语义真源**（改之前必读）：`AssemblyDumper` 的 `Pass100_FillReadMethods`（节点分派/count-then-loop/`Capacity` 收缩/align 位置）、`Pass015_AddFields`（字段名=消毒后节点名）、`Pass002_RenameSubnodes`（只有两处影响字节：`ValidNameGenerator.GetValidFieldName`、`ChangeStringToUtf8String` 把 align 从内层 `Array` 抬到 string 节点）。
- **绑定**：AR 字段名 = 消毒后节点名（`m_SubMeshes`、`m_MeshMetrics_0_`），可见性 `internal`，引用型字段 ctor 已预分配 → 计划直接绑字段，`DynamicMethod` 访问器避免基元装箱。stock 类没有对应字段的节点按树结构消费掉字节。
- **打包**：`dotnet build Source/Ruri.Tpk/Ruri.Tpk.csproj -c Debug` 再跑 `…/Source/0Bins/Debug/Ruri.Tpk.exe`（无参 ⇒ 输入 `D:\Ruri\Git\FractalTools\TypeTree\output`，输出 `Libraries/RuriTypeTree.tpk`）。迭代可用 `RURI_TYPE_TREE_TPK` 指向别的 tpk。

**tpk 表达不了的三类偏差**（Unity 类型树无条件，每节点必序列化）—— 都是 `[Since]` 竞争解析的 capability，禁在共享代码加游戏分支：

| attribute | 用途 | 例子 |
|---|---|---|
| `[TypeTreeNodeGate(classID, nodePath, Captures=[...])]` | 条件节点 | Endfield Mesh 的 `m_CompressedMesh` 仅在 `m_CollisionMeshBaked` 为假时写 |
| `[TypeTreeValueFix(classID, nodePath)]` | 值改写 | Endfield `m_MeshCompression==4` 归一成 0 |
| `[TypeTreePostRead(classID, Slot, Captures=[...])]` | 读后解码 | ACL 解压、`m_TOSData`→CRC32→`m_TOS`、shader blob 上提 |

stock 类无处安放的私有节点在 `Captures` 声明后捕获成 `TypeTreeValue`（标量/字节数组/序列/结构），由 gate 与 post-read 经 `TypeTreeReadContext` 取用；没声明的只消费字节。路径 = 从类根起、消毒后节点名以 `/` 连（`m_MuscleClip/m_Clip/m_Data/m_DenseClip/m_ACLArray`）。

**输入数据集**
- `D:\Ruri\Git\FractalTools\TypeTreeDumps` —— 官方 dump，1384 个版本，`InfoJson/<ver>.json`。规范真源。
- `D:\Ruri\Git\FractalTools\TypeTree` —— 分叉引擎树。文件夹按 `CustomEngineType` id 命名（`1`=Houkai、`2`=StarRail、`5`=Endfield），每个 `<gamever>/info.json`；`RazTreeConverter.py` → 扁平 `output/`（`{maj}.{min}.{build}x{id}`，`x`⇒`Experimental`，`TypeNumber`=引擎 id）+ 拷 `Common/*.json`。**`output/` 是产物且 gitignore；`Common/` + `1,2,5/` 才是真源 —— 「补全数据集」= 填 `Common/`。**
- `Core/CustomEngineType.cs` —— 引擎→id，存进版本 `TypeNumber`（byte ≤255）。

**版本模型**
- `UnityVersion` = `Major.Minor.Build` + `Type` + `TypeNumber`；真实 dump = `Final`/`Beta`，**自定义覆盖层 = `Experimental`**（可靠判别依据）。
- `Pass000_ProcessTpk`：`MinimumVersion=3.5.0`；`MakeVersionRedirectDictionary` 按相邻差异把版本吸附到边界；丢弃 ID 100000-100011 与 `129`。
- `TypeTreeTpkBuilder.Create`：版本排序；`CommonString` = 只追加、前缀一致的并集（索引不匹配就抛）；类只在 dump 变化时 emit（奇点压缩）；**类在某版本缺席 = null 标记 = 在此被移除**。
- `SharedState.GetGeneratedInstanceForObjectType`/`ClassGroupBase.GetInstanceForVersion` 做**精确**版本范围匹配，无覆盖即抛。

**自定义引擎是覆盖层不是快照** —— 分叉游戏在某基础 Unity 版本之上发布**部分**树。Endfield（id 5，基础 2021.3）是 ECS：发布叶子组件 + `MonoBehaviour(114)` 但**丢掉整条抽象链**（`GameObject(1)`/`Transform(4)`/`Component(2)`/`Behaviour(8)`/`Renderer`/`Collider`/`Joint`/`Effector2D` 等 ~15 个基类，被 100+ 叶子引用）。StarRail（id 2）在 2019.4.210+ 发布精简版 `UnityConnectSettings(310)`（6 字段）。

**`ArAssemblyDumperHook` 现存 4 个 hook**（原 6 个）：
- 已删 `Pass005.GetClass`（Endfield 断掉的祖先链 —— 覆盖层规则让 Experimental 版本不对省略的类打 null 标记后，祖先前向携带、基类精确解析）。
- 已删 `Pass555`（期望 113 个 common string，数据集顶到 112；加入 `6000.5.0a8`——1384 个 dump 里第一个有 113 个的——即满足）。
- **保留** `SharedState.GetGeneratedInstanceForObjectType` + `ClassGroupBase.GetTypeForVersion` 的最近版本回退：自定义子类按引擎不相交定义（StarRail `[2019.4.100,2020.0)`、Endfield `[2021.3.527,2022.0)`），中间是真实 Unity 空隙；`Pass015`→`GenericTypeResolver.ResolveNode`→`GetTypeForVersion`（还有 `Pass100/101`、`UniqueNameFactory`）会撞空隙抛 `No instance found`。**没有任何真实奇点版本能填上——那是自定义数据本身的洞。**
- **保留** 数据适配器 `Pass506` no-op（StarRail 精简的 310 没有 `m_CrashReportingSettings`/`m_UnityPurchasingSettings` 地标）、`Pass039` prune（doc 注入引用了不完整 dump 里缺失的 enum 成员）。

**最小 Common 集（8 文件 ~75 MB，保持它小）**：因回退容忍空隙，必需的只有每个自定义引擎的**基础**（`2017.4.0f1`/`2019.4.0f1`/`2021.3.0f1`）、**113-string 上限**（`6000.5.0a8`）、**下限+早期锚点**（`3.5.7`/`4.1.0`/`5.0.0f4`/`5.6.0b5`）。**禁重新加入中间 minor**（2018.x/2020.x/2022.x/6000.1-4）—— 回退下冗余，只拖慢生成。只有当某个非自定义游戏需要精确建模那个确切类型树时才加真实版本。

---

## 12. IL2CPP 原生方法反汇编（`Il2CppMethodDump`）

AR 经 `AssetRipper.Cpp2IL.Core` 把 `GameAssembly.dll` 变成**哑 assembly**（桩方法体），ILSpy 再反编译成 `ExportedProject/Assets/Scripts/**.cs`。本 hook 搭同一趟分析的车，把每个方法的**原生 x86/ARM 方法体**作 `//` 注释注入到匹配的 C# 方法体里。源：`AssetRipperHook/Il2CppMethodDump/`。

**模型来源**：`IL2CppManager.Initialize`（冻结）在加载期跑 `Cpp2IlApi.InitializeLibCpp2Il`，之后静态 `Cpp2IlApi.CurrentAppContext` 持有完整模型并**贯穿 export 存活**。GUI 每次加载经 `ClearStaticState` + 新 `InitializeLibCpp2Il` 重置。**禁在 DllPostExporter/哑 DLL 保存阶段 dump**（那写的是 `AuxiliaryFiles/GameAssemblies/` 的原始 DLL，不是用户读的 C#）。

**Hook 点**：`WholeProjectDecompiler.CreateDecompiler(DecompilerTypeSystem)`（`AssetRipper.ICSharpCode.Decompiler`，AR 分叉，版本不在 nuget.org）。AR 的 `CustomWholeProjectDecompiler` **没有** override 它，所以基方法会跑。用 `[RetargetMethodFunc]`：`ret` 前 `dup` 返回的 `CSharpDecompiler` 并 `call AddTransform`，把 `IAstTransform` 追加进 `decompiler.AstTransforms`（幂等）。**`AddTransform` 必须 `public static`** —— 注入的调用住在 ILSpy assembly 里。

**AST 变换**（`Il2CppAsmCommentTransform : IAstTransform`）：每个带体的 `EntityDeclaration` → `decl.GetSymbol() as IMethod` → 查反汇编 → `body.InsertChildBefore(firstStatement, …, Roles.Comment)` 逐行插 `Comment(line, SingleLine)`。三个坑：
1. 改树前先 `DescendantsAndSelf.OfType<…>().ToList()` 物化。
2. `GetSymbol()` 在 `ICSharpCode.Decompiler.CSharp` —— 缺那个 `using` 会解析到 `TypeSystemExtensions.GetSymbol(ResolveResult)` 编译失败。
3. **空方法体** `{ }`：ILSpy 把注释 emit 在 `}` **之后**，锚 `Roles.RBrace`/`LBrace` 都修不了 → 加一个 `EmptyStatement`（渲染成孤零零 `;`）再 `InsertChildBefore` 它。

**关联 ILSpy `IMethod` ↔ Cpp2IL `MethodAnalysisContext`**（`Il2CppAsmLookup`）：key = `CleanAssemblyName | Normalize(Type.FullName) :: Name / paramCount`；`Normalize` 把 `+ / \` → `.` 并剥掉泛型 arity `` `\d+ ``（ILSpy 的 `FullName` 两者都不带，Cpp2IL 带）。含 assembly + arity 故精确：测试游戏 Assembly-CSharp **3832/3832，0 漏**。查找非消耗+幂等，`CurrentAppContext` 变则重建。

**反汇编两条路**：
- **x86** → `Il2CppX86Listing.Render` 用 **Iced** 自解码 `method.RawBytes`（有每条指令 `IP`），收集方法内近跳转目标并 emit `loc_<IP>:` 标签；每条指令用本地 `MasmFormatter` + 挂 `Il2CppSymbolResolver`，再叠 `Il2CppRegisterFlow` 的符号注释。
- **其它（ARM/Disarm、WASM）** → `appContext.InstructionSet.PrintAssembly(method)` + `Il2CppAsmAnnotator.Annotate`（纯文本正则回退，无标签、无字段恢复）。
- 经 `app.InstructionSet is X86InstructionSet` 分支；`UnderlyingPointer == 0`（抽象/extern）逐方法跳过。

> **双 Iced 坑**：本项目引用两个暴露 `Iced.Intel` 的 assembly（真 `Iced` 经 Cpp2IL 传递、`MonoMod.Iced` 经 RuntimeDetour 传递）→ `using Iced.Intel;` 报 `CS0433`。修：显式 `<PackageReference Include="Iced" Version="1.21.0" Aliases="icedreal" />` + `extern alias icedreal; using icedreal::Iced.Intel;`，且该文件别 `using System.Text`（`Decoder`/`StringBuilder` 再冲突）→ 完全限定 `System.Text.StringBuilder`。
> **并发坑**：`X86InstructionSet.PrintAssembly` 用 static `MasmFormatter`/`StringOutput`，非线程安全，而 `WholeProjectDecompiler` 并行反编译 → 每次 `PrintAssembly` 串行化在一把锁下（持于 `Il2CppAsmLookup.GetDisassembly`）。

**符号解析**（`Il2CppAsmAnnotator.ResolveAddress`，x86 指令感知与 ARM 文本回退共用）：裸地址无意义，每个**地址操作数**就地换符号、不保留裸地址。x86 由 Iced 告知操作数种类：只解**分支/调用目标**与**绝对数据全局**；**立即数一律不碰**（旧正则曾把 `add eax,5E593F7Ah` 误标 `sub_`）；**寄存器相对位移**留给数据流恢复成字段。按顺序：

1. `appContext.MethodsByAddress[addr]` → 托管方法。
2. **PE 导出表**（反射 `LoadPeExportTable`+`GetExportedFunctions`，测试游戏 242 条）。
3. **关键函数**（反射 `GetOrCreateKeyFunctionAddresses()` 的 `ulong` 成员）—— `il2cpp_codegen_*` wrapper **不在导出表里**，这是它们唯一来源。
4. `GetLiteralByAddress` → 字符串字面量；`GetAnyGlobalByAddress` → `MetadataUsage`（TypeInfo/method/field global）。
5. 未命中 metadata 的地址按 **PE 段表**（`ParsePeSections` 解析各段 VA 范围+可执行/可写/是否落盘）分类：
   - **常量池解引用**：仅放行落在**只读且已落盘段**（`.rdata`）的地址 → `TryMapVirtualAddressToRaw`+`GetByteAtRawAddress` 读出**实际值**（`movss xmm0,[360f]`、整数 `[5h]`、`andps [{7FFFFFFFh x4}]`），作**最低优先级**传入。
   - **可执行段的括号地址**（代码指针/跳转表项）→ `loc_`（体内）/`sub_`（体外）。
   - **只读落盘段的 C 字符串**（`TryReadCString`：NUL 结尾、全可打印 ASCII、长度≥2）→ 引号字符串，救回 icall 签名 `lea rcx,["UnityEngine.Time::get_time()"]`、版本串等（**非托管字面量，`GetLiteralByAddress` 命不中**）。
   - **落盘数据槽里存着指向可执行段的指针** → `->目标符号`（`call qword ptr [->sub_1802178D0]`）。
   - 其余（运行期才填、文件里无值的 `.data`/`.bss`：icall 缓存/once-flag/TypeInfo 缓存）→ `g_XXXX`。**已实测其值不在文件、`GetAnyGlobalByAddress` 也命不中，是可用元数据的真实边界，绝不臆造。**
6. 两个最常见的匿名 global 由 `DetectMetadataInitIdiom` 升级：`cmp byte ptr [X],0 … mov byte ptr [X],1` → `method_init_flag`；`call il2cpp_codegen_initialize_method` 前压入的 token → `method_init_token`。`IsDirectMemoryOperand` 同时接受 32 位绝对 `[disp]` 与 64 位 RIP 相对（都以 Iced `MemoryDisplacement64` 的绝对地址为键）。守卫 `addr < 0x10000` 跳过寄存器相对偏移与 8 位寄存器名（`ah`/`bh`）。

**符号恢复（`Il2CppRegisterFlow` + `Il2CppTypeModel`）** —— 把元数据里**本就已知**的（字段偏移/静态类/返回类型）从裸偏移还原，而不是留 `[rcx+18h]`。每个 x86 方法一趟前向抽象解释：
- **播种**参数寄存器（镜像 Cpp2IL `X64CallingConventionResolver`：实例方法 `rcx=this`、`rdx/r8/r9` 前三整型参、浮点走 xmm 占同 slot、**>8 字节值类型返回**走隐藏返回指针使 `rcx`=返回缓冲且 `this`→`rdx`、尾随 `MethodInfo*`）。
- **基本块 meet 传播** `TrackedValue`（`ManagedRef`/`TypeInfo`/`StaticBase`/`Klass`），前驱一致则保留否则 Unknown。
- 恢复项：实例字段 `[rcx+18h] → ; this.groundDetector`（`FieldAnalysisContext.Offset` 反查，走继承链、含 0x10 对象头）；链式 `; this.a.b`；静态字段（`offsetof(Il2CppClass,static_fields)` **自动发现**=`0xB8`）；数组 `.Length`(+0x18)/`[i]`(+0x20+i*8) 并传播元素类型；调用返回类型；**虚/接口调用** `call [klass+N] → ; -> Type::VirtualMethod`（用该类型自己的 `VTable`，`offsetof(Il2CppClass,vtable)` **自动发现**=`0x150`，已对 `Object.Equals`=slot0 核对）；对象分配 `call il2cpp_codegen_object_new → ; rax = new T()`；`Il2CppClass` 结构读 → `; T::class[0xNN]`；泛型实例字段（`Unwrap` 到 `.GenericType`）；icall 惰性缓存槽 `[icall<UnityEngine.Time::get_time()>]`（`DetectIcallCacheIdiom`：近旁签名 C 字符串 + 同槽读写 once-cache 不变量）；**PE 镜像基址** `lea reg,[image_base]` 单列（Assembly-CSharp 约 47 处，不再误标 `g_<base>`）；`inc`/`dec [field]` → `; field++/--`。
- **一致性回撤 `RetractInconsistentArrows`**：元数据 `VTable` 槽序与运行期内存 vtable 对某些偏移不符会串名，`method.slot==i` 过滤挡不住。后处理按命名槽的**返回种类**（`ClassifyReturn`：Void/ScalarInt/ScalarFloat/Bool/Ref/Struct/Pointer）挑**绝不会命中正确名**的矛盾信号即撤名降级 `T::class[0xNN]`：void|标量的 `rax` 被解引用/整宽存/全 64 位捕获/当 `this`；float 名却 `movsd xmm,[rax]`；**xmm0 被读**（非 float 名）；**`test al,al`**（非 bool 名，真 bool 豁免）；**MethodInfo 载 r8/r9**（0 形参名却有整型实参）；引用结果存进不相关具体引用字段（`AreUnrelatedRefClasses` 保守）。结构/IntPtr 返回 rax 本是合法指针 → 不测（真结构 getter `get_position→Vector3` 名保留）。两种分派形态都认（`mov reg,[klass+disp]; call reg` 与直接 `call [klass+disp]`），连带撤配对的 MethodInfo 载入（`+8` 同槽）。**全局根治**：`CondemnedVtableSlots` + `EnsureCondemnationScan` 一次性预扫全 Assembly-CSharp 再渲染 → 某 (类型,槽) 被任一站点证伪即全方法一致撤（确定性，与渲染序无关）。`Object.*` 基槽误标 553→~281，余下无可观测矛盾（结果丢弃/尾 jmp）= 诚实极限。
- **编译器异常抛出助手**（`Il2CppHelperNamer`，`CodeLabel` 在 `sub_` 兜底前调用、按地址缓存）：il2cpp 为 `throw new XException(...)` 生成的助手无 global-metadata 身份，本会永留 `sub_`；但它把异常类型名作 C 字符串内嵌（`lea r8,["IndexOutOfRangeException"]`）并经 `object_new`/raise（`IsAllocOrRaiseFunction` 按关键函数**语义名子串**判，版本稳健）或尾调用共享构造器处置。反汇编目标一趟、读 bare `*Exception`/`*Error` 标识符（含空格的消息串排除）、**仅当 body 也 alloc/raise/尾调用时**才命名 `il2cpp_throw_<Type>`。全库扫：4973 个 `sub_` 助手、恰 3 个内嵌类型名，**全部命中，0 误报 0 漏报**；泛型/共享助手（类型来自运行期数据、无内嵌串）正确留 `sub_`。
- **回写失效用 Iced `InstructionInfoFactory` 精确算写寄存器集** + 调用点叠 ABI volatile 集 → 残留旧类型绝不误标后续访问（**错标比漏标更糟，宁缺毋滥**）。值类型字段偏移 0-based（`Vector3` x@0/y@4/z@8），引用类型含 0x10 头。
- **恢复天花板**（`_dumpprobe -- audit <seed> <N> [cs]` 随机抽样）：global-metadata 的所有符号种类已全恢复；候选 managed-field 命中率 ~52%，残留是 (a) il2cpp 运行期 C 结构访问、(b) 值类型/SIMD 批量拷贝/native 跳转表（本就非托管符号）、(c) 寄存器对象身份经任意计算丢失 —— **(c) 是静态数据流分析的固有极限**（需 SSA/值编号/过程间分析），非符号缺失。**栈槽跟踪试过 0 收益已回滚**（MSVC 用 callee-saved 寄存器存 this/局部，不 spill 到栈）。
- **残余上游边界**（非本 hook）：折叠 RVA（两方法共享一 VA，ASM 忠实但 C# 声明配错 = Cpp2IL 方法→RVA 分配）、`[StructLayout((LayoutKind)N)]` 枚举渲染、特性构造参数（v24 是 generator 函数）。

**迭代探针**（不跑完整 AR）：`Il2CppX86Listing`/`Il2CppAsmAnnotator`/`Il2CppTypeModel`/`Il2CppRegisterFlow`/`Il2CppSymbolResolver` **只依赖 Cpp2IL 模型** → 一个 `net10.0` 控制台直接 `<Compile Include>` 这五个真源 + `PackageReference AssetRipper.Cpp2IL.Core`/`Iced`(`Aliases="icedreal"`)，`InitializeLibCpp2Il` 后对挑出的 `MethodAnalysisContext` 调 `Il2CppX86Listing.Render(app, method)`，秒级看结果（改源即重编真源、非拷贝）。这是打磨符号恢复的主循环；ILSpy 探针只在验证 AST 注入接缝时才需要（`net9.0` 控制台复刻 `IL2CppManager` 静态 ctor → `DetermineUnityVersion` → `InitializeLibCpp2Il`，再 `new WholeProjectDecompiler` 子类 override `CreateDecompiler`，用文件夹 `IAssemblyResolver` 反编译一个哑 `Assembly-CSharp.dll`；经 HintPath 引 `ICSharpCode.Decompiler` 构建输出 DLL）。**PowerShell 5.1 反射不了这些包**（.NET Framework vs net9）。

---

## 13. CAB 虚拟文件 —— 名字索引 + bundle-granular 加载

把整个游戏当**一张 CAB 依赖图**按需取用，而不是把 21 GB 全载进内存。核心 `Core/CabMapping/{CabMap,CabTable,CabSelection}.cs` 是唯一实现，CLI/GUI（`Services/ExportCabMap.cs` 是薄门面）/Blender pythonnet 桥全部消费它。

**`<game>.cabmap`，RCM6 `0x52434D36`（唯一格式，自包含列式）**：列式 blob + 偏移表 —— CAB 名（**OrdinalIgnoreCase 排序**，查名二分、无字典）、**distinct chunk 文件表 + 每 CAB 一个 int 索引**（237k CAB 实际只落 ~40 个 chunk，按行存路径是 231× 冗余）、chunk 条目文件名、AssetBundle Container 可读寻址路径、ClassID[]、int 依赖图。
- **base 存绝对游戏根 → map 文件位置无关**（曾存相对 base，map 一被复制就静默重锚、种子归零）；游戏搬家 = 缓存失效，加载时 40 个 chunk 一个都探不到就响亮报错要求重建，绝不静默空结果。
- 加载 = 单趟顺序流读直入最终数组（无整文件中间缓冲），**顺手转置出反向邻接**（~3ms，`Dependents`/`ReverseClosureIds` 零构建成本）。
- `--build-cab-map` 生成：外层多 lane 并行流水 `.chk` × 内层每 bundle 一 worker，合并按目录枚举序确定性落盘。
- **可重建缓存**：格式一变即 bump magic 整体重建，**绝不写多格式兼容 reader**（旧 RCM2/3/4 与 `.names`/`--build-name-index` sidecar 已全删）。

**为什么名字进 map**：CAB map 全按内容 hash 索引，可读名只活在每个 bundle 的 **AssetBundle(142) 对象的 Container** 里，必须实际加载解析才看得到。Endfield 每个 CAB 100% 含一个 142 对象。合并扫描单趟把名字并进 map 本体。

**合并扫描（有界内存）**：`GameBundleHook.AssetBundleOnlyFactory` 只物化 ClassID 142、其它返 `null`，于是 `SerializedAssetCollection.FromSerializedFile`（反射调）只读那一个小对象，跳过 Mesh/AnimationClip/Texture。`ReadFullMetadata(sf, fileName)` = `ReadSerializedMetadata`（deps+ClassID）+ `ReadContainerNames`（条目名+Container 路径）单趟双投影，需 `NameScanVersion` 解析 142 的 source-gen 布局。`VirtualFileSystem.ScanChunk<T>(chkPath, project)` 是**单一**有界并行流式扫描器（逐 bundle 解密+解析+投影+即弃），`ScanChunkMetadata/Names/Full` 都是它的薄包装。258k CAB 全扫峰值 ~3.5 GB。

**★最关键的坑：chunk 条目文件名 ≠ CAB 名，无法互转。** 条目名 `fileInfo.fileName` = `Data/Bundles/Windows/<initial|main>/<24位hex>.ab`；CAB 名 = `cab-<32位hex>`，来自 bundle **内部目录**里 SerializedFile 的 `NameFixed`（=`SpecialFileNames.FixFileIdentifier(内部名)`，小写）。两套独立标识。**所以名字索引必须给每个 CAB 记录它的 chunk 条目文件名**（连 Container 为空的也记，否则 load 过滤拿不到条目名）。

**bundle-granular 加载（Endfield 必须，否则 OOM）**：AR 载一个 `.chk` 会经 `VirtualFileSystem.TryLoadChunkFiles → ExtractChunkFiles` 解出**该 chunk 全部 bundle**，而 Endfield 把 161k bundle 塞进单个 `.chk`（1.8 GB）—— 一个角色闭包只要几千个，整块加载在 13 GB 空闲下要 24 GB+。解法 = `GameBundleHook.LoadIncludeFile`（`Func<string,bool>?`，`null`=全载）：`ExtractChunkFiles` 用它在**解密前**按 chunk 条目名过滤。调用方把闭包每个 CAB 经名字索引映射回条目名组成集合，load 前置、`finally` 清。实测 pelica 闭包 4240 CAB 跨 20 chunk，big chunk 只取 ~2297/161113。

**解析层（`CabSelection`，唯一入口）**：谓词（`NamePatterns`/`ClassIds`/`FileScopes`/`SeedCabNames`）**AND 组合选种子** → 一次 `ClosureIds` 走 int 图 → `CabClosure` 同时给出待载 chunk 与 bundle 粒度过滤器。**谓词只约束种子、永不约束闭包**（种子的依赖可以合法跨目录，砍掉=导出断引用）。谓词扫描全核并行零物化：容器路径按 UTF-8 直接解码进池化 buffer 上 span 正则（**每分区克隆一份解释型 Regex** —— .NET Regex 内部只缓存一个 matcher，共享实例并发 `IsMatch` 会退化成逐调用分配风暴），文件 scope 折叠成 per-distinct-file 的 `bool[]`，`SeedCabNames` 走排序列二分。实测 237k CAB：单正则 200ms/144MB → **14ms/0.4MB**；`ResolveCabsForPaths` 反转成「查询集 HashSet + span AlternateLookup + 全表并行探测」374ms/140MB → **~15ms/0MB**。

**CLI**：`--cab-map <map> --names <regex>`（叠 `--hook Endfield_1.4.4`）→ 载 map → `CabSelection.Resolve` → 设 `LoadIncludeFile` → `handler.Load` → 导出**整个闭包**。
- **`--names`/`--load-types` 驱动时 `--load` 只是范围**：约束谁能当种子，不约束种子需要什么。
- **名字驱动时导出侧不再叠逐资产名过滤**（否则丢掉没带该名的依赖贴图/材质/网格）。`--names` 不配 `--cab-map` 时保持老语义。
- **回归口径**：`--names chr_0004_pelica_postmodel` 必须仍是 `2 seed → 66 CAB / 66 bundle → 12 chunk`，`loaded 3635 / exported 183`。

**GUI 预览 = Asset List 的另一种行**，不开新窗口（旧 `CabFileBrowser`/`AssetBrowser` 已删）。`assetListView` 是**单个虚拟模式 ListView 两种 backing**，`_listMode` ∈ {Assets, CabMap}。列 = Name/Container/Type/PathID/Source/Deps（**Size 列已删** —— CAB 无 size，顺带省掉每资产 YAML 序列化估算）。同一套搜索/类型过滤/排序/多选（**Ctrl+A 走 native `LVM_SETITEMSTATE` iItem=-1**，虚拟模式无托管全选）/右键菜单服务两种模式。
- 「Load CABMap」→ 载 map → `_allCabRows` 缓存 + `EnterCabMapMode()`。
- CabMap 模式：单选在 Preview 显示 CAB 信息（hash/source/deps/容器路径，无 3D）；右键「Load selected」=`LoadCabsScopedAsync`（载闭包→切 Assets 模式，append 跨多次累积 `_scopedLoadFilter`）、「Export with dependencies」=`ExportCabsWithDepsAsync`（`ResolveScopedClosure`→设 `LoadIncludeFile`→复用 `RunFilteredExportAsync`→完事回 `EnterCabMapMode()`）。
- Assets 模式右键：「Export selected」(Converted/YAML)、「Export with dependencies」（选中资产的源 CAB→闭包）。
- Scene tree 经 `_assetIndexByObjectKey`/`_nodeByObjectKey` 双向联动（虚拟模式无持久 AssetItem）。
- **名字缺失则做不了 bundle-granular**（条目名为空→`LoadIncludeFile` 留 null→整块加载→大游戏 OOM）；map 天生带每 CAB 的条目名，没这问题。

**导出格式 = 可重新导入的 Unity 工程**（`ExportedProject/Assets/…` 按原始寻址路径布局），不是 glTF：网格→`.asset`（`S_`静态/`SK_`骨骼）、prefab→`.prefab`、贴图→`.png`、材质→`.mat`、动画→`.anim` + `.state/.transition/.statemachine/.blendtree`。**别拿 `*.glb/*.fbx/*.mesh` 去找网格。** `HeadlessRunner` 默认 `ShaderExportMode.Decompile` —— 大闭包里上百个着色器逐个反编译是耗时与内存的主要尖峰。

---

## 15. FModelHook —— UE 着色器反编译（无头优先）

`Source/Ruri.FModelHook` + `.CLI` + `.GUI`：把 UE `.ushaderbytecode` 归档反编译成带「用到它的材质球 + 材质符号」的 `.shader`。shader 内符号（UB 成员名/纹理名）真值矩阵在 [`Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md`](Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md)；本节只讲**材质链接**与入口。

- **唯一入口 = 无头 CLI**：`Ruri.FModelHook.CLI.exe --game-config <AppSettings.json> [--skip-global] [--list-archives] [--archive-filter <tok>] [--split-variants|--no-split-variants] [--export-only]`（`--headless` 已是默认可省）。直接从 AppSettings 解析（AES 动态 key + mappings + EGame）构造 CUE4Parse `DefaultFileProvider` 跑完整 export+decompile，**绝不 `new FModel.App()`**。流水线只依赖 `state.Provider`（`AbstractVfsFileProvider`），与 WPF view-model 解耦 —— 这是无头化的关键。旧 `AutoExport/` 已整删。代码 `Game/SBUE/Headless/`。`--list-archives` 挂载后只打印归档名+大小再退出（IoStore 归档是挂载后的虚拟条目，磁盘上没有 loose 文件）。
- **.usmap mappings 是材质符号的硬前置**：UE5 IoStore 材质包用 unversioned property 序列化，缺 mappings 时每个材质 `LoadPackage` 抛 `MappingException` → Pass030 提取 0 个 → 全退化 `UnknownMaterial`。扫描前必须 gate `Provider.MappingsContainer != null`（FModel 在 `MainWindow.OnLoaded` 的 `UpdateProvider` 之后才异步 `InitMappings`，「文件已挂载」先于 mappings 就绪 —— 这个竞态曾让全部材质提取失败）。
- **hash → 材质的桥**（都折进 `HashToMaterialsFromUnified`）：主桥 = 每材质内联 `FShaderMapBase.ResourceHash`（非 bShareCode 走 `Code.ResourceHash`），等于归档 `ShaderMapHashes`，对每个 cook 材质都在（headless 设 `ReadShaderMaps=true`）。**容器头 `FFilePackageStoreEntry.ShaderMapHashes`（Pass020）在 InfinityNikki/X6Game 上极稀疏**，单靠它 18–85% shader-map 退化 `UnknownMaterial`，**不能当主桥**。**`CookedShaderMapIdHash` 是另一个 ID 空间（`BaseMaterialId` 派生），IoStore 下绝不拿它匹配归档 hash**。Niagara 是第三条独立桥（Pass035 `FNiagaraShaderMap.ResourceHash`）。
- **Pass030 两层解析**：Tier1 = 完整 hash→材质桥，冷启动建一次并缓存；候选集 = 容器头 shader-map-owning 包（`PackageShaderMapHashes` keys）∪ `M_/MI_/MF_/MPC_/MAT_` 前缀材质（**禁**用旧的 `path.Contains("Material")` —— 会膨胀到 157k 贴图/曲线；前缀也有 11 万但只 ~715 个真有 inline map），逐包 `LoadPackage` 读 inline ResourceHash 后**立即丢弃**（`LoadPackage` 不缓存 → 内存有界 ~2.7GB），存顶层 `MaterialResourceHashes`（**每 hash 封顶 16**，一个 shader-map 常被几十个 MI 共享）。Tier2 = 归档级富提取，**每 hash 只加载 1 个代表**做 UES/RenderState（共享同一父级 UES）。冷扫 ~5–8min 一次性。
- **并行反序列化竞态（关键坑）**：CUE4Parse `UMaterial.Deserialize` 把 inline shader-map 反序列化包在 try/catch，**并行 `LoadPackage` 下偶发异常被吞 → `LoadedShaderMap` 静默置空 → 材质从桥漏掉，桥变非确定性**（bridge-hash 逐次漂移、整归档材质可能消失）。修：容器头子集的空包**单线程重试**；前缀空包不重试（多为继承父级的真空 MI，11 万重试太贵）。
- **黑洞缓存**：Pass005 会话开头从上次 `UnifiedShaderMetadata.json` **流式只读**需要的几段（`MaterialResourceHashes` 桥 + 已 enrich 材质 + Niagara 桥，跳过重型 `ShaderCodeArchives`），于是 Tier1 全桥扫描 + Pass035 只冷启动跑一次、暖启动 ~200ms。失效守卫：`CacheFormatVersion`（提取形状变了就 bump，当前 7）+ `GameVersionEnum` + `MaterialScanComplete`。**坑：暖路径信任缓存时必须同时设 `state.Root.MaterialScanComplete=true`（不只 `state.MaterialScanComplete`），否则 Pass080 写回 false → 下次又冷建整桥。** Tier1/Pass035 都是 8 路并行 `LoadPackage`。
- **native 依赖全来自 NuGet**，build 还原到 `<bin>/runtimes/<rid>/native/`（`dxil-spirv-c-shared.dll`←`AssetRipper.Bindings.DxilSpirV`，内建 dxbc-spirv 直译 SM5 DXBC；`spirv-cross.dll`←`Silk.NET.SPIRV.Cross.Native`）。`NativeToolsLoader` 优先探 `runtimes/<rid>/native` 再回退旧 `Tools/`。⚠ **绝不再往 `<bin>/Tools/` 拷旧 native** —— 残留的过期 dxil-only `dxil-spirv-c-shared.dll` 会遮蔽 NuGet 的 dxbc-spirv 版，把 DXBC 报成 `dxil_spv_parse_dxil_blob failed (-4)`（1958→129 退化的根因）。真出 `DllNotFoundException` 就删 obj+bin 重建。

## 16. Unreal lane —— UE 资产在内存里直接成为 Unity 资产

UE 是 `GameType.UnrealEngine` 这一个解码器(`UnrealEngine_4.0`,按**引擎家族**认领:没有自己解码器的 UE 标题都走它),和 Endfield 一样进 cabmap→浏览→内存导入,只是容器是 CUE4Parse 的提供器、对象在 `GameBundleHook.CustomFilePreInitialize` 里被换成 AR 资产,后面的流水线不知道数据不是 Unity 的。

- **依赖方向(钦定)**:内核 `Ruri.RipperHook` **不引用 CUE4Parse**;解码器整棵住 `Source/Ruri.FModelHook/FModelHook/Unreal/`(`Ruri.FModelHook.Unreal[.Converters|.TypeTree]`;同级平铺 `ShaderDecompiler/`、`Headless/`,不再有 Game/SBUE 分层;`FModelHook/` 只放 hook 代码,`Core/` 与 `EngineUbMetadata/` 留在项目根),FModelHook 引用 RipperHook。宿主不用被告诉它在哪:模块的构建在自己旁边写下 `Modules/<名>.module`(内核与模块同一个输出目录),`Bootstrap.LoadDeclaredModules` 开机即加载;要额外加一个仍可 CLI `--module <dll>`、GUI `RuriRipperHook.json` 的 `Modules`,都汇到 `Bootstrap.LoadModule`(模块目录探测托管与原生依赖,`HookCatalog` 见到新程序集即失效重扫)。`HookCatalog.DeclareHost(属性类型)` 让每个宿主只列自己家族的解码器——同一个 FModelHook.dll 里 FModel 的 `UE_ShaderDecompiler` 与 RipperHook 的 `UnrealEngine_4.0` 互不串台。模块在 FModel 的 bin(`FModel/FModel/bin/<配置>/net10.0-windows/win-x64/Ruri.FModelHook.dll`,`CUE4Parse-Natives.dll` 放旁边);CI 的 FModelHook job 因此也 init AssetRipper 并带 `-p:PureRelease=true`。
- **身份与选项**:`[RipperInstallProbe]` 静态探针读 exe 与 DefaultGame.ini,报 `PlayerIdentity.Engine="UnrealEngine"`;**引擎版本读 exe 里编译进去的 Build.version 字面量**(UTF-16 `++UE5+Release-5.1-CL-23901901` / `++UE5+Release-5.1`,每个 UE 二进制含启动器都有,`UnrealInstall.BuildVersionLiteral` 16MB 窗口+256B 重叠扫描),不读版本资源(OniValley 5.1 的 Shipping exe 根本没有,厂商也可改写);字面量缺失(改名分支的自研引擎)⇒ schema 把 `unreal.engine` 标 required、面板常驻警告、挂载拒绝并说明;解码器解析先按 product 再按引擎家族。读取参数(AES/EGame/贴图平台/usmap/版本覆盖/额外 pak 目录/`unreal.codecs`)= 源选项,`--source-option name=value` / Blender 表单;解码器发 `unreal.settings.schema` 数据集,面板按它画,**代码不认识任何选项名**。
- **数据流**:cabmap 一行=一个包(CAB 名=包路径,容器路径 `Assets/<包路径>`),依赖零解析取自 IoStore 容器头 `ImportedPackages`,ClassIds=导出类名经转换器表(`UnrealConverters.All`,按 usmap 父链匹配)映射。加载:`UnrealPackageLoader` 两道并行屏障——所有包先 Allocate(造空 AR 资产入 `UnrealAssetTable`,键=`GetPathName()`+槽位),再 Fill(引用一律可解),fileStack 留空。转换器:StaticMesh/SkeletalMesh/Skeleton/Texture/Material/AnimSequence/World,其余一切 → PropertyBag(MonoBehaviour+按类名的 MonoScript)。引擎中立建造器在 `Core/Conversion/`(Mesh 14 通道 Float32 单流,2019+ 蒙皮权重走通道 12/13;Clip 写编辑器曲线;Texture 行翻转;Material=Dummy Shader+SavedProperties,贴图属性声明 2D;PropertyBag 结构来自类型树)。坐标:Unity=(UE.y, UE.z, UE.x)/100,四元数同置换,UV v'=1-v,绕序按法线表决。
- **类型树**:usmap → `UsmapTypeTreeBuilder`(与 Ruri.Tpk 链接同一份源)→ 运行期注册 lineage `"6"`(`CustomEngineType.UnrealEngine`),类名索引。⚠ 运行期把 tpk 的 `string` 改名成 `Utf8String`/`PropertyName`,喂 AR 的 `TypeTreeNodeStruct` 必须用 `TypeTreeNode.UnityTypeName`,否则每个字符串字段都被 AR 当成结构。`UnrealValueWriter` 按**字段形状**写值(指针/结构/字符串/数值),形状不合 ReportOnce,不抛。
- **usmap 从哪来**:`Ruri.Tpk --unreal-reflection <game exe> [out.usmap]`——exe 旁有 pdb 就够:AsmResolver 读 PE+PDB,按 `Z_Construct_U*_Statics::{Class,Struct,Enum}Params` 公开符号定位静态反射数据,布局/位域/枚举值全部读自 PDB 类型记录,重放 `ConstructFProperty` 的反向走法;类名=IMPLEMENT_CLASS 去前缀,enum 照 `SetEnums` 补 `_MAX`,内建类父级读 PDB 里的 `Super` typedef,数据库没记录的 legacy `UProperty` 家族显式列出并省略。Titan:6.5s 出 10,524 structs / 2,166 enums。`Ruri.Tpk --unreal <usmap>` 再把它直出为自定义引擎 tpk。别再注入 UnrealMappingsDumper——DebugGame 会崩。
- **贴图**:Unity 认得的 cooked 布局(DXT/BC4-7/ASTC/ETC2 与各种 raw 格式)直接搬第一级 mip 的字节,不解码、内存缩到 1/4-1/8;只有 Unity 没名字的布局或主机平台的 swizzle 数据才经 CUE4Parse 解码。行序保持源的自上而下:`TexturePixels.TopDown` 把贴图登记进 `TextureOrientation`,内核的 `TextureOrientationHook`(由活动解码器注册一次——`RegisterModule` 统一挂模块的属性 hook,模块 `OnApply` 只设状态不再自挂,否则一次运行挂四遍;IL 改写 `TextureConverter.TryConvertToBitmap(ITexture2D, out)` 里那一次 `FlipY`,找不到即失败)按登记决定翻不翻。⚠ hook 引用带 `out` 的重载,属性里写元素类型即可(`HookRegistry.MatchByElementTypes`);baseline 记在 `HookBaselines.json`。
- **蓝图与自定义结构**:反射 schema 只有 `/Script/` 包里的类。类在内容包里(蓝图类、UserDefinedStruct)时,`TagSchema` 从对象自己的属性标签建形状(嵌套结构取其标签、容器取首元素、原生结构取 CUE4Parse 类型的公共成员),每个对象一棵不缓存的树(`PropertyBagBuilder.Structure(..., shared:false)`)。判据 `UnrealTypeTree.IsNative(export.Class)`。DataTable 的行是原生序列化不是属性:`DataTableConverter` 把每行做成行结构类的 MonoBehaviour 资产,归在表的文件夹下,表本身仍是自己属性的包。map 属性写成 `MapEntry{first,second}` 列表(AR 的 pair 两半不可写)。
- **动画**:采样率是序列自带的 `PlatformTargetFrameRate.Default`(引擎 `GetSamplingFrameRate` 的定义;cooked 数据没有编辑器侧的 NumberOfSampledKeys),帧数是编码数据的键数;Titan 全部 1920fps。`unreal.animation.samplerate` 可重采样(0=源率)。⚠ CUE4Parse-Natives 的 ACL 解码(`ACL.cpp` 逐样本 `nearest` seek)会吐出**长度 0.9983 的非单位四元数**:约 12% 的样本、各骨骼同一时刻同一比例缩放,位置/缩放轨不受影响;任何角度量都把它读成 6.67°/9.43° 的来回抽动,一条静止 77s 的 clip 因此被写成 150 万键 748MB。`ClipBuilder` 先把旋转样本归一化再对半球——旋转是方向,长度不承载信息。约减(`Core/Conversion/CurveReducer`)是**逐段最小二乘 Hermite 拟合**:每段两条切线自由(Unity 关键帧的 out/in 切线只属于那一段),在拟合误差最大的样本处分裂直到全部样本在容差内;连续相同样本是量化平台,容差在平台上放宽半个较小跳变;各曲线并行归约。容差=ACL 编码的 `ErrorThreshold`/`DefaultVirtualVertexDistance`(cooked 未写=类缺省 0.01cm/3cm)×`unreal.animation.tolerance`(默认 1;0=保留全部样本),非 ACL 编码保留全部样本;`--log-level Verbose` 打印每条序列的 codec 与容差。实测 Backflip(5.3s×75 曲线):稠密 762k 键→7.7k 键 3.7MB;AS_Iktu_Soldier_1(77s 静止姿势):748MB→369KB(每曲线 2 键);Fill 由 908s 降到 3.7s。
- **内存**:加载器汇总行带 `heapAtStart/heapAfterSchema/heapAfterAllocate/heapAfterFill/peakWorkingSet`(强制 GC 后的驻留量),`--log-level Verbose` 再打 `UnrealLoadProfile`(网格字节/内联贴图字节/属性包字段数/clip 键数)——先看这两行再动手。已做:`UnrealFileProvider` 让包实例按路径唯一(CUE4Parse 的 `LoadPackage` 每次新建,importer 的 `ImportedPackages` 各自私藏一份),Fill 完 `Forget`、Load 末 `Release`,cabmap 扫描走 `LoadUncached`;贴图字节不再复制进 AR 对象:`TextureBuilder.Defer` 把 `m_StreamData` 指向内核的 `DeferredResource`(挂在 ProcessedBundle 上、经自定义 `FileSystem` 打开的 .resS,一次只物化一段),导出时 `CookedBytes` 用不进缓存的包实例重读首级 mip,PNG 逐字节相同,Blender 桥按资源文件分组串行编码本就走 `GetImageData`。**虚拟贴图**(Titan 角色/环境贴图约半数是 VT 流式的 `Texture2D`:`PlatformData.Mips` 为空、`VTData` 有 tile)不再经 CUE4Parse 解码成 RGBA(每张 22MB 内联):`VirtualTiles.Lift` 镜像 `DecodeVT` 的寻址(Morton 地址、`GetTileData`、RawGPU/ZippedGPU 两种 chunk 编码)把每个 level-0 tile 去边框后的内层块拷进 `Width×Height` 的块网格,直接得到 cooked DXT/BC 首级 mip,同样延迟到导出再拉;多层/UDIM/crunch/边框不整块的留给解码器。验证:477 张 VT 与解码路径逐像素比对,DXT1/DXT5 全部一致或 ≤8/255(解码器舍入),BC5 法线只差蓝通道(CUE4Parse 合成 Z、AR 置 0,块数据相同)——⚠ 2026-09-06 修正:**这不是显示约定**,蓝通道就是法线的第三分量,BC5 不存它是因为「单位长度可解」,而**图片没有 shader 替它解**;Blender 的 Normal Map 节点直读蓝通道,实测该关卡每一张 `_N` 全图蓝=0.000,解出来 (x,y,-1) 全部朝里。已在 `TextureOrientationHook.RestoreNormalZ` 补回(算式抄上游 `TextureConverter.UnpackNormal`,那份只对 DXT5nm 生效且还要换 A/R),修完 A/B 两条路的蓝通道都在 0.50–1.00。实测 Characters 闭包 2654 包:峰值 25.3→12.3GB,Fill 后驻留 12.0→1.6GB,Fill 24→8s,零失败。**allocate 只读导出表头**:`IUnrealConverter.Allocate(conversion, ResolvedObject header)`——`ResolvePackageIndex(new FPackageIndex(package, i+1))` 给出名字/类/outer/路径而不反序列化任何导出,converter 只在 allocate 建**跨包会被引用**的资产(PrimarySlot、prefab、UMaterial 的 shader),LOD1+(`StaticMeshConverter.Lod`)与 DataTable 行改在 Fill 建(只有本包 prefab/表引用它们);两个阶段都用 `ForClassName(类名, usmap 超链)` 选 converter(`Handles(UObject)`/`For(UObject)` 已删,`UnrealConverters.IsA` 走同一条 `Ancestry`);加载器读完表头即 `Forget`,Fill 重新 `LoadPackage` 反序列化再 `Forget`。实测同一 Characters 闭包:`heapAfterAllocate` 4.4GB→74MB,allocate 3.9s→0.6s,峰值 12.3→**9.0GB**(当天起点 25.3GB),产物 22,619 文件除 GUID/时间戳外逐字节相同(唯一差异:一个从未被填充的空 LOD1 网格不再产生)。再往后是网格(`m_StreamData` 同机制,AR `GetVertexDataBytes` 已支持)、非原生属性包(每对象一棵 `TagSchema` 树)与 Fill 并行度下的瞬时峰值。Nanite:CUE4Parse `StaticMeshDto` 在没有普通 LOD 时自己解析 Nanite 簇,无需额外规则。
- **世界分区(World Partition)**:引擎把分区世界 cook 成「持久关卡包 + 每个流式 cell 一个 `<世界包目录>/<世界名>/_Generated_/<cell 名>.umap`」(WorldPartitionHelpers.cpp),cell 对象住在持久包里:`WorldPartition` 导出(ULevel 指向它的属性不是 CUE4Parse 读得到的 tagged 属性,按导出表类名找)→ `RuntimeHash` → SpatialHash 走 `StreamingGrids/GridLevels/LayerCells/GridCells`,5.4+ 的 HashSet 走 `RuntimeStreamingData/SpatiallyLoadedCells/NonSpatiallyLoadedCells`;每个 cell 的 `RuntimeCellData.CellBounds|ContentBounds/Priority/HierarchicalLevel` 与 `bIsAlwaysLoaded/bIsHLOD/bClientOnlyVisible/DataLayers.DataLayerNames`。常驻 cell 没有自己的包:cook 只对 `IsAlwaysLoaded()` 的 cell 跑 `OnPrepareGeneratorPackageForCook`,把 actor 折进持久包,所以它的关卡包=世界包。⚠ **cell 的关卡包不许由「世界目录 + cell 名 + `.umap`」拼**:cell 自己说得出来(`StreamingData.PackageName` / `StreamingData.WorldAsset` / 它持有的 `LevelStreaming.WorldAsset` / 几何容器 `FastGeoContainerPtr`,依次问、先答者算),拼出来的那份一遇到按内容包/数据层分桶的 cook(`_Generated_/<桶>/`)就全部指向不存在的包。`UnrealWorldPartition` 读成行,发布 `unreal.worlds`(SceneList:**按 cabmap 里那一行的类**——包里有没有 World,不看扩展名,`.uasset` cook 的世界与 `.umap` 的一样收;排掉 `_Generated_` 下的;`partitioned`/`cells#`)与 `unreal.world.cells`(PlaceList,参数 `world`,可选 `minX/minY/maxX/maxY`(UE cm)与 `level`:给了窗口只留包围盒相交的 cell,常驻 cell 属于任何窗口;**build 不发包围盒时全为 0,面板据此自己不做窗口**)。Titan:TitanMain 1,004 cell(HashSet,`RuntimePartitionLHGrid_0`,叶 688 个 256 m 见方、层级 1–4 逐级合并、2 个常驻),每个 cell 包本身就是一个 World,`WorldConverter` 直接出场景——单独导一个叶 cell:464 包闭包、160 个 actor,159 个世界坐标根节点全部落在 cell 包围盒内。Blender:Unreal 页签 World Partition 区块(`Game/UnrealEngine/unreal_panel.py`,UI-only)列世界→选世界→窗口/层级/是否含常驻→cell 列表→Select/Import(写 `SELECTED_CABS` 再调宿主 `import_selected`)——headless 验收:窗口 (−190000,−370000)…(−170000,−350000) 叶层切出 2 个 cell,Import cells 64.7s 放进 3,097 个对象/228 根节点/2,544 张图,根节点落在该 256 m cell 的位置(插件轴映射 Blender X=−Unity X、Y=−Unity Z);CLI:`--data unreal.world.cells --data-arg world=… --data-arg minX=… --data-arg level=0` 拿到包路径后 `--names` 导出。
- **cook 与引擎版本对不上时的读法**:每个版本门由包里声明的 custom version 决定,而一个**一条都不写**的 build 只剩引擎版本可问——那个答案对它从新引擎**回移**过来的那几处恰好是错的。哪几处是 build 的事实,所以**由标题以数据陈述**(`UnrealTitle.SkeletalMeshSectionVersion`),`UnrealSerializationDialect` 用一个 IL manipulator 只在那一个读处生效(把 `FSkelMeshSection.SerializeRenderItem` 里对 `FUE5MainStreamObjectVersion.Get` 的调用换成「标题说的,没说就照旧」)。🛑 **别整份声明 custom version**:同一个版本还决定材质/动画/贴图/世界分区/RigVM 怎么读,回移了一个字段不等于回移了那五十处。实例:某 4.26 分支的骨骼网格 section 带着 UE5 的 `bVisibleInRayTracing`(4 字节),不补 ⇒ 越界发生在 `bDisabled` 处、`AbstractUePackage.DeserializeObject` 把异常吞掉、`LODModels` 留下 null,最后在 `SkeletalMeshDto` 报一个与真因毫不相干的 NullReferenceException——**报错位置离真因两层,先看被吞掉的那条**。
- **标题自带的设计表就是清单的真源**:一个把玩法写成脚本的 build,自己带着①**表注册表**(`ConfigDB/aki_base.csv`:表名→数据库→语言库,1,705 行)、②每张表的 **FlatBuffers 访问器**(`Define/Config/<表名>.js`,字段名与 vtable 槽位,1,825/2,013 张表有)、③**实体注册表**(表名→逻辑/类型/模型 id)。于是解码器**按表名读任意一张表**、字段名是设计师起的名字,代码里不出现任何数据库文件名。判据链(实测):角色 `RoleInfo.meshid`/皮肤 `RoleSkin.meshid` → `ModelConfigPreload.Id` → `Meshes[]`;实体 `BlueprintConfig.blueprinttype` ↔ `TemplateConfig`(1:1,18,121),**种类就是 `entitylogic`**(Npc 13,887 / Item 2,702 / Monster 1,057 / Vision 259 / Animal 66 / Vehicle 17…),细类是 `entitytype`(260 种),名字是 `componentsdata.BaseInfoComponent.TidName` 经语言库、没有就用设计师起的 `name`;模型在 `ModelComponent.ModelType` 里按它自己的四种写法给(`ModelId` / `NpcModel.Mesh` / `NpcModel.Da` 一个装配用数据资产 / `PrefabPath`)。🛑 **别靠资产名前缀分类**:同一个前缀既有角色也有怪物,而 Blueprint 的父类链到 `TsBaseCharacter_C` 就并了(脚本语言写的玩法,类层次不带信息)。场景同理:`AkiMapSource.mappath`+`AkiMap.mapname` 给区域名、`InstanceDungeon.sublevels`+`mapname` 给关卡名(实测 4,991 个世界里 732 个被这两张表认领)。
- **装配式角色**:一个 build 可以不发整只模型,只发一个数据资产(属性指向 头发/脸/衣服/骨架 四个 SkeletalMesh)。`unreal.placements` 对**自己摆不出任何东西的包**才走这条:遍历导出的属性、凡是指向网格的就摆一行,**不认属性名**——认名字就等于把那一套命名抄成第二真源。
- **几何容器(FastGeo)**:cook 也可以把静态几何以 scene proxy 而不是 actor 的形式流式化——cell 包里是 `UFastGeoContainer`,没有 actor、没有组件树。`unreal.placements` 对这种导出直接发行:`ComponentClusters[].StaticMeshComponents[]` 每个一行,`WorldTransform` 已是世界空间(parent = -1),网格取 `SceneProxyDesc.StaticMeshSceneProxyDesc.StaticMesh`、材质取 `OverrideMaterials`。不认它 ⇒ 这些 cell 静默导成空(实测一张大图 50,082 cell 里 9,653 个是这种)。
- **类型树构建**:10,524 类原本 10s,现 1.3s——继承字段列表按父链共享、节点与字符串在 blob 自己的线性查重前面加字典备忘、blob 上限 8192 节点(运行期按多 blob lineage 注册,查类跨 blob)。
- **Blender**:模块无需任何设置——FModelHook 的构建把自己的绝对路径写进内核输出的 `Modules\Ruri.FModelHook.module`(csproj `DeclareModuleToKernel`),CLI/GUI/Blender 桥启动时 `Bootstrap.LoadDeclaredModules()` 一律先加载声明过的模块,UE 安装(`Engine\` + `<Project>\Content\Paks\*.utoc`)因而在任何宿主里都被探针直接认出;首选项 `Decoder Module DLL` 只作额外覆盖(之前不填就「Built 0 CABs … no decoder」);`Game/UnrealEngine` 模块按引擎家族认领,源选项表单从 `unreal.settings.schema` 画,`Unreal` 页签显示挂载会话。GUI 探针口径:面板要真 draw 得把 `bl_category` 临时改到侧栏当前页签(`Region.active_panel_category` 只读),draw 用包装计数。
- **直出 Blender(2026-09-06 建成,09-07 把旧路删光)**:UE 包**不再有**「转成 Unity 资产」这条路——转换器、加载器、Unity 类型树、只服务它的 Unity 建造器全部删除(**净删 4131 行**),数据集是唯一输入。六个数据集(`Ruri.FModelHook.Unreal.UnrealDatasets`,参数都可写引擎对象路径或挂载文件路径,`PackageKey` 一次归一;⚠ 光 `FixPath` 不够——它在剥对象名**之前**取叶子,于是永远不补回刚被剥掉的扩展名):
  - `unreal.placements`(参数 `package`):一个世界摆放的每个场景组件,**父在子前**,带父行号/可见性/自身变换(已过宿主基)/网格与每槽材质的对象路径/灯光种类与形状;ISM/HISM 组件自身不摆,每实例一条子行。
  - `unreal.mesh.geometry`:每级 LOD 的 `positions@ normals@ tangents@ colors@ uv@ indices@ sections@ skin@` 全部 blob 列 + `uvSets`(稀疏坐标集的集号)+ `bones`(蒙皮权重索引到的骨名)+ `materials`(网格自带槽)。
  - `unreal.mesh.skeleton`:骨骼网格的参考骨架,逐骨名字/父/静止变换/clip 寻址路径。
  - `unreal.materials`:每个材质接口解析后的参数集**摊平成行**(`kind` = m 材质本身/k 图连接的输入/t 贴图参数/f 标量/c 向量),解析走 `MaterialConverter.Resolve` —— 与 Unity 转换**同一份读法**,两条路不可能对同一材质有分歧。
  - `unreal.textures`:贴图解码后的像素,PNG 容器,附资产自己声明的 sRGB/法线两个事实。读包在本线程(一个档一条流),解码+编码 `Parallel`;⚠ fpng 的静态构造**不是线程安全**的(指针注册表是裸 Dictionary + 裸计数器),两个线程同时首用会从类型构造器里抛 "same key 1" 并被运行时永久缓存 ⇒ 并行前先在本线程 `WarmEncoder()` 编一张 1×1。
  🔴 **不写第二套 builder**:接缝是三处**既有的中立形态**——`MaterialProperties`(`MaterialBuilder.build_from_props(props, key)` 成为入口,`build_from_ref` 变成 Unity 那道门;内容摘要改摘属性集本身)、`DecodedMesh`(`unreal/direct.decoded_mesh` 用 `np.frombuffer` 零解析填出来)、参考骨架节点(`armature_builder.build_armature_from_nodes` 从 `build_armature_from_avatar` 拆出)。贴图源 `UnrealTextureSource` 是**第三个输入源**(与 `AssetDatabase`/`BridgeAssetDatabase` 同一鸭子表面,键换成对象路径),不是第二套系统。C# 侧同理:`UnrealMaterialParameters` 改持贴图**对象路径**而不是 `ITexture2D`,`UnrealSceneGraph` 把「哪些组件算数/叫什么/可见否/父是谁」从 `WorldConverter` 里搬出来给两条路读,`UnrealComponents.MaterialPaths` 是槽材质那条规则的唯一出处。装配器 `unreal_importer.py`(插件根,与 `prefab_importer` 平级——UE 关卡不是 Unity prefab,不是同一件事的第二份实现)。
  🔴 **实测(SL_CritterVillage,ProjectTitan)**:现行路 **23.1s** / 1533 对象 / 1511 网格 / 459 万顶点 / 58 材质 / 165 图;直出 **7.0s** / 1574 对象 / 1533 网格 / 547 万顶点 / 63 材质 / 173 图 —— **3.3x,且建得更多**(现行路受闭包限制,直出按对象路径直接要包)。分段:摆放 0.51s、几何 0.42s(**56 个不同网格、21.5 万顶点**)、材质+贴图 7.98s、装配 0.73s。
  🔴 **一关 1533 个摆放只有 56 个网格**:每个摆放各建一份网格数据块 = 把同样的 21.5 万顶点写 547 万次,装配 20.31s;按 (网格路径, 每槽材质) 共享数据块后 **0.73s(27.8x)**——引擎本来就是这么摆的。蒙皮的不共享(权重在物体的顶点组上,骨架也是各自的)。剩下的大头是 C# 侧的材质语义 + 贴图解码编码,两条路都要付。
  🔴 **单资产**:同一个 `SM_IktuSpear_001`(两条路都是 3901 顶点、UVMap+UV1、Color、同一张材质 40 节点 34 图 3 接线,几何逐位相同)——现行 2.8s,直出 0.95s。
  🔴 **蓝图也是同一份摆放**:`UnrealComponentTree` 拆成「收集(`UnrealSceneGraph.Collector`,含唯一那份父在子前的定序)+ 造 Unity 节点」,`BlueprintConverter.Collect` 把构造脚本(CDO 原生组件 + 类链 SCS + `InheritableComponentHandler` 覆盖)状进同一个 Collector,于是 `unreal.placements` 同时服务世界与蓝图 actor。Unity 那条路照旧:同一关卡重跑 MESHHASH/PLACEHASH 逐位不变。
  🔴 **面板已切**:`Game/UnrealEngine` 的 Import Level/Window/World/Actor 全部走 `unreal_importer.import_package`,不再经 `import_selected` 的 cabmap 闭包(闭包只是"这张 cabmap 收了什么",直出按对象路径直接向挂载要)。Unity 转换保留给它真正的用途——导出 Unity 工程。
  - `unreal.animations`:每条序列的曲线,与桥给 Unity 剪辑写的**同一种 clip blob**(一份 JSON 索引 + 一段 float32),所以宿主用的是已有那个读取器。解码经 `UnrealClip`,约减经 `ClipBuilder.Reduce`(约减本身独立成中立形态 `ReducedChannel`,不再和写 Unity 剪辑绑在一起)。实测 `AS_Penguin_Flying_01`:两条路曲线 250 条、关键帧 23250、时长 0..92 全同,**25 根骨的 `matrix_basis` 逐帧最大差 0.000000**;直出 0.1s,旧路 5.2s。
  🔴 **删了什么**(2026-09-07):`Converters/` 十个 `IUnrealConverter` 全删(目录改名 `Readers/`,里面只剩读取:`UnrealSceneGraph`/`UnrealBlueprint`/`UnrealClip`/`UnrealMaterial`/`UnrealMaterialParameters`/`UnrealMeshGeometry`/`UnrealRig`/`UnrealComponents`);`UnrealPackageLoader`(两段并行 Allocate/Fill)、`UnrealConverters`(转换器表 → 数据表 `UnrealClasses`,cabmap 的类列表照旧)、`UnrealTypeTree` + `TypeTree/TagSchema`(usmap→Unity 类型树,**`UsmapTypeTreeBuilder` 留着,`Ruri.Tpk --unreal-reflection` 还要用**)、`UnrealLoadProfile`、`UnrealValueWriter`、`VirtualTiles`;内核 `Core/Conversion/` 的 `MeshBuilder`/`TextureBuilder`/`MaterialBuilder`/`HierarchyBuilder`/`PropertyBagBuilder`/`ConvertedSpace`/`DeferredResource`/`TextureOrientation` 全删(它们的消费者**只有** UE 转换器),`ClipBuilder` 只剩 `Reduce`。
  ⚠ 一并没了:UE 的 DataTable/PropertyBag 变 MonoBehaviour(**Blender 侧零消费者**,只为填导出的 Unity 工程)、贴图朝向登记表(只有 `TextureBuilder` 写它)。`--export-scene` **不受影响**——那是 VFS 游戏(终末地)的功能,不是 UE 的。UE 装载现在会明说「本 build 按数据读,导出工程会是空的」,而不是默默导出 0 资产还报成功。
  🔴 **贴图 hook 改名 `TextureConversionHook`**(原 `TextureOrientationHook`):朝向那半随登记表一起没了(没人再标 top-down,一律翻转=上游原行为),只剩两件**修正**——BC5 补蓝通道、读取加锁。`FlipY` 现在只当**锚点**(那是位图在栈上而纹理还在 arg0 的唯一一点),不再被替换;匹配不到仍然 `rewritten=false` 触发 Hook Fail,漂移闸门没丢。
  🔴 **浏览器按引擎分派**:`Game.GameModule` 多一个 `importer` 声明(与 `face_retarget`/`secondary_motion` 同一形状:没有就不动那条路),`Import Selected` 先问模块。UE 模块声明 `unreal_importer.import_package`,于是通用浏览器、Scene 页签、Actors 页签四个入口全走直出,cabmap 闭包只剩 Unity 游戏在用。
  🔴 **验收看图,不只看计数**:`BP_Arctic_PenguinL` 两条路计数全同(50005 顶点/265686 corner/22 顶点组/22 有权重),**但世界位置差一个 90° 绕 Z + Z −0.9**——现行路把组件变换 `bake_bind_pose` 进顶点(它的骨世界含组件节点),直出把它留在物体上。渲染出来逐像素同一只企鹅,`stored == evaluated` 证明静止姿势零形变。⚠ 探针教训:`import_package` 刚返回时子物体的 `matrix_world` 还是**旧的**(依赖图未更新),照它取景会把相机架错地方——先渲染一次或强制更新再读。

- **Unity 那条路还剩什么可绕(2026-09-07 实测,终末地 Pelica 一个角色)**:先量后改,读数来自桥自己打的三行——`[ImportCabs]` 各阶段、`[ExportCost]` 按扩展名、`[ProcessCost]` 逐处理器(后者本次新增,处理阶段此前只打步骤名,贵在哪只能猜)。
  🔴 **唯一真废动作:剪辑 YAML**。曲线早就走 `ClipCurveBlob` 零解析过桥,`ClipCaptureExporter` 却在建完 blob 后照旧调 `_inner.Export`——`.anim` **24 个文件 793 MB / 38.8s**,占整次导入 55.6s 的 **70%**,而它唯一的消费者是 `discover_clip_refs` 里一次「读全文嗅 class 名」。改法:blob 建成功就不写文本(失败才写,那时文本是唯一还能指认它的东西);`Partition` 的索引从 **`.meta`** 起而不是从资产体起(guid 住在 meta 里、meta 一定会写,于是「有身份有路径但没有文本」成了可表达的状态);发现改问 `db.clips()`(桥由 blob 表回答,磁盘由 `.anim` 扩展名回答)。**export 39,975ms→1,046ms(38x),整次角色导入 55.6s→16.9s(3.3x)**,内容零变化,剪辑照样发现 24 条、照样导入(2960/3500 条曲线、52 万关键帧)。网格早就做对了一半(`ExportWithoutGeometry` 只清顶点/索引缓冲还留文档,因为 Python 侧还要读文档里的非几何字段);剪辑没有那种字段,所以整份都不必写。**开 blob 通道时顺手问:它替代的文本还有谁读。**
  🔴 **Unity YAML 本身不是瓶颈**:同一次导入 `unity_yaml.parse_text` 只被调 15 次、1 MB 文本、**0.196s(1.5%)**——网格/曲线/闭包图早就走 blob 了,剩下的只有材质、prefab、avatar、controller。「把 Unity 游戏也改成数据集直出」**不值得做**,那条路的收益上限就是这 0.2s。
  ⚠ **`EditorFormatProcessor` 的 2.16s 不是废动作**(占处理阶段 96%,其余处理器全在 32ms 以下):它里面 `AnimationClipConverter.Process` 重建的正是 blob 要读的编辑器曲线。改成按需只算用户勾选的那几条会把闭包解析付两遍,常见用法反而更慢——**`SkipProcessingAnimation` 特性对 Blender 桥必须保持关闭,开了剪辑就空**。
  余下的钱:`load` 2.6s(读闭包的 74 个 CAB,不可约)、`prewarm` 0.6s(全核解码编码)、宿主侧约 6s 中 **5.6s 是终末地 NPR 着色栈建 13 张材质**(`ruri_endfield.py` 是**生成物**,真源在 `Ruri.RenderPipelines.Generator`,且已有过节点数战役;`character_shaders` 是用户开关不是浪费)。

- **验收命令**:`Ruri.RipperHook.CLI.exe --module <FModelHook.dll> --hook UnrealEngine_4.0 --load <游戏根> --build-cab-map <x.cabmap>`;导出:同前缀 + `--cab-map <x.cabmap> --names <子串> --export <目录> --source-option unreal.mappings=<usmap>`。Titan 实测:SkySphere(网格/prefab/材质含 3 张贴图)、SKM_Glider 闭包 87 包 0 失败、LI_SmallBuilding 关卡 244 包 103 个 actor 落进 `.unity` 场景、Backflip 动画经 ACL 解码约减;Blender 面板全链路(识别→选项→建图→导入)通过。全量导出按文件夹分批(32GB 机器上每 ~1000 包闭包峰值 ~10GB),Release 构建约 1000 包/45s。
- **蓝图=prefab(2026-09-05 晚)**:`BlueprintGeneratedClass` 走 `BlueprintConverter`——类导出头的 `Super` 链跨包走到第一个原生类,usmap 判 `Actor` 后代才造 prefab(AnimBP/Widget 等仍是数据);Fill 按引擎构造语义拼组件树:类链**从根到叶**各自的 `SimpleConstructionScript.RootNodes`→`ChildNodes`,叶 CDO 的原生场景组件按**属性名**登记(`RootComponent` 为根,`AttachParent` 为父),叶/中间类的 `InheritableComponentHandler.Records` 按 `(OwnerClass, SCSVariableName)` 覆盖祖先模板,SCS 节点 `ParentComponentOrVariableName` 同名查原生属性或脚本变量,CDO 无根时第一个场景组件成根。prefab 路径=包 stem(`Assets/<pkg>.prefab`),名字=包名(不带 `_C`)。未做:`ChildActorComponent` 展开子蓝图、socket 偏移(`AttachToName`)。
- **组件树唯一实现**:`UnrealComponentTree`/`UnrealComponents`(UObject 直读,不再经 CUE4Parse-Conversion 的 DTO)同时服务 World 与 Blueprint:节点=组件相对变换挂在 `AttachParent` 节点下(跨 actor 也成立),`bHidden`/`bHiddenInGame`/`bVisible=false` 置 inactive;静态网格→MeshRenderer,**骨骼网格→在节点下重建 rig(`UnrealLoadShared.Rig`,按网格路径缓存,来源 `ReferenceSkeleton`,与网格自身 prefab 逐位同源)+SkinnedMeshRenderer**,ISM/HISM 每实例一节点,灯光→Light;材质槽=组件 `OverrideMaterials` 覆盖网格自带槽。
- **prefab 根只属于种子(钦定)**:闭包里被依赖到的网格/骨架/蓝图包**不再造顶层 prefab**——`CabClosure.SeedFileNames` 经 `GameBundleHook.LoadSeedFile` 到 `UnrealConversion.IsSeed`,四个造 prefab 的转换器只在种子包上造;UE 每个网格都有 model prefab,不这样做时导蓝图会把闭包 82 个网格各导一份顶层副本、导场景 cell 会在原点堆出全部网格 prefab(probe6 空渲染的根因)。扫描给会产 GameObject 的行多列一条容器路径 `Assets/<pkg>.prefab`,桥的 `BuildRootCabs` 才能把根归到行(`seed_roots`)。
- **数据行闭包外扩(内核 `CabSelection.ReachThroughDependents`)**:种子的正向闭包若只含 `MonoBehaviour/MonoScript` 行(物理资产、骨架资产、配置),按 `Dependents` 逐跳外扩到出现别的类为止;桥的 `ImportCabsCore` 常开。Titan 的 `PA_Adventurer` 是 **PhysicsAsset**(deps=0,被 `SKM_Adventurer` 依赖),此前导入 CANCELLED 什么都不建,现在一跳到网格 prefab 建出角色。
- **材质参数模型(无猜测)**:`UnrealMaterialParameters` 按引擎解析顺序读——基材质 `CachedExpressionData` 的 `RuntimeEntries[kind]`(**5.5 的表直接铺在 cached 数据上**,嵌套 `Parameters` 成员的版本从其下取;kind 序=引擎按版本声明的布局 `KindLayouts`——`EMaterialParameterType` 是普通 C++ enum class **不是 UENUM**,任何反射源里都没有,只能逐版本声明,并按每个材质自己的条目数与各 `<Kind>Values` 表长校验,不符则告警不读;已声明 5.5/5.4(Epic API reference 该版本页逐字)与 5.1(usmap 的 `MaterialCachedExpressionData` 只声明六张运行时表且 `RuntimeEntries` x6,顺序取这六项在 5.4 页里的相对序,OniValley 全部基材质按表长逐一校验零不符);🔴 **不能从 schema 推序**:5.5 的 usmap 里 `StaticSwitchValues` 排第二、`DynamicSwitchValues`/`*PrimitiveDataIndexValues` 混在其中,值表声明序≠枚举序,按 schema 推的那版两款游戏全读不到——已回退;5.0/5.2/5.3 页 dev.epicgames.com 只渲染目录、Wayback 无存档,未声明即告警不读;静态数组按 `ArrayIndex`)的 `ParameterInfoSet`×`<Kind>Values` 给出每个参数与默认值,实例 `Texture/Scalar/Vector/DoubleVectorParameterValues` 与 `StaticSwitchParameters` 按名覆盖,`BasePropertyOverrides` 只取 `bOverride_X=true` 的 `X`(枚举经 usmap 序数);`PropertyConnectedMask` 按 usmap `EMaterialProperty` 位序写成 keywords。**不再**用 CUE4Parse `GetParams` 的正则角色(`PM_*`)与 `ReferencedTextures` 名键(它把参数默认贴图 `DefaultDiffuse` 当成独立槽,Blender 按名提示误接)。图里无参数的常量贴图:材质**没有任何贴图参数**时,唯一 sRGB 常量→`_MainTex`、唯一 `TC_Normalmap` 常量→`_BumpMap`(贴图自己声明的种类),其余按贴图名。⚠ cooked 数据里**语义角色只在编译后的 shader 里**(shader map 只给 uniform 槽名/索引,字节码在 `ShaderArchive-*.ushaderbytecode`,CUE4Parse 没读它);ORM/RMOH 通道布局同理不可知——真解是 DXIL/DXBC 数据流分析(采样→GBuffer 通道),单列战役。
- **材质语义取自编译后的 base pass(2026-09-05 晚,零猜测)**:`MaterialSemanticsResolver`(`ShaderDecompiler/Semantics/`,挂在 `UnrealFileProvider.Semantics` 随挂载存活,每个 shader map 只解析一次)。链路:材质或父链第一层带内联 map 的那层,取**最高 feature level、质量无关(`Num`)优先否则最富质量**的 shader map(带质量开关的材质每个质量一份 map,Low/Medium 把贴图换成常量——MI_Global_Rope_01 的 SM5/Low 只采引擎 SRV;SM6/DXIL 与 SM5/DXBC 结论逐槽一致)的 `ResourceHash` → `MaterialShaderLibraryIndex`(全部 `*.ushaderbytecode`;IoStore 档只是头,代码按 shader group 存在容器 chunk 里,`IoStoreShaderCodeSource` 懒取并解压,切片长度=组内下一个起点,与 Pass010 写 `.ushaderlib` 共用同一份布局与解压;pak 式档经 CUE4Parse 整体解析成 `ArrayShaderCodeSource`;`ShaderLibrary` 现在自带 Read/FromIoStore/FromSerialized,代码源抽象 `IShaderCodeSource`)→ 第一个 `TBasePassPS*` 像素着色器(`HashedNamesResolver`)→ `UnrealShaderParser` 剥头 → `SpirvFrontend`(DXBC/DXIL→SPIR-V)→ `SpirvTaint` 从各 Location 输出逐分量反向污点到采样的绑定 → 绑定名经 `MaterialUniformBufferLayout.TryResolveAuthorName`(`Material_<参数名>` 回读成 `Texture2D_n`/`VirtualTexturePhysical_n`)对回 `UniformTextureParameters[组][序]`,参数名 `None` 的常量贴图按 `TextureIndex` 找 `ReferencedTextures`。判据(5.5 非 Substrate GBuffer):基色=到 MRT3.rgb;法线=到 MRT1.rg;金属/高光/粗糙=到 MRT2.r/g/b;遮蔽=到 MRT3.a 且不到其他任何 GBuffer 分量(MRT3.a 还混入 SpecularColor 的 AOMultiBounce,基色/金属/粗糙也到达它);自发光=只到 MRT0 不到任何 GBuffer 目标(GBuffer 内容都会点亮场景色,次表面走 MRT4);遮罩=决定 discard 的通道。每部位记通道**集合**,`UnrealMaterialParameters.Apply` 只在恰一通道时写 `_PackedMap<部位>` 通道号,别名 `_MainTex/_BumpMap/_EmissionMap/_PackedMap`。Titan 实测:M_Adventurer_Head 三张常量贴图 → D=基色、N=法线、ORM=遮蔽0/金属1/粗糙2;M_Body 只采 Albedo/Normal(`Surface` 在该静态排列里根本没被采样,`Sub Surface` 只到 MRT4);MI_Arctic_Fencewood `Surface VT` 金属1/遮蔽2、粗糙来自标量;MI_Global_Rope_01 与 Marshland 的 MI_Marshlands_Nightmarket_Bar_Trimsheet 最高质量 map 也**不采任何材质贴图**(平色模式,颜色来自向量参数——下一步:同一污点扩到 Material cbuffer 成员 → `_Color` 等向量参数角色;Blender 侧在语义已解析时不得再按名猜图)。Verbose 日志逐绑定打印 reach 表。原生库:`NativeLibraryResolver` 先探 `Ruri.ShaderDecompiler.dll` 自身目录的 `runtimes/<rid>/native` 再探 app base,`DxilSpirvLibrary` 静态构造自注册,宿主不必记得初始化。快速回路:`Ruri.RipperHook.CLI.exe --module <FModelHook.dll> … --names <正则> --log-level Verbose`(CLI 不自动发现模块,必须 `--module`)。
- **材质常量语义 + 贴图角色配置(2026-09-05 深夜,用户"全部做完 禁止猜测")**:同一污点扩到 uniform 块——`SpirvTaint.Source` 有 Texture/Uniform 两种,uniform 源按访问链走 `OpMemberDecorate Offset`/`ArrayStride`/向量分量算出字节偏移(16 字节寄存器 + lane),动态下标不记。`MaterialConstantBufferReader` 的 preshader 读取器现在按材质记 `PreshaderField`(成员/绝对偏移/lane 数/程序区间/引用的参数名),`Evaluate` 可用**任意参数值**重算一个字段——`MaterialSemantics.Evaluate` 把实例的参数值替换进 `UniformNumericParameters` 的 `Value` 再跑同一个求值器,所以实例常量精确。判据比贴图更严:颜色=≥3 lane 且每分量恰一 lane 一一对应(单 lane 到三分量是系数不是颜色);标量=只到该 GBuffer 分量与 MRT3.a、不到其他分量(`Blend_Offset` 之类到处都到的是权重);引用了材质未声明参数的字段(引擎注入的 `SelectionColor`)不算;多个字段喂同一部位只在**求值全等**时写(1−Tint 与 Tint 在 Tint=0.5 时相等⇒ MI_Global_Rope_01 `_Color`=0.5,Marshland Bar_Trimsheet 不等⇒不写);还要 `PropertyConnectedMask` 说该输入已连(MP_BaseColor/MP_EmissiveColor/MP_Metallic/MP_Roughness)。产物 `_Color/_EmissionColor/_Metallic/_Glossiness(=1−粗糙)` + 关键字 `RURI_TEXTURE_ROLES_FROM_SHADER`。🔴 **UE 5.5 的 preshader 操作码布局 = 原来标成 Ue57 的那张表**(Titan 5.5.1 实测:原始 37 带 5 字节 swizzle 操作数,即 9 处已插入),枚举改名 `Ue55`,阈值 minor≥5;内存路径必须自己设 `MaterialConstantBufferReader.PreshaderVersion`(`DecompilePipeline.DetectPreshaderVersion`),否则 5.5 的 swizzle 被当 append 全部求值失败。Blender 侧:`RuriRipperPyBridge/unity/texture_roles.py` 是分层角色表(默认层 `texture_roles.json`=Unity Standard + Ruri 转换词汇;标题模块层 `Game/<模块>/texture_roles.json`;用户层 `%LOCALAPPDATA%\RuriRipper\texture_roles\<游戏>.json`),`material_builder` 只按解析到的角色接线,**不再按名字猜**;没有任何层命名的贴图记进未映射表,面板在导入选项下列出(名字/次数/例子)让用户选角色+通道,保存到标题模块目录或工作区;带 `RURI_TEXTURE_ROLES_FROM_SHADER` 的材质不列(剩下的就是 shader 没采的)。Endfield 的 HGRP 名表搬进 `Game/EndField/texture_roles.json`。
- **角色/场景查找(2026-09-05 深夜,用户"就和 Endfield 一样,别让我加载单个骨骼或 mesh")**:数据集 `unreal.actors`(`UnrealActorScan`)只读包头:每个含 `BlueprintGeneratedClass` 导出的包,沿 Super 跨包走到 usmap 命名的第一个引擎类,再按 usmap 父链判 Actor 后代;列 package/name/kind(Character|Pawn|Actor,按引擎祖先)/parent/native/skeletal#/static#(cabmap 直接依赖里带 Mesh+SkinnedMeshRenderer / Mesh+MeshRenderer 的包数)。Titan 21k 包 6 s,1225 个 actor(Character 5、Pawn 8);`BlueprintConverter.IsActorClass` 改走同一份 `NativeAncestor`。Blender `Game/UnrealEngine/actors_panel.py` 新 Actors 页:搜索/按 kind/只看带网格,Import=把该包放进浏览器选择再走 `ruri.import_selected`,Reveal=`ruri.cabmap_reveal`;Unreal 页的世界列表加过滤与每个世界的 Import(非分区关卡整包一键导,分区世界仍走 cells)。Post 页改成 Endfield 模块自己声明的 GameTab(`Game/Endfield/__init__.py`),宿主不再全局挂 post 格。🔴 **无 .usmap 即拒载**:用户 GUI 只得到空骨架的真因是 Load Options Form 里没设 `unreal.mappings`——无 schema 时 `SchemalessMappingsProvider` 顶上,unversioned 属性全部读不出(网格 0 顶点、材质 KeyNotFound)却静默成功;现在 `UnrealPackageLoader.Load` 遇到带 `PKG_UnversionedProperties` 的包且容器是 Schemaless 就抛 InvalidOperationException 说明怎么设,`unreal.actors` 同样拒答;CLI 返回 status=error。**必填选项常驻警告(用户"usmap 是必须加载的,没加载直接常驻警告")**:`unreal.settings.schema` 多一列 `required`(挂载的构建首个包带 `PKG_UnversionedProperties` ⇒ `unreal.mappings`=1,同一 cook 全包同旗;挂不上时 0 并告警);`GameModule.settings_schema` 只报数据集 id,页签解出解码器的那一刻(`_adopt_identity`→`_load_source_option_rows`)表单自动加载,必填未设的行红框 + 顶部 alert 框写明该选项与说明,表单未加载也有 alert;宿主不认识任何选项名。🔴 顺手挖出两个连带根因:①桥的每安装槽位(`_options_by_key`)在 `load_cab_map` 时快照选项,先建图后设 usmap 再 Apply 时 `_set_current_tab`→`use_session` 用旧快照 `reinitialize`,内核静默丢掉 usmap、导入再次被拒——`RipperBridge.reinitialize` 现在把同根目录的槽位一并改成刚应用的值;②`UnrealProviderSession` 指纹拿原样字符串,安装探针传带尾反斜杠的根目录、数据集传 `Session.GameRoot`(已去尾)⇒ 每次 Apply 挂载两遍——现在按 `GetFullPath`+去尾分隔符归一。验收 `blender_ue_probe22_required.py`:先载 cabmap、无 usmap 导入被拒并说明、设 usmap+Apply 后 missing 清空、重选页签不丢、再导 Ariessa_Head 得网格。🔴 **必填未设即在 UI 边界拒绝导入(2026-09-05 晚,用户 OniValley 又见空骨架 C# 栈)**:红框只是被动警告,导入按钮照样触发、一路炸进 C# 抛栈;现在三个导入入口(浏览器 Import Selected、Actors 页 Import Actor、Unreal 页 Import World)都先过 `cabmap_panel._blocking_required_options`——缺必填项直接 `self.report({'ERROR'})`+CANCELLED 并说"到本页 Load Options Form 设好再 Apply",不再跨进桥;Actor/World 委托 Import Selected 前各自先 gate(否则委托调用会因 ERROR 上报而抛)。`_ensure_source_option_form` 在 gate 里补加载表单,所以没手开过表单的页也拦得住。`UnrealPackageLoader.Load` 的 C# 拒绝仍是最后兜底。OniValley 复现证明管线本身没问题:用户就是没 Apply usmap——headless 用其自带 cabmap,设 usmap+Apply 后水体材质函数、Buddha_head_SM(3558 顶点)、BP_FirstPersonCharacter(11448 顶点)全导入成功。🔴🔴 **真根因(用户第二次报同样的错后查实,上一句"就是没 Apply"只说对了一半)**:必填判据读的是**表单输入框**(`row.path_value`)而不是**已生效值**——用户把 usmap 浏览进输入框那一刻红框就消失、新加的 gate 也放行,而只有 Apply 才把值推给内核,于是内核仍是 Schemaless、导入照样炸进 C#。`_missing_required_options` 改为只认 `_source_options(config)`(已 Apply 的那份,内核真正读的值);输入了但没 Apply 时红框改说"Typed but not applied — Click Apply",与"根本没填"区分开;另加 `_sync_bridge_to_tab`,导入前用本页已生效选项重述桥(导入路径 `resolve_import_closure` 以前从不重述,全靠桥上次被谁留下的状态)。复现坐实:输入不 Apply ⇒ 旧码 `missing=[]` 且炸 C#;新码 `missing=['unreal.mappings']` 且干净拒绝、零跨桥。🔴 **教训:门的判据必须落在"生效态"而不是"编辑态",两者之间隔着一个 Apply;拿编辑态当判据的门=看起来有门,实际全放行。**
- **Scene 页:关卡按引擎自己的两分法(2026-09-06,用户"世界分区参考 endfield 流世界,单关卡当箱庭列表")**:`Game/UnrealEngine/scene_panel.py` 新 Scene 页,与 Endfield 的 StreamingScene 同形——页内 `Scene|World` 展开选择,不再把两类混在一张手搓行表里(旧 `unreal_panel._draw_world_partition` 与其 6 个算子已删)。`Scene`=`partitioned=0` 的关卡:template_list + 搜索(走 C# 搜索引擎 `open_host_table`/`search_data_table`,与浏览器同一套规则与快捷筛选),选中整包导入。`World`=分区世界:枚举选世界 → `Size` 滑杆按世界自身地面的比例绕中心取窗 → Read Cells 把窗口与层级作为**数据集参数下推 C#**(`unreal.world.cells` 的 minX/minY/maxX/maxY/level,cut 在读的地方做,不在 UI 里筛)→ 单元 template_list → Import Window。为此 C# 补两列:`unreal.worlds` 加世界地面 `minX/minY/maxX/maxY`(其 cells 边界的并集),`unreal.session` 加 `unitScale`(=`UnrealPackageLoader.Basis.UnitScale`),面板据此把尺寸显示成米,**不在 UI 里再存一份单位换算**(否则就是第二处真源)。🔴 **always-loaded 单元默认必须取**:这种单元的 `level` 包**就是世界包本身**,小型分区世界(Titan 的 LevelInstance)常常只有这一个单元,默认把它过滤掉就得到"0 cells"的空列表——第一版正是这么翻车的。实测 Titan:7 个自足关卡、4 个分区世界;SL_CritterVillage 整包 1533 物体;LI_HatcheryTemple 全域 528×371 m,Size .25 → 132×93 m,导入 12300 物体。顺带改了浏览器默认宽高:列因子 .45/.45/.40/.28(屏幕上约 Name .45 / Path .25 / Type .12 / Deps .05 / Source .13),行数从写死 12 改成可调 `browser_rows`(默认 22,在列宽 popover 里)。**cabmap 建图零行必须说原因**(OniValley 实例):`CabMap.Build` 把解码器扫描器抛的异常按消息计数,全部失败且 0 CAB 时抛 InvalidOperationException 带首条原因(CLI 非零退出、Blender 面板弹原因),不再写出 106 字节空图 + 误导的"换解码器"提示;建图临时覆盖的 `ScanIncludeFile` 改为还原原值(原来置 null 抹掉 hook 声明的过滤器);`UnrealArchiveScan.ScanFull` 非归档文件不再触发挂载。
- **标题行(`UnrealTitles` / `UnrealKeyring`,2026-09-11)**:引擎家族解码器读得出的东西(档案目录、引擎版本、工程名)照旧自己读;读不出的三件事由**标题行以数据声明**——`Game`(CUE4Parse 的 per-studio 容器方言)、`Keys`/`Mappings`(发布端点:url + JSONPath,与 FModel 的端点配置逐字同义)、`Markers`+`Project`(靠**游戏自己发的文件**认出这是哪个标题,所以用户指到启动器目录/安装目录/工程目录都成立)。行住私有子模块 `Source/Ruri.GameHook/Unreal/<游戏>/`,按 `[UnrealTitles]` 属性在**本程序集内**发现;`FModelHook/Unreal/**` 一行游戏名都不许有。
  - **解析顺序**:`UnrealSourceOptions.Text(name, title)` = 宿主填的 > 标题发布的 > schema 缺省;`Fingerprint(title)` 把发布值与 keyset 指纹一起算进去,所以端点换了内容会自动重挂。
  - **key 文档是跨版本历史不是本版清单**(用户 2026-09-11 指正):Nikki 那份 3348 条、391KB,而这台机器上的 2537 版只有 5 个 GUID 上锁(`E90EC863…`×58、零 GUID×29、`0D867983…`×18、`35FABC58…`×3、`F6EB20D0…`×2)。整份提交、由档案 GUID 去挑,多余的永不被用;**挂完仍有档案上锁才重取一次**(`SubmitPublishedKeysAgain`),不比版本字符串。
  - **配置以线上为准,本地那份只为断网**(用户 2026-09-11 钦定):`ReadKeys`/`ReadSchema` **每次都先抓端点**,抓到就覆盖 `%LOCALAPPDATA%\RuriRipperHook\Unreal\<Product>\` 里的 `keys.json` 与那一个 `*.usmap`(写新的同时删掉同目录里别的 usmap,所以每个标题最多两个小文件);只有请求失败——断网或端点挂了——才读本地那份历史并 warn。usmap 解析全在内存(`PublishedSchema : UsmapTypeMappingsProvider` 在构造里 `Load(byte[])`)。判据:连跑两轮,第二轮 `keys.json` 时间戳前进而文件数不增(实测 WW 14:49:06 → 14:51:48,3 个文件不变)。
  - 🛑 **禁走 FModel 的端点读取器**:`DynamicApiEndpoint.GetAesKeys` 内部调 `FModel.Helper.FixKey`,而 `Helper` 这个静态类的其它成员用 WPF ⇒ 无头宿主(CLI/Blender)一碰就 `Could not load file or assembly 'PresentationFramework'`,异常被 catch 后整条链静默变成「没有 key」。`GetMappings` 不走 `FixKey`,于是**同一个读取器 mappings 成功、keys 失败**,看起来像端点坏了。`UnrealKeyring` 自带 `HttpClient` + `SelectTokens` + key 归一化,JSONPath 语义照抄。
- **标题可替换数据集(`UnrealTitleDatasets`,2026-09-11)**:家族解码器回答每一个 UE 安装,但某个工作室把引擎改到家族答不出的程度时(它的角色不在 Blueprint actor 里、它的设计表根本不是包),由**那个标题发布同名数据集顶掉**——`Datasets.Publish` 本就是 `Registered[id]=...`,覆盖就是普通发布,不需要任何人去门控。
  - **认领时机**:内核新增 `Session.Opened` 事件,`UnrealEngine_Hook` 订阅后解析标题并 `UnrealTitleDatasets.PublishFor(product)`。**不能放在 Mount 里** —— 数据集的签名在挂载之前就被解析了,放晚一步覆盖就不生效(实测报 "has no argument 'language'")。
  - 只有**认领了当前安装的那个标题**发布:一个安装只有一个标题,两个标题对同一个问题的回答绝不能同时活着。
- **鸣潮的设计数据不是 uasset**:`Aki/ConfigDB/db_<表>.db` 是**标准 SQLite**,每行 `BinData` 是 **FlatBuffers**,而**读表的 schema 游戏自己打包了**——`JavaScript/Core/Define/Config/<表>.js` 是生成的访问器(字段名+vtable 槽位),`ConfigQuery/<表>ById.js` 写明用哪个库哪张表哪个访问器。本地化在 `ConfigDB/<语言>/lang_multi_text.db`,表 `MultiText` 直接 `文本键→文本`,**语言就是文件夹名**。所以 `KuroConfigStore` 一个通用读取器对**每一张表**都成立,零硬编码;实测 152 个角色带中文名、144 个 join 到模型包。SQLite 走 `sqlite3_deserialize` 全内存,不落盘。
- **Infinity Nikki 的角色表是 UE DataTable**:`Config/DesignerConfigs/System/Character/DT_CharacterList*` 的行里 `name` 已经是**解析好的本地化文本**(引擎出包时就把 `.locres` 解掉了),`avatar_config_data` → `DA_Avatar_*` → `character_gen_asset` → CGA 的 `BakedSkeletalMeshLib` 就是烘焙好的整只网格。所以 NPC 是**一个包一整只**,不用拼。
- **新增三个通用数据集**:`unreal.files`(列**原始文件**,cabmap 只索引包所以配置和文本以前完全看不到)、`unreal.file`(取一个文件的字节,blob,走 `--data-out`)、`unreal.characters`(`CharacterModels` 角色;家族实现=cabmap 里标题声明的内容根下带骨骼网格的包)。
  ⚠ **cabmap 的 class id 是 Unity 词汇**:`BlueprintGeneratedClass` 一行产出「这蓝图**可能**含的全部类」,`SkinnedMeshRenderer` 在内,所以「带 SkinnedMeshRenderer」会把技能蓝图全捞进来(实测 1723 行)。判据要问家族表要 `SkeletalMesh` 的**完整产出集**(`Mesh`+`GameObject`+`SkinnedMeshRenderer`)。
- **`unreal.actors` 缺 usmap 不再拒答**:继承链本来就在包里(`export.Super` 跨包解析),usmap 只决定「引擎自己的类从哪开始」。没有 schema 就走到链末端为止,`kind` 退化成那个类名。之前是直接抛 `InvalidOperationException`,鸣潮的 Actors 页签因此完全不可用。
- **CLI 的 stdout 必须 UTF-8**(`Console.OutputEncoding`):打印的是游戏自己的文本,控制台默认代码页把中文换成问号之后下游无法恢复。
- **模块要带自己的包**:`Ruri.FModelHook` 是被当作**模块**加载到一个从没引用过它那些包的宿主旁边,所以 `CopyLocalLockFileAssemblies=true`;而包的**原生那一半**要 `RuntimeIdentifier=win-x64` 才会被选中(否则 `e_sqlite3.dll` 根本不会出现)。
- 🛑 **`System.Numerics.Tensors` 必须钉到 CUE4Parse 的版本**(内核 csproj,10.0.10 = 程序集 10.0.0.10):AssetRipper 的图带 10.0.0,.NET 按名绑定后**拒绝低于引用的版本**,于是 `InfinityNikkiAes.InfinityNikkiDecrypt` 最后那步 `TensorUtils.Xor` 在**每个加密档案**里抛 `FileLoadException`,被 `SubmitKeysAsync` 的 catch 吞成「这档案没开」。判据:key 全对、`keyed=1`、`missingKeys` 不降、`files=4`。修后 `mounted=134/135`、`missingKeys=0`、`files=1,173,841`。
- **原生编解码器是依赖不是缓存**:`EnsureCodecs` 先问已内置的 natives(`CUE4ParseNatives.IsFeatureAvailable("Oodle\0"u8)`),内置有就一个字节都不下;确实缺的才由 CUE4Parse 自己的 helper 按它各自跟踪的 release 取,落在**加载它们的程序集旁**(`CUE4Parse-Natives.dll` 本来就在那儿)——原生加载器只认路径,给它一个内容寻址的缓存目录纯属自找麻烦。
- **`unreal.archives` 多三列**(`index`/`keyed`):把「没给 key」「给错 key」「根本没有目录索引所以按名挂不上」三种情况分开,否则它们长得一模一样。
- **一个包本身就是网格时,它摆自己**:`unreal.placements` 原本只答世界与蓝图 actor;Nikki 这类「角色由身体+脸+衣橱在运行时拼出来」的标题根本不发 actor,整柜衣服就是一堆 `*_SkMesh` 包。现在这种包出一行、静止变换、材质走 `UnrealComponents.MaterialPaths` 同一份读法。
- **上游日志**:CUE4Parse 走 Serilog,宿主不配就是静默的。`UnrealReaderLog.Install()` 按 FModel GUI 的 `ConsoleLogSinkHook` 同一套把它写到**控制台**(不是 ripper 的资产日志,两边日志不互相耦合)。CLI 侧 `StderrLogger` 改成 `Dispatch` 开头统一装——原先只有 `--build-cab-map` 那条路装,`--data` 把上面每一条报错都丢了。
- **贴图角色层的三段**(2026-09-11 实测于 Nikki):`unreal.materials` 的参数名是**工作室自己的词汇**,不是引擎输入名——脸走 `BaseColor`/`Normal`,套装走 `Main_D`/`Main_N (Normal)`/`Main_M`/`Pattern_D`/`Sparkle_Mask`。按 `texture_roles` 的设计,这是**数据三层**:默认层(Unity 词汇,模块旁)→ 引擎/游戏层(`Game/<模块>/texture_roles.json`,新增的 UE 输入词汇层在 `Game/SBUE/`)→ **按 product 存的用户层**(`%LOCALAPPDATA%\RuriRipper	exture_roles\<Product>.json`,面板的 Unmapped Textures 就是写它的地方;家族模块不拥有标题的词汇,所以 Nikki 的落这里)。判据:探针里数「有几张材质的 Base Color 被连上」——0/69 → 14/69(加引擎层)→ 20/69(加 product 层)。
- **`unreal.materials` 的语义腿对 Nikki 没跑**(`resolver` 为空,日志里一条 `material semantics not read` 都没有),所以材质拿不到 proven 关键字,角色只能靠上面三层的数据。这条待查:`MaterialSemantics` 默认是开的,`provider.Semantics` 为空说明着色器库那条路没认出这个 build。
- **两个标题的实测(2026-09-11)**:
  - **InfinityNikki**(工程 `X6Game`,build `2537`):key 与 usmap 都由端点发;挂 134/135 档案、
    `files=1,173,841`、`missingKeys=0`;端点的 usmap 是当前线上版(REL2.9),对 2537 会在少数 tagged 结构上
    报 `Invalid bool value` / `Unknown property`——几何与材质参数照读。角色**没有蓝图 actor**,
    整柜衣服是一堆 `*_SkMesh` 包(靠新加的「包本身是网格就摆自己」那条摆出来)。
  - **WutheringWaves**(工程 `Client`,引擎 4.26):只需要 key(yarik0chka 端点,487 把);
    `unreal.settings.schema` 把 `unreal.mappings` 判为**非必填**,即它的包带属性标签,不需要 usmap;
    挂 51/51 档案、`files=1,734,097`、`missingKeys=0`。⚠ 没有 usmap ⇒ `unreal.actors` 直接抛
    (`UnrealActorScan` 要 `MappingsForGame` 才能判断类的祖先),所以这类 build 的资产发现走 **cabmap**。
- **`unreal.datatable`(新)**:一个包里每张 DataTable 的每一行,列 = 行结构自己声明的字段(union,写入顺序)。
  表自带 schema,所以**一个标题新增一张表不需要任何代码**;「按需」体现在它按包取,不预扫。
- 🛑 **CLI 传 `/Game/...` 这类对象路径时要关 MSYS 路径转换**(Git Bash 会把它改写成
  `C:/Program Files/Git/Game/...`,数据集静默返回 0 行):`MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'`。
- **cabmap 对大 UE 库是长作业**:`UnrealArchiveScan.ScanFull` 逐包读头(8 线程),WuWa 51 个 pak /
  173 万包在本机 HDD 上要几十分钟;它是**一次性索引**,建完之后 `--cab-query` + `--cab-rule` 的全资产查询才是秒级。
  🔴 **`unreal.datatable` 实测**(Nikki `DT_BP_S0029ANER2_LV1`):2 行,`struct=Struct_ClothesLevelUpInfo`,列名是 UE 给 UserDefinedStruct 字段的原生命名(`Material_2_<GUID>`),值是 `MaterialInstanceConstant'/Game/...'`。⚠ 只匹配 `UDataTable` 本身:游戏若把表做成 DataTable 的**子类**,CUE4Parse 按类名走通用 `UObject` 反序列化,`RowMap` 读不到——`X6Game/Content/Config/DesignerConfigs/Avatar/DT_AvatarConfig.uasset` 返回 0 行就待查这一条(也可能它本来就是空表)。
