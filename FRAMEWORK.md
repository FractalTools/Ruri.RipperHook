Ruri-RipperHook 框架事实:只记与常识相反或读代码得不到的约束与坑,规则见 CLAUDE.md。
1. 只能整解决方案编译(dotnet build -c Debug),两个读取器共用 Source/0Bins/Debug,单编内核会把共享包覆盖回较窄版本。
2. 继承来的 public static hook 不被 ApplyTypeHooks 拾取(无 FlattenHierarchy),须在派生类 InitAttributeHook 显式 apply;单 Ret 的 void 实例方法上 postfix-continue 不可靠。
3. 整体替换复刻的是写 hook 当天的上游快照,HookTargetFingerprint 比对 HookBaselines.json(按优化配置分存),换配置前先设 RURI_HOOK_BASELINE=1 录基线。
4. AR 不留 ObjectInfo.ObjectData 也不设 AssetCollection.FilePath,只能在 SerializedAssetCollection.ReadData postfix 取;BuildAssetList 期间禁调 asset.Write,bundle 重建不支持(用 AssetsTools.NET)。
5. 让 AR 自己消费路径=往 IAssetBundle.Container 合成条目并 prefix-continue hook OriginalPathProcessor.Process,OriginalPath 存储值保留正斜杠。
6. 自定义 IAssetProcessor 只经 ExportHandlerHook.Register 注入,禁镜像上游 processor 表(曾漏 OriginalPathProcessor 致整场景静默导空),锚点 LightingDataProcessor 消失即抛。
7. 解码器按 GameType/版本、特性按 RipperFeature 区分,唯一目录 HookCatalog(取 Version ≤ 本 build 的最新解码器,手动指定永远优先,引擎文件与 bundle 版本各取各的)。
8. 特性只扩 AR 原生之上(先查 ProcessingSettings/ExportSettings/ImportSettings 有无等价属性),Blender 宿主特性由 RipperBlenderBridge.HostFeatures 一处启用且禁启 SkipProcessingAnimation(曲线靠 EditorFormatProcessor 重建)。
9. 游戏身份只认 globalgamemanagers 里 PlayerSettings(ClassID 129)的字节,禁用目录名/cabmap 名/app.info/游戏名单;变换过的引擎文件用 RipperEngineFile + 静态 TryDecrypt,由 HookCatalog 收集不按游戏索引。
10. AR 的 Logger 没 sink 就静默:CLI 接 StderrLogger+FileLogger,GUI 在 InstallAssemblyResolver 之后接 ConsoleLogger,FModel GUI 靠 ConsoleLogSinkHook 重配 Serilog。
11. 类型模型是数据:RuriTypeTree.tpk 由 Core.TypeTree 运行时解释,重打包=编 Source/Ruri.Tpk 后跑 Ruri.Tpk.exe 再编 Release,新游戏版本的 dump 先进 D:\Ruri\Git\FractalTools\TypeTree(缺 Common/ 拒打包)。
12. 分叉引擎(目录名=CustomEngineType id,自定义覆盖层的版本类型是 Experimental)是覆盖层不是快照:游戏快照从不对缺席的类打 null,只有引擎快照里缺席才打。
13. 树后还有树没有的数据时用 hook 类上的 TypeTreeRaw 标记;fork 类的双结构拷贝不支持数组的数组,TypeTreeFallbackHook 在两棵树相等时只建单结构对象。
14. IL2CPP 方法体注释:LibCpp2IL 只经 app.LibCpp2IlContext 读(旧静态入口一调即抛,曾被空 catch 吞成整功能静默失效),AddTransform 必须 public static,MonoMod.Iced 与真 Iced 同暴露 Iced.Intel 须 extern alias,PrintAssembly 的静态 MasmFormatter 须串行。
15. 符号恢复宁漏勿错:地址操作数就地换符号、立即数不碰、运行期才填的 .data/.bss 一律 g_XXXX,某 (类型,槽) 的 vtable 名被任一站点证伪即整方法一致撤名。
16. 整个游戏当一张 CAB 依赖图按需取用,唯一实现 Core/CabMapping:cabmap 存绝对游戏根(搬家即缓存失效、响亮报错要求重建),格式变就 bump magic 整体重建。
17. chunk 条目名与 CAB 名不能互转,名字索引须记条目名;大游戏必须 bundle-granular 加载(GameBundleHook.LoadIncludeFile 在解密前按条目名过滤,否则整个 .chk 解出 OOM)。
18. CabSelection 的谓词只约束种子不约束闭包,按分区扫描时每分区克隆一份 Regex(共享实例并发会退化成分配风暴)。
19. 材质符号以 .usmap 为硬前置;hash→材质主桥是内联 FShaderMapBase.ResourceHash(CookedShaderMapIdHash 是另一个 ID 空间),并行 LoadPackage 下 CUE4Parse 吞反序列化异常使桥非确定,空包须单线程重试。
20. native 依赖只来自 NuGet,禁往 {bin}/Tools/ 拷旧 native(反编译库的探测顺序与症状见其 CLAUDE.md)。
21. UE 只有一个解码器 UnrealEngine_4.0 按引擎家族认领,UE 包不转 Unity 资产而以 unreal.* 数据集直出 Blender,通用读取代码不许出现游戏名。
22. FModelHook 模块由构建写 Modules/{程序集名}.module 自声明、Bootstrap.LoadDeclaredModules 开机加载,须自带包(CopyLocalLockFileAssemblies+RuntimeIdentifier=win-x64),System.Numerics.Tensors 必须钉到 CUE4Parse 的版本,否则解密末步 FileLoadException 被吞成"档案没开"。
23. UE 引擎版本读 exe 里 Build.version 的 UTF-16 字面量(不读版本资源),缺失即标 unreal.engine required 并拒绝挂载;读取参数全是 unreal.settings.schema 声明的源选项,代码不认识任何选项名,必填门判已 Apply 的生效态而非输入框。
24. 标题差异(容器方言、key/usmap 发布端点、认标题的文件标记)由 UnrealTitles 数据行声明,禁用 FModel 的端点读取器(拖进 WPF,无头下异常被吞成"没有 key")而用自带 UnrealKeyring。
25. 工作室把引擎改到家族答不出时由标题发布同名数据集顶掉(UnrealTitleDatasets),认领必须在内核 Session.Opened 事件里,且只认领当前安装的标题发布。
26. cabmap 的 class id 是 Unity 词汇,UE 蓝图筛选要问家族表要 SkeletalMesh 的完整产出集,不能按"带 SkinnedMeshRenderer"筛(会把技能蓝图全捞进来)。
27. unreal.placements 只有一份父在子前的定序(UnrealSceneGraph.Collector,世界与蓝图共用);FastGeo 容器无 actor,须按 ComponentClusters[].StaticMeshComponents[] 发行,否则静默导空。
28. 世界分区 cell 的关卡包禁止拼名,必须问 cell 自己(StreamingData.PackageName、WorldAsset、LevelStreaming.WorldAsset、FastGeoContainerPtr 依次),常驻 cell 的关卡包就是世界包所以默认必须取。
29. cook 与引擎版本对不上只由标题以数据声明那一处回移(UnrealTitle.SkeletalMeshSectionVersion + UnrealSerializationDialect 的 IL manipulator),禁整份声明 custom version;回移字段没补时异常被 DeserializeObject 吞掉,最后在 SkeletalMeshDto 报无关的 NullReferenceException。
30. 贴图 BC5 法线必须补回蓝通道,unreal.textures 并行编码前须先 WarmEncoder(fpng 静态构造非线程安全)。
31. ACL 解码吐长度约 0.9983 的非单位四元数,须先归一化再对半球,否则静止长 clip 被读成抖动写成百万级键;曲线抽稀容差取 ACL 编码的 ErrorThreshold 乘 unreal.animation.tolerance。
32. 材质参数按引擎声明的 kind 布局(CachedLayouts)读,EMaterialParameterType 不是 UENUM 不能从 schema 推序;语义角色只从编译后 base pass shader 经 SPIR-V 污点反推(MaterialSemanticsResolver),Blender 侧不得再按名猜图。
33. usmap 来自 exe 旁的 pdb:Ruri.Tpk --unreal-reflection {exe} [out.usmap],再 Ruri.Tpk --unreal {usmap} 直出自定义引擎 tpk,别注入 UnrealMappingsDumper(DebugGame 会崩)。
34. Git Bash 传 /Game/... 对象路径须 MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*',否则被改写成 C:/Program Files/Git/Game/... 而数据集静默返回 0 行。
35. AssetRipper 生成的 FetchDependencies 对整个类只有一条非列表 PPtr 路径的类(NavMeshSettings、PhysicsManager、Physics2DSettings、Prefab 等)是永不结束的序列,物化即耗尽内存,取依赖一律用 AssetWalker 的 WalkStandard 并以最外层字段名栈分类,别枚举它。
36. 音频剪辑的通道数、频率与整数采样数只在 FSB5 子声音头里,序列化字段只有单精度秒数 m_Length;core.statement.audioclips 取头里的值,与 Ogg 重建所用的是同一组(Fmod5Sharp 按头建 Ogg 信息包,末页粒度位置以头里采样数封顶),不取 m_Length 乘频率反推,也不另写 FSB 或 Vorbis 解析。

37. containers 列了 dds 时,DXT1、DXT5、BC7 与其 Crunch 变体的单图贴图按存储序(下行在前)原样成 DDS:整条 mip 链、Crunch 先解成块、BC7 用 DX10 头 dxgiFormat 98、头里不带色彩空间、不翻行不解包法线;其余格式、立方体与多图贴图仍走列出的其他容器(table 的 container 列即扩展名)。
38. 剪辑里绑在脚本组件上的浮点曲线,属性名只能由目标脚本的字段表对 CRC32 回查还原(AnimationClipConverter 的 PathChecksumCache.Add(script)),脚本布局只在请求带 scripts=1 时加载,不带就全是 script_0x哈希占位符,看到占位符先查请求;曲线索引带出绑定脚本的 scriptAssembly、scriptNamespace、scriptClass,同一份索引里的路径仍可能是 path_0x哈希占位符,要对播放它的层级按 CRC32 解析。
39. 相机包组件(core.statement.cinemachine)里指向相机包外对象的指针不拒产:场景里的节点或其组件仍写成节点源路径(层里没有这个节点才拒产),相机包自己的 ScriptableObject 仍整条嵌入,其余(例如输入动作引用、别的包的配置)写成与 core.statement.scripts 同一种 asset 引用(kind=asset、key、class、name),被指的记录由脚本记录层持有;只有指向任何已加载档案都没有的对象才拒产。
40. 剪辑里每条曲线(位移、旋转、缩放、欧拉、浮点)的块在时间、数值、入出切线四个数组之后再带三个数组:入权重与出权重(各与数值同宽)、权重模式(每键一个:0 无、1 入、2 出、3 双),曲线索引条目 weighted=true 标明有;索引条目另带曲线上存的 m_PreInfinity、m_PostInfinity 两个整数原值 preWrap、postWrap(不转成别的枚举,读取端按源引擎自己的值域读;AssetRipper 注入的 CurveLoopTypes 只是它的命名,没有对过引擎)与浮点曲线绑定原生类时的类名 className(绑在脚本上的曲线类名由脚本三项给出,不填 className);任何会重排曲线块的读写(ClipStatement 的 Parse 与 Write)必须按条目的 weighted 与曲线维数算块长并原样带出这几项,按条目 off 读前四个数组的读者不受影响,人形求解产出的曲线块没有这三个数组(weighted 为假)。
41. 剪辑的动画事件(AnimationClip.m_Events)写进剪辑索引的 events 数组,每条 time、functionName、stringParameter(源里的 data)、floatParameter、intParameter、messageOptions(SendMessageOptions 的序列化整数),传对象的再有 objectReference(与脚本记录同一种 asset 引用:kind、key、class、name,没有传对象就不写);ClipStatement 重排曲线块时必须把 events 原样带出(它重建索引),没有事件的剪辑没有 events 成员。
42. 纹理编码是并行的(UnityStatement.EncodeTextures 里的 Parallel.For),而 AssetRipper 的资源文件列表在首次使用时加载并被别的线程同时遍历(Bundle.ResolveResource)、资源流的取数是先设位置再读(StreamedResourceExtensions.GetContent),两处都不是线程安全的,偶发「集合在枚举时被修改」崩溃;所以流式纹理(StreamData 有来源、ImageData 为空)的字节先在并行之前串行取好、挂在纹理的 ImageData 上,编码完再清掉,别的在并行里读流的新代码同样要先串行取好。
43. Animator 行(core.statement.animators)新增列 keep_controller_state(0 或 1):这个 Animator 的控制器在对象失活再激活时是否保持状态。取值按版本序列化的字段读:新名 keep-animator-state-on-disable 在就取它,否则旧名 keep-animator-controller-state-on-disable 在就取它,两个都没有取 0(引擎缺省:失活再激活回到默认状态);StatementAnimator 记录相应多一个 KeepControllerState(缺省 false),StatementTables.Animators 与 UnityStatement 里建这条记录的地方是唯一两处。
44. 渲染器的材质槽一槽一个键:空键 = 槽里没有材质(空指针,文件号与路径号都是 0,原作如此,不是导出漏带);槽指针非空而任何已加载档案都取不到对象时同样是空键,但会记一条报告「material slot points at no loaded object」,带渲染器名、槽号与该槽的文件号和路径号,把「槽本来就空」和「档案答不出」分开;展开渲染器的三条路径(节点展开 FlattenRenderer 与另两条按预制体或资产展开)共用 UnityStatement.MaterialKeys,不再各写一遍。
45. Animator 行(core.statement.animators)新增列 path_ordinal(整数):该 Animator 所在节点之前、语句里有多少个同一源路径的节点,与脚本记录层的 pathOrdinal 同口径(都按放置对象加入的顺序即节点表顺序数,UnityStatement.PathOrdinalOf 与脚本记录层的 Placement 读同一份放置对象表),用来分辨挂在两个同路径节点上的 Animator(例:一个场景里两个同名兄弟的实例各自 Body 上的 Animator,路径字符串逐字相同);StatementAnimator 相应多一个 PathOrdinal(缺省 0)。
46. 人形肌肉剪辑的根曲线按剪辑的 Bake Into Pose 三个旗标拆分:剪辑的 LoopBlendPositionXZ、LoopBlendPositionY、LoopBlendOrientation(原生二进制里 ClipMuscleConstant 的偏移 +0x96D、+0x96C、+0x96B,根运动的提取按它们决定哪些轴进根运动):旗标为真的轴留在身体姿态(臀部)里,为假的轴拆进根的运动(MotionT 与 MotionQ,旋转只拆偏航),拆出的运动成路径空串的位置与旋转曲线;KeepOriginalPositionXZ、KeepOriginalPositionY、KeepOriginalOrientation 是「Based Upon: Original」的参考系选择,不是烘焙开关,不参与拆分(此前误当烘焙旗标读,两套旗标不一致的剪辑轴的归属反了)。读取处是 HumanoidClipGenericizer.Convert 与 ClipCurveBlob 建剪辑索引的地方,索引字段与解算参数一并改名 bakePositionXZ、bakePositionY、bakeOrientation(ClipStatement、HumanoidClipGenericizer.Solve、AvatarMuscleReferential.BodyTransform);剪辑自带 MotionT 与 MotionQ 通道时仍按通道拆,不看旗标。
47. Animator 行(core.statement.animators)新增列 avatar_kind(文本:Humanoid 或 Generic,没有头像的行为空)与 human_scale(数:原生 Animator.humanScale,人形头像取头像里 Human 的缩放、按单精度最短十进制取值,通用头像与没有头像取 1);人形头像 = 头像常量的 HumanSkeletonIndexArray 非空(人体把骨架节点映射到人体骨骼);StatementAnimator 相应多 AvatarKind(缺省空串)与 HumanScale(缺省 1),StatementTables.Animators 与 UnityStatement.AvatarKindOf 是仅有的写处。