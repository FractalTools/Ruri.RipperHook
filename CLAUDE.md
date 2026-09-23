# Ruri-RipperHook — 内核约束(唯一常驻)

> 框架事实/流水线/坑表 = [FRAMEWORK.md](FRAMEWORK.md)(写或调 hook 前读那份,不是这份) · UE 符号真值矩阵 = [Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md](Source/Ruri.ShaderDecompiler/UE_SYMBOL_SOURCES.md)。
> 通用工程律 = skill `ruri-engineering-discipline`(写码/重构/移植前先过)。规则与用户指令冲突或规则本身错 → 先改本文,再写代码。带真实姓名的本机路径(`\Users\<人名>\`)禁止记录并提交,通用账户名不在此列。
> 定位:`RuriRipperImporter`(Blender 插件)的上游数据管线 —— 通用跨引擎资产格式转换工具链;对外描述中立技术化,各来源的容器格式适配住 `Source/Ruri.GameHook` 私有子模块,不进对外描述。

1. **可编辑区 = 现有 `Source/Ruri.*/**`**(RipperHook/Tpk/Hook/ShaderDecompiler/FModelHook/**GameHook**);`AssetRipper/**`、`FModel/**` 等上游子模块冻结,只读;`Source/Ruri.ShaderDecompiler` 是**我们自己的**子模块(独立仓库,与 GameHook 同规矩:先在子模块提交推送再 bump 父仓 gitlink),可编辑。
   `Source/Ruri.GameHook` 是**我们自己的**私有子模块,按引擎分两半、各进各的 assembly:`Unity/**` 编进 `Ruri.RipperHook`,`Unreal/**` 编进 `Ruri.FModelHook`(唯一可引用 CUE4Parse 的项目)。
   **界线**:`Source/Ruri.FModelHook/FModelHook/Unreal/**` = **无任何加密的通用 UE 读取**(挂载/读取器/标题注册表/端点抓取机制);`Ruri.GameHook/Unreal/<游戏>/` = **只放该游戏特定的解密与身份**(容器方言、key/mappings 发布在哪、靠哪些文件认出它)。通用能力写进前者,游戏特例写进后者。
2. **禁新建 assembly**:任何特性(含重型/原生 NuGet 依赖)落进现有 csproj,默认 `Ruri.RipperHook`;想为"隔离依赖"或"可扩展性"起新项目=信号错误,改为往核心加 hook。
3. **只用 AOP**:游戏行为走 `[RipperHook(GameType.X,"游戏版本","引擎版本")]`,宿主能力走 `[RipperFeature("Name")]`(两者正交,FRAMEWORK §7);禁在子模块里子类化/monkey-patch,禁共享代码里 `if(game==X)`,禁 ProjectReference 上游再改它。临时探查可改子模块,收工 `git checkout` 还原。
4. **hook 只走 Ruri.Hook** 的 `[RetargetMethod]`/`[RetargetMethodFunc]`/`[RetargetMethodCtorFunc]` + `Initialize()`;禁裸 `new MonoMod...Hook/ILHook`(唯一例外=`Ruri.Hook` 自身的 `ReflectionExtensions.RetargetCall*`)。
5. **上游改了不报错是本仓库头号静默故障** —— 出任何 hook 相关报错/错数据/内存暴涨,先查 FRAMEWORK §2 的 IL 指纹闸门(黄字 `Upstream rewrote ...`);先读上游 diff 再重录基线,顺序反了=把真 bug 盖章成正常。
6. **写 hook 优先级**:上游有扩展点就用/没有就去上游加 > 包装原方法 > 整体替换(整体替换必须录基线)。
7. **扩展点而非特例**:新游戏/格式/导出器必须零改共享代码即可插入;分发靠数据(注册表/委托表/attribute 发现),标准接缝 = `ExportHandlerHook.CustomAssetProcessors` + `RegisterModule(...)`(FRAMEWORK §6);一条数据分支胜过 N 份编译期分叉。
8. **导出看到的是纯净 Unity 数据**:解密/ACL 解码/自定义容器全由读路径 hook 变透明,处理与导出阶段禁重新处理;新导出格式(如 USD)=hook 替换或增强某个 AR 导出方法直接消费干净模型,禁并行服务重推数据。
9. **类型树是数据不是生成代码**:真源 `Source/Ruri.GameHook/Unity/TypeTree/RuriTypeTree.tpk`(内嵌资源),重打 = `dotnet build Source/Ruri.Tpk/Ruri.Tpk.csproj -c Debug` 再跑其 exe;tpk 表达不了的偏差走 `[TypeTreeNodeGate]`/`[TypeTreeValueFix]`/`[TypeTreePostRead]`,禁共享代码加分支。
10. **引擎级 hook 装在 Common 类的 `InitAttributeHook`**,不是每版本各一份;安装函数须幂等(范例 `EndfieldShaderBindingHook.Install()` 跨 5 版本重入无害)。
11. 风格:**代码=英文**;日志走项目 logger 带分类(FRAMEWORK §10);并行时只对共享非线程安全状态串行(范例 FRAMEWORK §12 逐次反编译锁);其余(禁缩写/一文件一单元/禁注释/0-GC/SIMD)见 skill。
12. **git**:里程碑即提交即 push,每里程碑各自一个 commit 各自 push,禁攒大包;`add` 按名点名禁 `-A`/`.`;严禁 AI 署名 trailer;子模块先提交推送再 bump 父仓 gitlink;禁提交 WIP/坏构建/琐碎回退。消息:代码=一行简短中文(匹配现有日志风格);`.md`=多行正文,点明加/重构了哪些章节以及**原因**(结构或行为转变,非字面改动),2–4 行抓意图。
13. **测试循环**:🛑 **产物禁落 D 盘**(机械盘,重 IO 会把整机卡死,连 `grep -r`/`ls -R` 都会超时)——首选全程内存(UE 那条路本就直出宿主、不落盘);必须落盘时一律 `E:\Temp\RipperHookImportOutput`(每次运行前清空,禁塞额外文件夹);开新运行前先杀残留 `Ruri.RipperHook.CLI.exe`;长运行走 `run_in_background`+`Monitor` until-loop,禁用短 sleep 串绕过死锁守卫。
14. 🛑 **禁体量缓存,读完即清**(2026-09-11 钦定):中间产物一律内存里做完就丢;**禁内容寻址的持久目录**(旧版留下 1.3 GB 的 `texture_cache`/871 个 `.img`,当前代码里连写入方都没有了还在盘上——贴图现在打包进 .blend 并删临时文件)。
    **允许留的只有「小 + 断网仍能开档 + 自我清理」三条全中的**:联网取到的 key 文档与 .usmap 落 `%LOCALAPPDATA%\RuriRipperHook\Unreal\<Product>\`,**有网就取、取到就覆盖本地**(用户钦定:配置以线上为准);只有断网或端点挂了才读本地那份历史。写新的同时删掉同目录里旧的,所以每个标题最多两个小文件、不会堆积。usmap 读进来仍走 `UsmapTypeMappingsProvider.Load(byte[])`,解析全在内存。
    **原生库是依赖不是缓存**:加载器只认路径,所以落在加载它们的程序集旁(`CUE4Parse-Natives.dll` 本来就在那儿),且先问已内置的 natives、能不下就不下。
    判据:连跑两轮,第二轮的 `keys.json` 时间戳必须前进(证明确实重取并覆盖),且 `%LOCALAPPDATA%` 下该标题的目录文件数不增。
15. **两个反汇编 GameType 可叠加**:`Il2CppMethodDump`(把原生 asm 注释注入反编译脚本)、`DisassemblyExporter`(只出代码、跳过资产、全程序集强制反编译);模型来自加载期 `Cpp2IlApi.CurrentAppContext`,仅 IL2CPP、opt-in,**禁在导出/哑 DLL 保存阶段 dump**;架构/坑/迭代探针见 FRAMEWORK §12。
17. 🛑 **片段不许单独读,先读骨架再读片段**:片段的绑定只存骨骼路径的 CRC32,对着骨架才叫得出名字;不给目标骨架,读出来的骨骼曲线全是 `path_0x<crc>_` 占位符,对不上任何骨骼(判成没脸 / 动画不动 / 曲线全丢,先查这个)。所以 `core.statement.clips` 的 `paths` 与 `avatar` 是必填参数(解析在 `ClipStatement.Restate` / `UnitySkinning.SuffixTable`)。Unity 里不存在没有 avatar 的动画目标:运行时就是把目标对象层级的可逆路径字符串算一次哈希来绑定的,`paths` 就是这份层级。
16. **FModelHook 唯一入口 = 无头 CLI,绝不 `new FModel.App()`**;导出级别全由命令行参数控制;架构/桥/缓存/native 依赖见 FRAMEWORK §15。
