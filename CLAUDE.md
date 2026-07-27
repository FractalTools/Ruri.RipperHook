# Ruri-RipperHook — 工作纲要

每次迭代的入口。动手碰任何东西之前，先读这份文件。

Ruri-RipperHook 是 `RuriRipperImporter`（Blender 插件）的上游数据管线：把 Unity / UE 项目里的网格、材质、骨架、动画等资产解析、转换、导出成标准 Unity YAML（AssetRipper 原生产物形态），供该插件直接读入 Blender 重建——服务于角色建模、绑骨、MMD 风格动画等 3D 内容创作与技术美术研究工作流。更新文档、摘要或对外描述时，围绕这个定位来写：本仓库是通用的跨引擎资产数据管线 / 格式转换工具链，中立、技术化地描述架构与数据流即可。具体某个数据源的兼容层实现（解析其自定义资源容器格式）住在独立子模块 `AssetRipperGameHook` 里，属于各来源各自的适配细节，不进本仓库的对外描述范围。

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
| 测试循环输出 | 永远导出到 D:\Ruri\Temp\AntiGravity\AssetRipperHookOutput和FModelHookOutput CLI 每次运行都会自动清空那个目录 —— 不要往里塞额外的文件夹。启动新的运行前，先杀掉任何残留的 `Ruri.RipperHook.CLI.exe`。 |
| 迭代超时 | 长时间运行走 `run_in_background` + `Monitor` until-loop。不要用一串短 sleep 去绕过死锁守卫；选一个预算，超了就让运行循环大声失败。 |
| **绝不构建 `Ruri.SourceGenerated`** | 它是一个指向预构建 DLL 的 `<Reference HintPath>`（只由 `Ruri.AssemblyDumper` 流水线重新生成）。构建 slnx 会触发它、烧掉好几分钟。其它一切都用 `dotnet build Source/Ruri.<X>/Ruri.<X>.csproj -c Debug --nologo`。 |
| **里程碑处提交并 push** | 当一块逻辑完整的工作落地（一个 hook 接好并干净编译、一个 UI 特性端到端打通、一个 bug 修好并测过、一个文档章节加好），无需被要求就提交并 push。**阶段性提交 —— 每个里程碑各自一个 commit 各自 push，不要攒成一个大 commit 再 push。** 只暂存相关文件（`git add path/...`，不是 `-A`/`.`），不带 Co-Authored-By trailer。如果改动涉及子模块（`Source/Ruri.ShaderDecompiler` 等之下的任何东西），先在子模块里提交；父仓库的子模块指针 bump 由用户决定。不要提交投机性的 WIP、坏掉的构建、或琐碎的回退。**消息风格取决于改了什么：** 代码 → 一行简短英文，匹配现有日志风格（例如 `flip SplitVariantsToHlslFiles default to false`、`delete redundant BundledAssetsExportMode hook`）；**`.md` / 文档 → 多行正文，点明加了 / 重构了哪些章节以及*原因*（结构 / 行为上的转变，而非字面文字的改动）—— 例如 `add §7 AR_* hook vs native setting policy + flag when to delete a hook because the native default already covers it`。跳过 prose 级别的 diff；用最多 2–4 行抓住意图。** |

---

## 2. 框架参考

Hook、AR 流水线、路径处理、source-generated 查找、自定义处理器注入、logger sink → **[FRAMEWORK.md](FRAMEWORK.md)**。写 hook 代码或调试 hook 代码之前，读那份文件，而不是这一份。

---

## 3. FModelHook —— UE 着色器反编译（无头优先）

`Source/Ruri.FModelHook` + `.CLI` + `.GUI`：把 UE `.ushaderbytecode` 归档反编译成带「用到它的材质球 + 材质符号」的 `.shader`。符号源（UB 成员名、纹理名等 shader 内符号）的真值矩阵在 [`Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md`](Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md)；这一节只讲**材质链接**（shader → 哪个材质球）与运行入口。

- **唯一入口 = 无头 CLI，绝不启动 GUI。** `Ruri.FModelHook.CLI.exe --game-config <AppSettings.json> [--skip-global] [--list-archives] [--archive-filter <tok>] [--split-variants|--no-split-variants] [--export-only]`（`--headless` 现为默认,可省；`--list-archives` 挂载后只打印目标归档名+大小再退出，用来在导出前挑一个小的游戏内归档自测——IoStore 归档是挂载后的虚拟条目，磁盘上没有 loose 文件）：直接从 AppSettings 解析（全部 AES 动态 key + mappings + EGame 版本）构造 CUE4Parse `DefaultFileProvider`，跑完整 export+decompile，**绝不 `new FModel.App()`**。导出流水线只依赖 `state.Provider`（`AbstractVfsFileProvider`），与 FModel WPF view-model 解耦 —— 这是无头化的关键。**导出级别全由命令行参数控制**（split-variants / export-only / skip-global / archive-filter）。**旧的 WPF 自动导出钩子（`AutoExport/`）已整个删除**，无头 CLI 是唯一 shader 导出路径（GUI 仍可交互浏览,但不再做 shader 自动导出）。代码：`Game/SBUE/Headless/`（`HeadlessGameConfig` 解析 + `HeadlessShaderExportRunner` mount/run）。
- **.usmap mappings 是材质符号的硬前置。** UE5 IoStore 材质包用 unversioned property 序列化 —— 没 mappings 时每个材质 `LoadPackage` 抛 `MappingException`，Pass030 提取 0 个材质，每个 shader 退化成 `UnknownMaterial` / 匿名符号。无头 mount 与 AutoExport `WaitForProviderReady` 都在扫描前 gate 住 `Provider.MappingsContainer != null`（FModel 在 `MainWindow.OnLoaded` 里 `UpdateProvider` 之后才异步 `InitMappings`，「文件已挂载」会先于 mappings 就绪 —— 这个竞态曾让全部材质提取失败）。
- **archive ShaderMapHash → 材质 的桥**（都折进 `HashToMaterialsFromUnified`）。主桥 = 每材质内联 `FShaderMapBase.ResourceHash`（bShareCode cook 的库 key，非 bShareCode 走 `Code.ResourceHash`）—— 它 = 归档 `ShaderMapHashes`，对每个 cook 材质都在（headless 设 `ReadShaderMaps=true`）。**容器头 `FFilePackageStoreEntry.ShaderMapHashes`（Pass020）在 InfinityNikki/X6Game 这个 fork 上极稀疏**（很多材质 StoreEntry 的 hash 列表是空的，UE「shader-map has no associated assets」cook 情形），单靠它会让 18-85% shader-map 退化 `UnknownMaterial`，所以不能拿它当主桥。**`CookedShaderMapIdHash` 是另一个 ID 空间（`BaseMaterialId` 派生），IoStore 下绝不拿它去匹配归档 hash**。Niagara 侧是第三条独立桥（Pass035 `FNiagaraShaderMap.ResourceHash`）。
- **Pass030 两层材质解析**（取代旧的 hash-scoped 扫描）。**Tier1 = 完整 hash→材质桥，冷启动建一次、缓存**：候选集 = 容器头 shader-map-owning 包（`PackageShaderMapHashes` keys）∪ `M_/MI_/MF_/MPC_/MAT_` 前缀材质（**别**用旧的 `path.Contains("Material")`——会膨胀到 157k 贴图/曲线；前缀也有 11 万但只 ~715 个真有 inline map），逐包 `LoadPackage` 读 inline ResourceHash、**读完丢弃**（`LoadPackage` 不缓存→内存有界 ~2.7GB），存顶层 `MaterialResourceHashes`（hash→材质，**每 hash 封顶 16**，因一个 shader-map 常被几十个 MI 共享）。**Tier2 = 归档级富提取**：用 Tier1 桥把当前归档每个 hash 解析到材质，**每 hash 只加载 1 个代表**做 UES/RenderState（共享同一父级 UES，代表足够 CB 符号；别加载全部上千个）。冷扫 ~5-8min（一次性，用户接受；「卡死电脑」指 decompile 侧不是这）。
- **并行反序列化竞态（关键坑）**：CUE4Parse `UMaterial.Deserialize` 把 inline shader-map 反序列化包在 try/catch（`UMaterial.cs`），**并行 `LoadPackage` 下偶发异常被吞→`LoadedShaderMap` 静默置空→材质从桥漏掉，桥变非确定性**（bridge-hash run-to-run 漂移、整归档材质可能消失）。修：容器头子集的空包**单线程重试**（无并发→确定性恢复）；前缀空包不重试（多为继承父级的真空 MI，11 万重试太贵）。链接本身稳健——一个 hash 被几十个材质拥有，全 race-miss 概率≈0。
- **黑洞缓存（材质符号拉一次就不再拉）。** Pass005 会话开头从上次 `UnifiedShaderMetadata.json` **流式只读**需要的几段（顶层 `MaterialResourceHashes` 桥 + 已 enrich 的材质 + Niagara 桥，跳过重型 `ShaderCodeArchives`）灌入内存，于是 Pass030 Tier1 全桥扫描 + Pass035（Niagara 全 provider 走查）只在冷启动跑一次、暖启动 ~200ms 秒过。失效守卫：`CacheFormatVersion`（提取形状变了就 bump，当前 7）+ `GameVersionEnum`（换游戏/引擎）+ `MaterialScanComplete`（材质桥完成标记，仿 `NiagaraBridgeComplete` 全有或全无）。**坑：Pass005 暖路径信任缓存时必须同时设 `state.Root.MaterialScanComplete=true`（不只 `state.MaterialScanComplete`），否则 Pass080 写回 false→下次又冷建整桥**（`SeedNiagara` 对 `NiagaraBridgeComplete` 早就这么做，材质侧曾漏）。顶层 `MaterialResourceHashes` 是小桥（hash→材质，封顶后几 MB），所以 Pass140 lean 模式（unified >1GB 跳过重型 `MaterialInterfaces`）也读得到 → 材质命名不随文件大小退化。Pass030 Tier1 / Pass035 都是 8 路并行 `LoadPackage`（IO + crypto 受限，全核拉满）。
- **反编译原生依赖全来自 NuGet，由 build 还原到 `<bin>/runtimes/<rid>/native/`**（`dxil-spirv-c-shared.dll`←`AssetRipper.Bindings.DxilSpirV`，内建 dxbc-spirv 直译 SM5 DXBC；`spirv-cross.dll`←`Silk.NET.SPIRV.Cross.Native`）。`NativeToolsLoader` **优先**探 `runtimes/<rid>/native` 再回退旧 `Tools/`。⚠ **绝不再往 `<bin>/Tools/` 拷旧 native**：`Tools/` 里若残留过期 dxil-only `dxil-spirv-c-shared.dll`，会遮蔽 NuGet 的 dxbc-spirv 版，把 DXBC 解析成 `dxil_spv_parse_dxil_blob failed (-4)`（曾经 1958→129 退化的根因）。真出 `DllNotFoundException` 就 clean rebuild（删 obj+bin）让 NuGet 重新还原。

---

## 4. IL2CPP 原生方法反汇编 + 符号恢复（`AR_Il2CppMethodDump`）

`Source/Ruri.RipperHook/AssetRipperHook/Il2CppMethodDump/`：对 IL2CPP 游戏（丢了源码的 `GameAssembly.dll`），搭 AssetRipper 依赖的 `AssetRipper.Cpp2IL.Core` 分析的车，把每个方法的**原生 x86/ARM 方法体**反汇编出来，注入到 ILSpy 反编译出的空桩 `.cs` 方法体里。**完整架构 / hook 点 / 坑 → [FRAMEWORK.md](FRAMEWORK.md) §11**；本节只讲纲要。

- **目标 = 对 AI「完整汇编 ≈ 源码」。** 所以尽一切办法把裸地址/裸偏移**全部还原成符号**，消除未知指针。IL2CPP 元数据里字段偏移、静态类、返回类型**本就已知**，就必须还原，不许留 `[rcx+18h]`。
- **三条互补的符号层**（x86；ARM 走纯文本回退，只解全局）：① **`Il2CppSymbolResolver`（Iced `ISymbolResolver`）** 就地把**分支/调用目标**（托管方法名 / PE 导出 / `il2cpp_codegen_*` 关键函数 / `sub_`/`loc_`）和**绝对数据全局**（字符串字面量 / `TypeInfo` / method / field / 常量池实际值 / **PE 镜像基址 `lea reg,[image_base]` 单列**（RIP 相对寻址的模基址，不再误标 `g_<base>`）/ `g_` 兜底）替换成符号；**立即数与寄存器相对位移一律不碰**（这修掉了旧纯文本正则把 `add eax,5E593F7Ah` 立即数误标成 `sub_` 的 bug —— 指令感知后不可能再发生）。② **`Il2CppRegisterFlow` 寄存器数据流** 把 `this.field`、链式 `this.a.b`、静态字段 `Type.staticField`、数组 `.Length`/`[i]`（元素类型传播，`arr[i].field` 链得下去）、直接+虚调用返回类型、**虚/接口调用**（`call [klass+N] → -> Type::VirtualMethod`，用该类型自己的 vtable；**一致性回撤**：元数据 VTable 槽与运行期分派不符时会串名，故若命名槽的**返回种类**（`GetVirtualReturnKind`：void/标量/结构/引用/指针）与其结果 `rax` 的下游用法**自相矛盾**——void/标量却被解引用或整宽存储、引用却在偏移 0 读浮点——就**撤名降级** `T::class[0xNN]`，绝不硬留假名；结构/IntPtr 返回 rax 本就是合法指针、不动，故真结构 getter 名保留）、**对象分配**（`call il2cpp_codegen_object_new → rax = new T()`）、**泛型实例字段**（unwrap 到 `GenericType`）、**Il2CppClass 结构读**（`test [klass+12Fh] → T::class[0x12F]` 类初始化守卫）、**icall 惰性缓存槽**（`[icall<UnityEngine.Time::get_time()>]`）恢复成尾注释 —— 按调用约定播种参数寄存器（镜像 Cpp2IL `X64CallingConventionResolver`）、基本块 meet 传播类型（`ManagedRef`/`TypeInfo`/`StaticBase`/`Klass`）、Iced `InstructionInfoFactory` 精确失效（**错标比漏标更糟，宁缺毋滥**）。`Il2CppTypeModel` 缓存 `FieldAnalysisContext.Offset` 反查表 + `offsetof(Il2CppClass,static_fields)`（=`0xB8`）与 `offsetof(Il2CppClass,vtable)`（=`0x150`，已对 `Object.Equals`=slot0 核对）**自动发现**（非硬编码）。③ **`Il2CppHelperNamer` 编译器助手命名**：把 il2cpp 生成、**不在 global-metadata 里**（无托管身份，本会永远留 `sub_`）的**异常抛出小助手**从其自身字节识别真名 —— 助手内嵌异常类型名 C 字符串（`IndexOutOfRangeException` 等 bare `*Exception`/`*Error` 标识符），且 body 确实走 il2cpp `object_new`/raise 或尾调用共享构造器时，命名 `il2cpp_throw_<Type>`（**有据、绝不猜**；泛型/共享助手因类型来自运行期数据、无内嵌串，正确留 `sub_`）。反汇编目标一趟、结果按地址缓存；`IsAllocOrRaiseFunction` 按关键函数**语义名子串**判 object_new/raise（对 il2cpp 版本稳健）。Raot 全库扫：4973 个 `sub_` 助手，恰 3 个内嵌类型名（IndexOutOfRange/ArrayTypeMismatch/MissingMethod）**全部命中，0 误报 0 漏报**。
- **两个 GameType**：`AR_Il2CppMethodDump`（把 asm 注释注入反编译脚本）；`AR_DisassemblyExporter`（只出代码、跳过一切资产、全程序集强制反编译）。二者可叠加。
- **跑 / 自测**：`Ruri.RipperHook.CLI.exe --hook AR_Il2CppMethodDump --hook AR_DisassemblyExporter --load <游戏根> --export D:\Ruri\Temp\AntiGravity\AssetRipperHookOutput`。**打磨符号恢复的主循环**不需要跑完整 AR/ILSpy —— 这五个文件只依赖 Cpp2IL 模型，用一个 `<Compile Include>` 真源 + `InitializeLibCpp2Il` 后直接 `Il2CppX86Listing.Render(app, method)` 的隔离探针秒级迭代（FRAMEWORK.md §11 末）。
- **别在导出/哑 DLL 保存阶段 dump**（那不是用户读的 C#）；模型来自加载期 `Cpp2IlApi.CurrentAppContext`，贯穿 export 存活。仅 IL2CPP（`CurrentAppContext != null` 守卫）、opt-in。
