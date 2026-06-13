# Ruri-RipperHook — 工作纲要

每次迭代的入口。动手碰任何东西之前，先读这份文件。

更新文档、摘要或对外描述时，绝不要包含私有 `AssetRipperGameHook` 游戏解密部分的任何描述。本项目应当被描述为一个正常、合法的解包 / 导出 / 数据处理工具。

---

## 0. 工程信条 —— 要么顶级，要么不做

Ruri 优化契约的通用内核（态度 / 1:1 移植 / 代码风格 / 黑洞级性能）已抽进 `ruri-engineering-discipline` skill——**写码 / 重构 / 移植前先过那条**。本节只保留本仓库特化的扩展性信条（§C）：**每个特性都设计成一个全局最优框架里的扩展点。** §1 是机械性的「不许碰什么」。如果某个改动必须偏离 —— 或你发现某条信条本身就是错的 —— 先改对应处（skill 或本节），再写代码。

### A·B. 态度（永不降级）+ 1:1 移植纪律

> 永不降级、顶级算法、无损重构、1:1 忠实移植（参考 = ground truth、先读真源码、同义替换不算偏离、忠实移植不写 oracle、汇报「1:1 移植，源 = `file:line`」）等通用铁律已抽进 `ruri-engineering-discipline` skill（§A/§B）——写码 / 移植前先过那条，此处不重复。本仓库的同义替换范例：AssetStudio `AnimationClip` → AssetRipper `IAnimationClip`、native SWIG 调用 → 它的 C# 绑定等价物。

### C. 可扩展性 —— 设计框架，而非个案

- **构建扩展点，而非特例。** 每个特性都是一个家族里的一员；为这个家族做设计。新游戏 / 格式 / 导出器的支持，必须无需改动共享代码就能插进来。
- **共享路径里不许硬编码分支。** 埋在共享代码里的 `if (game == X)` / `if (format == Y)` 是设计臭味。通过数据来分发 —— 一张注册表、一个委托列表、一个由 attribute 发现的 handler。这是 §1「只用 AOP」规则的泛化；本仓库的标准接缝是 `ExportHandlerHook.CustomAssetProcessors` 和 `RegisterModule(...)`（FRAMEWORK.md §6）。加一个 case = 加一条注册，而绝不是改分发器。
- **零变体分发。** 一条数据分支的路径胜过 N 份编译期分叉的拷贝 —— 更少的拷贝，更少「修复被遗漏」的地方。
- **冻结的上游是神圣的。** 对 AssetRipper / 子模块的行为，只能通过**现有 `Ruri.*` 项目内部**的 hook/module 来添加（§1）—— 绝不改动冻结的代码树，也**绝不新起一个 assembly**。「扩展点」是核心里的一个 hook/module 注册，而不是一个新项目。「不许碰」和「为扩展而设计」是同一枚硬币的两面。

### D·E. 代码风格 + 黑洞级性能内核

> 通用代码风格（禁缩写 / 一文件一单元 / 禁单行堆叠 / 注释随文件语言）与黑洞级性能（span / 0-GC / 全核并行 / 全 SIMD / 测量优化尖峰 / 顺手优化）已抽进 `ruri-engineering-discipline` skill（§C/§D）——写码前先过那条，此处不重复。本仓库特化：**代码 = 英文**；日志走项目 logger 带分类（FRAMEWORK.md §9）；并行化时只对共享非线程安全状态串行（如 FRAMEWORK.md §11 逐次反编译锁）。

---

## 1. 硬性规则（不许违反）

| 规则 | 细节 |
|---|---|
| 可编辑区 | **现有的** `Source/Ruri.*/**` 项目（Ruri.RipperHook, Ruri.AssemblyDumper, Ruri.Hook, Ruri.SourceGenerated, Ruri.ShaderDecompiler）。就地编辑它们。 |
| **不许新建 assembly** | **绝不为某个特性新建 `.csproj` / 项目 / assembly —— 哪怕是 `Ruri.*` 命名的也不行。** 每个特性都落在**现有 `Source/Ruri.*` 项目内部**（默认：`Ruri.RipperHook` 核心），形式是 Ruri.Hook 的 attribute hook 加上它们的支撑代码。新的 NuGet 依赖 —— 即便是重型 / 原生的（例如某个 USD 绑定）—— 也加到那个现有 csproj 上。如果你发现自己在为「隔离」一个依赖、一个导出器、或者为了「可扩展性」而搭一个新项目，**停下** —— 那个本能（泛化的 §0.C）在这里是错的；往核心里加一个 hook。 |
| 冻结区 | `AssetRipper/**` 以及所有子模块。 |
| 临时探查 | 为了确认「哪个方法才是正确的 hook 目标」，可以临时改一个子模块，**然后 `git checkout` 还原回上游**。最终实现必须以 Ruri.Hook attribute hook 的形式住在 `Source/Ruri.*/**` 里。 |
| 只用 AOP | 游戏特定行为通过 `[RipperHook(GameType.X, version)]` 类（或非游戏工具的等价物）来添加，由它们安装方法 hook。**不要**在子模块里子类化 / monkey-patch 基类型，**不要**在共享代码里嵌 `if (game == X)` 分支，**不要** ProjectReference 上游再去改它。 |
| **hook 只走 Ruri.Hook** | 每个 `Source/Ruri.*` 项目都必须通过 `Ruri.Hook` 框架来安装方法 hook —— 在派生自 `RuriHook` 的类上用 `[RetargetMethod]`、`[RetargetMethodFunc]`、`[RetargetMethodCtorFunc]` attribute，并在启动时调用 `Initialize()`。**不要**直接 `new MonoMod.RuntimeDetour.Hook(target, detour)` / `new ILHook(target, manipulator)` —— 走 Ruri.Hook，这样基于 attribute 的发现、hook 注册和清理才保持一致。唯一可以裸用 MonoMod 的地方是 `Ruri.Hook` 自身内部（`ReflectionExtensions.RetargetCall*`）。 |
| **导出看到的是纯净 Unity 数据** | 游戏解密、ACL 解码、自定义容器格式都由上游的**读路径** hook 变得透明 —— 等到任何处理 / 导出代码运行时，AR 已经持有**纯净、原汁原味的 Unity 数据**（标准 source-gen 类型；clip 曲线已解码；mesh 已 de-stream）。**绝不要**在导出阶段重新处理解密 / ACL / 自定义格式。一个新的导出格式（例如 USD）是通过**用 hook 替换或增强一个 AR 导出方法**、直接消费 AR 已经干净的模型来添加的 —— 而不是用一个并行服务去重新推导数据。 |
| 参考范例 | `Source\Ruri.RipperHook\AssetRipperHook`（游戏 hook）和 `Source\Ruri.AssemblyDumper\Pipeline\ArAssemblyDumperHook.cs`（构建期 hook）展示了标准的 Ruri.Hook attribute 模式（`AddMethodHook`、`[RetargetMethod]`、`[RetargetMethodCtorFunc]`）。 |
| 引擎级 hook 安装 | 每个引擎的跨版本设置放在 *Common* hook 类的 `InitAttributeHook` 里，而不是每个版本各放一份。EndField 在 `EndFieldCommon_Hook.InitAttributeHook` 里安装它的 shader 绑定后处理器；`EndFieldShaderBindingHook.Install()` 是幂等的，所以跨 5 个版本重入也无害。 |
| 测试循环输出 | 永远导出到工作区根目录的 `TestLoopOutput/`。CLI 每次运行都会自动清空那个目录 —— 不要往里塞额外的文件夹。启动新的运行前，先杀掉任何残留的 `Ruri.RipperHook.CLI.exe`。 |
| 迭代超时 | 长时间运行走 `run_in_background` + `Monitor` until-loop。不要用一串短 sleep 去绕过死锁守卫；选一个预算，超了就让运行循环大声失败。 |
| **绝不构建 `Ruri.SourceGenerated`** | 它是一个指向预构建 DLL 的 `<Reference HintPath>`（只由 `Ruri.AssemblyDumper` 流水线重新生成）。构建 slnx 会触发它、烧掉好几分钟。其它一切都用 `dotnet build Source/Ruri.<X>/Ruri.<X>.csproj -c Debug --nologo`。 |
| **里程碑处提交** | 当一块逻辑完整的工作落地（一个 hook 接好并干净编译、一个 UI 特性端到端打通、一个 bug 修好并测过、一个文档章节加好），无需被要求就在本地提交。**仅本地 —— 绝不 push。** 只暂存相关文件（`git add path/...`，不是 `-A`/`.`），不带 Co-Authored-By trailer。如果改动涉及子模块（`Source/Ruri.ShaderDecompiler` 等之下的任何东西），先在子模块里提交；父仓库的子模块指针 bump 由用户决定。不要提交投机性的 WIP、坏掉的构建、或琐碎的回退。**消息风格取决于改了什么：** 代码 → 一行简短英文，匹配现有日志风格（例如 `flip SplitVariantsToHlslFiles default to false`、`delete redundant BundledAssetsExportMode hook`）；**`.md` / 文档 → 多行正文，点明加了 / 重构了哪些章节以及*原因*（结构 / 行为上的转变，而非字面文字的改动）—— 例如 `add §7 AR_* hook vs native setting policy + flag when to delete a hook because the native default already covers it`。跳过 prose 级别的 diff；用最多 2–4 行抓住意图。** |

---

## 2. 框架参考

Hook、AR 流水线、路径处理、source-generated 查找、自定义处理器注入、logger sink → **[FRAMEWORK.md](FRAMEWORK.md)**。写 hook 代码或调试 hook 代码之前，读那份文件，而不是这一份。

---

## 3. FModelHook —— UE 着色器反编译（无头优先）

`Source/Ruri.FModelHook` + `.CLI` + `.GUI`：把 UE `.ushaderbytecode` 归档反编译成带「用到它的材质球 + 材质符号」的 `.shader`。符号源（UB 成员名、纹理名等 shader 内符号）的真值矩阵在 [`Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md`](Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md)；这一节只讲**材质链接**（shader → 哪个材质球）与运行入口。

- **唯一入口 = 无头 CLI，绝不启动 GUI。** `Ruri.FModelHook.CLI.exe --game-config <AppSettings.json> [--skip-global] [--archive-filter <tok>] [--split-variants|--no-split-variants] [--export-only]`（`--headless` 现为默认,可省）：直接从 AppSettings 解析（全部 AES 动态 key + mappings + EGame 版本）构造 CUE4Parse `DefaultFileProvider`，跑完整 export+decompile，**绝不 `new FModel.App()`**。导出流水线只依赖 `state.Provider`（`AbstractVfsFileProvider`），与 FModel WPF view-model 解耦 —— 这是无头化的关键。**导出级别全由命令行参数控制**（split-variants / export-only / skip-global / archive-filter）。**旧的 WPF 自动导出钩子（`AutoExport/`）已整个删除**，无头 CLI 是唯一 shader 导出路径（GUI 仍可交互浏览,但不再做 shader 自动导出）。代码：`Game/SBUE/Headless/`（`HeadlessGameConfig` 解析 + `HeadlessShaderExportRunner` mount/run）。
- **.usmap mappings 是材质符号的硬前置。** UE5 IoStore 材质包用 unversioned property 序列化 —— 没 mappings 时每个材质 `LoadPackage` 抛 `MappingException`，Pass030 提取 0 个材质，每个 shader 退化成 `UnknownMaterial` / 匿名符号。无头 mount 与 AutoExport `WaitForProviderReady` 都在扫描前 gate 住 `Provider.MappingsContainer != null`（FModel 在 `MainWindow.OnLoaded` 里 `UpdateProvider` 之后才异步 `InitMappings`，「文件已挂载」会先于 mappings 就绪 —— 这个竞态曾让全部材质提取失败）。
- **archive ShaderMapHash → 材质 的三桥**（都折进 `HashToMaterialsFromUnified`）：(1) 容器头 `PackageShaderMapHashes`（Pass020，IoStore `FFilePackageStoreEntry.ShaderMapHashes`，权威）；(2) 每材质内联 `FShaderMapBase.ResourceHash`（Pass030，bShareCode cook 的库 key，非 bShareCode 走 `Code.ResourceHash`）—— **`CookedShaderMapIdHash` 是另一个 ID 空间（由 `BaseMaterialId` 派生），IoStore 下绝不拿它去匹配归档 hash**；(3) Niagara `FShaderMapBase.ResourceHash`（Pass035，独立 ID 空间）。
- **黑洞缓存（材质符号拉一次就不再拉）。** Pass005 会话开头从上次 `UnifiedShaderMetadata.json` **流式只读**需要的几段（材质 + Niagara 桥，跳过重型 `ShaderCodeArchives`/`PackageShaderMapHashes`）灌入内存缓存，于是昂贵的 Pass030（材质扫描）+ Pass035（Niagara 全 provider 走查）只在冷启动跑一次、之后秒过。失效守卫三层：`CacheFormatVersion`（提取形状变了就 bump）+ `GameVersionEnum`（换游戏/引擎）+ Niagara 的 `NiagaraBridgeComplete`（整段全有或全无，只有完成标记在才信）。Pass030 的全 provider 回退 gate 在 `produced`（提取 + 缓存复用），不是 `extracted`，否则全缓存命中会误触发全量重扫。Pass030/035 都是 8 路并行 `LoadPackage`（IO + crypto 受限，全核拉满）。
- **反编译原生依赖全来自 NuGet，由 build 还原到 `<bin>/runtimes/<rid>/native/`**（`dxil-spirv-c-shared.dll`←`AssetRipper.Bindings.DxilSpirV`，内建 dxbc-spirv 直译 SM5 DXBC；`spirv-cross.dll`←`Silk.NET.SPIRV.Cross.Native`）。`NativeToolsLoader` **优先**探 `runtimes/<rid>/native` 再回退旧 `Tools/`。⚠ **绝不再往 `<bin>/Tools/` 拷旧 native**：`Tools/` 里若残留过期 dxil-only `dxil-spirv-c-shared.dll`，会遮蔽 NuGet 的 dxbc-spirv 版，把 DXBC 解析成 `dxil_spv_parse_dxil_blob failed (-4)`（曾经 1958→129 退化的根因）。真出 `DllNotFoundException` 就 clean rebuild（删 obj+bin）让 NuGet 重新还原。
