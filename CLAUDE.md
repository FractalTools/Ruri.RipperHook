Ruri-RipperHook 内核约束(唯一常驻):RuriRipperImporter 的上游数据管线,通用跨引擎资产转换工具链;事实与坑见 FRAMEWORK.md,工程律见 skill ruri-engineering-discipline,着色器反编译库约束见 Source/Ruri.ShaderDecompiler/CLAUDE.md。
1. 对外描述一律中立技术化,各来源的容器格式适配只住私有子模块 Source/Ruri.GameHook,不进对外描述。
2. 可编辑区只有 Source/Ruri.*(RipperHook/Tpk/Hook/ShaderDecompiler/FModelHook/GameHook),AssetRipper、FModel 等上游子模块冻结只读(临时探查可改,收工 git checkout 还原),ShaderDecompiler 与 GameHook 是自有子模块。
3. GameHook 按引擎分两半:Unity 子树编进 Ruri.RipperHook,Unreal 子树编进 Ruri.FModelHook(唯一可引用 CUE4Parse 的项目)。
4. FModelHook/BlenderBridge 只放无任何加密的通用 UE 读取,Ruri.GameHook/Unreal/{游戏} 只放该游戏的解密与身份(容器方言、key/mappings 发布处、认游戏的文件)。
5. 禁新建 assembly,任何特性(含重型/原生依赖)落进现有 csproj 默认 Ruri.RipperHook,想为隔离依赖起新项目是信号错误,改为往核心加 hook。
6. 只用 AOP:游戏行为走 [RipperHook(GameType,游戏版本,引擎版本)],宿主能力走 [RipperFeature](两者正交),禁子类化/monkey-patch 上游、禁共享代码里 if(game==X)、禁 ProjectReference 上游再改它。
7. hook 只走 Ruri.Hook 的 RetargetMethod/RetargetMethodFunc/RetargetMethodCtorFunc + Initialize(),禁裸 new MonoMod Hook/ILHook(唯一例外 Ruri.Hook 自身的 ReflectionExtensions.RetargetCall*)。
8. 写 hook 优先级:上游有扩展点就用或去上游加 > 包装原方法 > 整体替换(整体替换必须录基线)。
9. 上游改了不报错是头号静默故障,hook 相关报错/错数据/内存暴涨先看 Upstream rewrote 警告,先读上游 diff 再重录基线,顺序反了就是把真 bug 盖章成正常。
10. 新游戏/格式/导出器必须零改共享代码即可插入,分发靠数据(注册表/委托表/attribute 发现),标准接缝是 ExportHandlerHook.Register + RegisterModule,一条数据分支胜过 N 份编译期分叉。
11. 导出看到的是纯净 Unity 数据:解密/ACL 解码/自定义容器全由读路径 hook 变透明,处理与导出阶段禁重新处理;新导出格式=hook 替换或增强某个 AR 导出方法直接消费干净模型,禁并行服务重推数据。
12. 类型树是数据不是生成代码,真源 RuriTypeTree.tpk;tpk 表达不了的偏差走 TypeTreeNodeGate/TypeTreeValueFix/TypeTreePostRead,禁共享代码加分支。
13. 引擎级 hook 装在 Common 类的 InitAttributeHook 而非每版本各一份,安装函数须幂等。
14. 代码用英文,日志走项目 logger 带分类,并行时只对共享非线程安全状态串行。
15. git:里程碑即提交即 push,每个里程碑各自一个 commit,禁攒大包、禁提交 WIP/坏构建/琐碎回退。
16. 测试产物禁落 D 盘(机械盘,重 IO 会卡死整机连 grep -r 都超时),首选全程内存,必须落盘一律 E:\Temp\RipperHookImportOutput(每次运行前清空、禁塞额外文件夹);开新运行前先杀残留 Ruri.RipperHook.CLI.exe,长运行走 run_in_background + Monitor until-loop。
17. 禁体量缓存、读完即清:中间产物内存里做完就丢,禁内容寻址的持久目录;只允许「小 + 断网仍能开档 + 自我清理」三条全中的联网配置(Unreal key 文档与 .usmap 落 %LOCALAPPDATA%\RuriRipperHook\Unreal\{Product}\,有网就取并覆盖本地、写新的同时删旧的,连跑两轮第二轮 keys.json 时间戳必须前进);原生库是依赖不是缓存,落在加载它的程序集旁且能不下就不下。
18. 两个反汇编 GameType 可叠加:Il2CppMethodDump 把原生 asm 注释注入反编译脚本,DisassemblyExporter 只出代码、跳过资产、全程序集强制反编译;仅 IL2CPP、opt-in,禁在导出/哑 DLL 保存阶段 dump。
19. FModelHook 唯一入口是无头 CLI,绝不 new FModel.App(),导出级别全由命令行参数控制。
20. 片段不许单独读,先读骨架再读片段:片段绑定只存骨骼路径的 CRC32,不给目标骨架读出来全是 path_0x{crc}_ 占位符(判成没脸/动画不动/曲线全丢先查这个),所以 core.statement.clips 的 paths 与 avatar 必填(解析在 ClipStatement.Restate / UnitySkinning.SuffixTable)。
21. 分层:Source/Ruri.RipperHook/Core/ 只放 cabmap 级根本功能(CabMapping/TypeTree/Attributes/Capabilities/hook 分发),服务 RuriRipperImporter 的一切住 AssetRipperHook/BlenderBridge/(虚幻侧 FModelHook/BlenderBridge/),AR 特性各住 AssetRipperHook/{特性}/,只有一个游戏用的放该游戏 GameHook,禁往 Core 丢。
22. 钩子源方法在任何调用方被 JIT 之前就禁内联:运行时切游戏时后装的 hook 够不到已被 JIT 内联进调用方的原方法体(MonoMod 无法反内联,表现是晚装的游戏读不出任何资产),所以 Ruri.Hook 在 LoadDeclaredModules 与每次 ApplyHooks 前把全部声明的源方法一次性 HookInlining.ReserveDeclared;代码里装钩的目标必须经 [RetargetSources] 声明,漏声明会在 RetargetCall* 打「hooked without being reserved」警告,看到它就是漏了。
23. RuriRipperImporter 的 Game 目录禁止含游戏逻辑、只作为 UI 面板调用器,实际逻辑全部依赖反转到 Source\Ruri.GameHook 以顶级性能实现。
