// UE umap -> 完整场景包导出 workflow（ruri-engineering-discipline §E 闭环：foundation -> 并行实现 -> 逐个对抗验证 -> 集成自测）
//
// 用户铁令（升级版，凌驾一切）：「完整场景导出 必须从源码真理级还原导出场景所关联的每个字节；fmodel 忽略的也要加上」。
// => 这不再是"只导 FModel 预览能渲染的子集"。**场景 umap 里出现的每一个 actor / 每一个 component / 它递归引用的每一个
//    asset，都必须被无损导出，一个字节不丢。** FModel 预览丢弃的全部要补上：Niagara 粒子系统、PostProcessVolume、
//    ExponentialHeightFog、SkyAtmosphere、SphereReflectionCapture、CineCamera、LevelSequence、RuntimeVirtualTextureVolume、
//    WorldDataLayers、PlayerStart、SkyLight/RectLight…… 凡是场景里有的，全部 source-truth 级还原。
//
// 【交付物 = 一个"完整场景包"】CLI `--export-map-direct` 跑完，输出目录下产出：
//   1. <map>.glb(+parts)   —— 可渲染层：静态/实例/蓝图/样条几何 + 地形 + 材质(嵌入PBR)+贴图 + 灯光(全类型) + 相机；
//                            每个 glTF 节点带 `extras`（其来源 actor/component 的关键属性 + 指向无损数据文件的链接）。
//   2. Actors/*.json        —— 无损场景图：**每一个 actor + 每一个 component** 的完整属性树（CUE4Parse JSON = uasset
//                            反序列化真值，逐字段无损）。涵盖 FModel 全部忽略的非渲染类型。
//   3. Assets/...           —— 依赖闭包：umap 递归引用的**每一个 asset** 无损导出（mesh->glb+json / texture->解码图+全mip+json /
//                            material->参数json / niagara/curve/sound/dataasset->json(+二进制)）。一个引用都不能漏。
//   4. scene-manifest.json  —— 清册：每 actor->其数据文件+GLB节点+引用的 asset；每 asset->其导出；完整性计数(actors=2537、
//                            assets=N、dropped=0)。这是"每个字节都在"的证明。
//
// 真值矩阵（全部 Read 真源逐行对照，禁臆造）：
//   渲染放置(已验证)：FModel/FModel/Views/Snooper/Renderer.cs、Models/SplineModel.cs
//   CUE4Parse 转换器：FModel/CUE4Parse/CUE4Parse-Conversion/{Meshes/glTF/Gltf.cs, Landscape/LandscapeExporter.cs, Materials/MaterialExporter2.cs}
//   CUE4Parse 类型：  .../Component/{SplineMesh/USplineMeshComponent.cs+FSplineMeshParams.cs, Lights/ULightComponent.cs+LightUtils.cs, Landscape/ULandscapeComponent.cs}、Exports/Actor/ALandscape.cs
//   无损 JSON：        FModel JsonConvert.SerializeObject(package.GetExports(), Indented)（Save-Properties 路径，逐字段无损）
//   依赖闭包：         CUE4Parse IoPackage.ImportedPackages(Lazy<IoPackage?[]>) 直接导入 + 属性树里 FPackageIndex/FSoftObjectPath 递归
//   UE 5.7.4 引擎源(每个类型的字段真理)：E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime/
//                     （Landscape/、Engine/Classes/Components/*Light*.h、SplineMeshComponent.h、CineCameraComponent.h、
//                       Niagara/、Engine/Classes/Engine/{ExponentialHeightFog,PostProcessVolume,SkyLight}.h、Engine/Classes/Components/SkyAtmosphereComponent.h …）
//   SharpGLTF.Toolkit 1.0.0-alpha0023：SceneBuilder.AddLight/AddCamera；node.Extras；MaterialBuilder.WithChannelImage/WithEmissive/WithMetallicRoughness/WithNormal。
//
// 实测场景事实(E:/Games/OniValleyDemo5.1, GAME_UE5_1)：持久图 Oni_Valley.umap + 328 WP _Generated_ cell；
//   2537 actor / 35 ExportType / 324821 静态放置 / 112 唯一网格；蓝图几何全在 BlueprintCreatedComponents[]；无骨骼网格。
//   非渲染 actor(FModel 丢弃,本 workflow 必须无损补全)：NiagaraActor32、PostProcessVolume16、ExponentialHeightFog13、
//   LevelSequenceActor15、SphereReflectionCapture5、CineCameraActor5、RuntimeVirtualTextureVolume2、SkyLight13、SkyAtmosphere1、
//   WorldDataLayers1、CullDistanceVolume1、PlayerStart1、以及 BP_Fireflies70 的 Niagara 等。
//
// 用法：Workflow scriptPath 执行本文件。全程 opus；验证 agent 独立、预设代码错、读真源逐行核、就地修。
// 输出目录：D:/Ruri/Temp/AntiGravity/FmodelHookOutput（CLI 每次清空；启动前杀残留 Ruri.FModelHook.CLI.exe）。

export const meta = {
  name: 'glb-scene-complete-export',
  description: 'UE umap->完整场景包：可渲染层(GLB:几何/材质/贴图/灯光全类型/相机/地形/样条) + 无损层(每 actor/component 完整属性 JSON) + 依赖闭包(每个引用 asset 无损) + 清册；零妥协、每个字节、source-truth；并行实现+逐个对抗验证+OniValley 集成自测',
  phases: [
    { title: 'Foundation', detail: '统一组件分发地基 + 完整包脚手架：PlacedComponent/IComponentExporter/GlbSceneContext/ComponentResolver + WorldGlbExporter(GLB+Actors/+Assets/+manifest) + StaticMesh满血 + 其余 stub，保静态 324821 平价', model: 'opus' },
    { title: 'Cells', detail: '7 并行 EDIT-ONLY cell：材质/样条/灯光(全类型)/地形/相机/无损场景数据(每actor+component)/依赖闭包(每引用asset)', model: 'opus' },
    { title: 'Compile-gate', detail: '串行唯一构建点：编译整套、修 cell 编译错误、census+小 cell 冒烟', model: 'opus' },
    { title: 'Verify', detail: '8 路独立对抗验证(EDIT-ONLY)：读真源逐行结构 diff + 跨系统不变量 + 完整性不变量(零丢失)，就地修', model: 'opus' },
    { title: 'Integration', detail: 'build + OniValley 全量导出 -> 每 actor/component/asset 都产出(零 dropped) + manifest 对账 + 每字节验收', model: 'opus' },
  ],
}

const ROOT = 'D:/Ruri/Git/FractalTools/Ruri-RipperHook'
const GLB = `${ROOT}/Source/Ruri.FModelHook/Game/SBUE/GlbSceneExport`
const FMODEL = `${ROOT}/FModel/FModel`
const CUE = `${ROOT}/FModel/CUE4Parse`
const CONV = `${CUE}/CUE4Parse-Conversion`
const UE = 'E:/Games/UnrealEngine-5.7.4-release/Engine/Source/Runtime'
const SKILL = 'D:/Tools/Users/Administrator/.claude/skills/ruri-engineering-discipline/SKILL.md'
const EXE = `${ROOT}/FModel/FModel/bin/Debug/net8.0-windows/win-x64/Ruri.FModelHook.CLI.exe`
const CLIPROJ = `${ROOT}/Source/Ruri.FModelHook.CLI/Ruri.FModelHook.CLI.csproj`
const OUT = 'D:/Ruri/Temp/AntiGravity/FmodelHookOutput'
const GAME = 'E:/Games/OniValleyDemo5.1/Oni_Valley_VFX/Content/Paks'
const MAPPINGS = 'E:/Games/OniValleyDemo5.1/Mappings.usmap'
const BASEARGS = `--export-map-direct --game-dir "${GAME}" --ue-version GAME_UE5_1 --mappings "${MAPPINGS}" --export-out "${OUT}"`

const BUILD = `dotnet build "${CLIPROJ}" -c Debug --nologo -v quiet`
const KILL = `Get-Process Ruri.FModelHook.CLI -EA SilentlyContinue | Stop-Process -Force`

const COMMON = `项目：Ruri-RipperHook 的 FModelHook —— UE umap -> **完整场景包**导出子系统。动手前先读 ${SKILL}
（§A 永不降级 / §B 1:1 忠实移植=参考是 ground truth，先 Read 真源逐行核、同义替换不算偏离、汇报"已 1:1 移植，源=file:line" /
§C 代码风格：英文注释、一文件一内聚单元、禁缩写、禁单行堆叠 / §D 黑洞性能：span/0-GC/全核并行）。

【最高铁令——每个字节，零妥协】用户原话："完整场景导出 必须从源码真理级还原导出场景所关联的每个字节；fmodel 忽略的也要加上"。
=> 不是"只导能渲染的"。场景里**每个 actor、每个 component、它递归引用的每个 asset** 都必须无损导出。FModel 丢弃的
（Niagara/PostProcessVolume/Fog/SkyAtmosphere/ReflectionCapture/CineCamera/LevelSequence/VirtualTexture/DataLayers/
PlayerStart/SkyLight/RectLight…）全部要补上。**绝不以"glTF 表达不了"为由丢数据**——表达不了就走无损 JSON + 闭包资产，
但数据一个字节不能少。任何"跳过/忽略/简化/future phase"都是违令。

【交付物=完整场景包】CLI \`--export-map-direct\` 输出目录产出：
  (1) <map>.glb(+parts)：可渲染层(几何/材质嵌入PBR/贴图/灯光全类型/相机/地形/样条) + 每节点 extras(来源 actor/component 关键属性+链接)。
  (2) Actors/*.json：每个 actor + 每个 component 的**完整属性树**（CUE4Parse JSON 无损）。
  (3) Assets/...：umap 递归依赖闭包里**每个 asset** 无损导出。
  (4) scene-manifest.json：清册 + 完整性计数(actors/components/assets/dropped=0)。

【架构（统一按组件分发，禁 per-game 分支）】cooked 蓝图 actor 组件全在 \`BlueprintCreatedComponents[]\`（实测：
BP_Boulder=13 StaticMeshComponent、BP_Torii=18、BP_Ancestral_tree=19、BP_Chochin_lamp=网格+PointLight、
River_spline/BP_Rope_spline=SplineMeshComponent×N、BP_Fireflies=Niagara+PointLight）；StaticMeshActor 用命名属性
\`StaticMeshComponent\`；InstancedFoliageActor 用 \`InstanceComponents[]\`；LandscapeStreamingProxy 用 \`LandscapeComponents[16]\`。
ComponentResolver 把这些并集去重成 PlacedComponent 流，WorldGlbExporter 用 IComponentExporter 注册表按组件类型分发
（spline 在 static 前，因 USplineMeshComponent : UStaticMeshComponent）。加一种类型=加一个 exporter 文件+注册一行。
**无损层(Actors/)与闭包层(Assets/)与渲染层正交**：对全部 2537 actor 无差别走无损 JSON，不管渲不渲染。

【硬规则】① 只可编辑 ${GLB}/ 下文件 + 必要时 CLI ${ROOT}/Source/Ruri.FModelHook.CLI/。绝不改冻结区 ${CUE}/、${FMODEL}/
（FModel/CUE4Parse=ground truth，只读）。绝不新建 assembly/csproj。
② 渲染几何必须仍由 CUE4Parse \`Gltf.ExportStaticMeshSections\`(Gltf.cs) 产出(逐字节 1:1)；landscape 由
\`ALandscapeProxy.TryConvert\`(LandscapeExporter.cs) 产出 CStaticMesh 再走同一条。绝不手写顶点导出。
③ 坐标：SceneTransform.cs 已建 Unreal-local->glTF 桥(节点矩阵 N=S^-1*W)。新组件世界变换走
\`SceneTransform.CalculateTransform(component, baseTransform)\`(AttachParent 链)、节点矩阵走 \`SceneTransform.NodeMatrix\`；绝不另立坐标约定。
④ 无损 JSON 走 \`JsonConvert.SerializeObject(obj, Formatting.Indented)\`（CUE4Parse 反序列化真值=逐字段无损；同 FModel Save-Properties）。
⑤ 日志走传入的 log/logError 委托(HookLogger)，带 [GlbScene] 前缀；禁裸 Console.WriteLine。

【构建/自测】仅构建 CLI（带 FModelHook；绝不构建 slnx/Ruri.SourceGenerated）：\`${BUILD}\` —— 0 error。
启动 CLI 前先 \`${KILL}\`。CLI：${EXE}
快速 census（跳过几何，~1-2min）：env RURI_GLB_CENSUS_ONLY=1 跑 \`${EXE} ${BASEARGS} --map "Levels/Oni_Valley.umap" --with-materials\`
单 cell（小）：\`--map "MainGrid_L8_X0_Y0_DL0.umap"\`。全量集成：\`--map "Levels/Oni_Valley.umap" --with-materials\`（~3-5min，324821 放置）。
输出在 ${OUT}（每次清空）。GLB 校验：读 .glb 的 JSON chunk 统计 meshes/materials/lights/cameras/nodes，或二进制 grep
"KHR_lights_punctual"/"images"/"cameras"；无损层数 Actors/*.json 与 census 的 actor/component 数对账；闭包层 Assets/ 与引用集对账。`

// ============ Phase 1：Foundation（地基 + 完整包脚手架；linchpin，必须先成且保静态平价）============
phase('Foundation')
const FOUNDATION_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    filesCreated: { type: 'array', items: { type: 'string' } },
    interfaceSignatures: { type: 'string' },
    packageLayout: { type: 'string' },
    buildClean: { type: 'boolean' },
    staticParityHeld: { type: 'boolean' },
    parityEvidence: { type: 'string' },
    blueprintGeometryNowExported: { type: 'boolean' },
    remainingStubs: { type: 'array', items: { type: 'string' } },
    sourceCitations: { type: 'string' },
  },
  required: ['filesCreated', 'interfaceSignatures', 'packageLayout', 'buildClean', 'staticParityHeld', 'parityEvidence', 'blueprintGeometryNowExported', 'remainingStubs', 'sourceCitations'],
}
const foundation = await agent(
  `${COMMON}

【任务=Foundation 地基 + 完整包脚手架（linchpin）】把 ${GLB} 重构成"统一按组件分发 + 完整场景包"，满血实现静态网格 +
蓝图几何，并搭好无损层/闭包层/清册的脚手架(stub)。**必须保证现有静态导出平价不退化**(全量原 324821 静态放置一个不少)。

先 Read：现有 ${GLB}/WorldGlbExporter.cs、WorldActorCollector.cs、SceneTransform.cs、SceneCensus.cs、UE_GlbSceneExport_Hook.cs；
真源 ${FMODEL}/Views/Snooper/Renderer.cs(WorldMesh:533-584/ProcessMesh:586-674/CalculateTransform:676-690/OverrideVertexColors:746-758)、
${CONV}/Meshes/glTF/Gltf.cs(ExportStaticMeshSections:198)。

**建立文件（全部新建在 ${GLB}/，命名空间 Ruri.FModelHook.Game.SBUE.GlbSceneExport）：**
1. \`PlacedComponent.cs\`：readonly struct { UObject Component; Transform WorldTransform; IPropertyHolder OwnerActor; }
2. \`IComponentExporter.cs\`：interface { bool CanExport(UObject component); void Export(in PlacedComponent placed, GlbSceneContext context); }
3. \`GlbSceneContext.cs\`：共享场景构建服务（搬 WorldGlbExporter 现有"建场景"内核）：SceneBuilder、ExporterOptions、IFileProvider、
   log/logError、**网格共享缓存**、part-flush(MaxInstancesPerGlb=50000)、AddRigidMesh(meshBuilder,Matrix4x4)、AddLight(LightBuilder,Matrix4x4)、
   AddCamera(CameraBuilder,Matrix4x4)、GlbMaterialFactory 句柄、**当前节点 extras 注入口**。
   ⚠**网格共享 key 必须含 OverrideMaterials 集合**：key=(mesh.LightingGuid, 有序 override 材质 PathName 列表)（修 271 处错共享 bug）。
4. \`ComponentResolver.cs\`：(actor, baseTransform) -> IEnumerable<PlacedComponent>，并集去重来源：BlueprintCreatedComponents[] ∪
   InstanceComponents[] ∪ 命名单组件属性(StaticMeshComponent/Mesh/LightMesh/SplineMesh/ComponentTemplate/**CameraComponent**) ∪
   landscape(actor 是 ALandscapeProxy)。去重按组件身份。每组件世界变换=SceneTransform.CalculateTransform。跳过纯 SceneComponent/SpringArm。
   ExportType=="LODActor" 跳过(Renderer.cs:448)。**注意**：resolver 只管"可被某 exporter 渲染的组件"；无损层不经 resolver，单独遍历全 actor。
5. \`WorldGlbExporter.cs\`（重构）：保留 Export(world,key,outDir,ct) 签名 + collector + SceneCensus.Log + RURI_GLB_CENSUS_ONLY 门 + part 收尾。
   主流程：
   (A) 渲染层：foreach actor -> ComponentResolver.Resolve -> foreach PlacedComponent -> 注册表第一个 CanExport 命中的 exporter.Export。
       注册表顺序 **[Spline, Static, Light, Camera, Landscape]**(spline 在 static 前)。
   (B) 无损层：调 \`CompleteSceneDataExporter.ExportAll(collectedActors, outDir/Actors, manifest)\`（stub）——对**每个 actor + 每个 component**写完整属性 JSON。
   (C) 闭包层：调 \`DependencyClosureExporter.ExportClosure(world.Owner 包, provider, outDir/Assets, manifest)\`（stub）——递归导出每个引用 asset。
   (D) 清册：写 \`scene-manifest.json\`（actors/components/assets 计数 + dropped 列表，理想 dropped=0）。
   这套(B/C/D)默认随 --export-map-direct 产出（完整包是默认行为）。
6. \`StaticMeshComponentExporter.cs\`（**满血**）：CanExport=component is UStaticMeshComponent && not USplineMeshComponent。1:1 移植
   Renderer.ProcessMesh + 旧 AddMeshInstance：ISM 每实例(Renderer.cs:549-555)、普通 SMC、ComponentTemplate/GeometryCollection(Renderer.cs:561-577)、
   **OverrideMaterials(修 271 bug，Renderer.cs:642-652，并入网格共享 key)**、OverrideVertexColors(746-758)/TextureData(606-640)、bMirrored(604)。
7. **stub**（建文件+注册+log 一行"not yet implemented—deferred to cell (N)"，CanExport 返回正确类型判定）：
   \`SplineMeshComponentExporter.cs\`(is USplineMeshComponent)、\`LightComponentExporter.cs\`(ULightComponent 子类:Point/Spot/Rect/Directional/Sky)、
   \`CameraComponentExporter.cs\`(CameraComponent/CineCameraComponent)、\`LandscapeComponentExporter.cs\`(is ALandscapeProxy)。
8. **stub**：\`GlbMaterialFactory.cs\`+\`MaterialTextureWriter.cs\`(按名绑定+最简 sidecar，平价；真嵌入PBR/无损解码留材质 cell)、
   \`CompleteSceneDataExporter.cs\`(stub：先只对每 actor 写一个最简 JSON 占位+计数，真无损全字段留 cell)、
   \`DependencyClosureExporter.cs\`(stub：先只列出 world 包的直接 ImportedPackages 计数，真递归闭包导出留 cell)、
   \`SceneManifest.cs\`(清册数据结构+写出；foundation 写全，cell 往里加计数)。
   这些类的 public 方法签名**定死**，cell 只填实现不改签名。

**关键不变量**：节点矩阵全程 SceneTransform.NodeMatrix(来自 CalculateTransform)；网格共享 key 含 override 集合(写=读同公式)；
渲染几何 100% 由 Gltf.ExportStaticMeshSections 产出。

**自测闭环**：a) \`${BUILD}\` 0 error。 b) \`${KILL}\`；\`${EXE} ${BASEARGS} --map "Levels/Oni_Valley.umap"\` -> exit 0；
原静态放置一个不少(蓝图几何并入后总数应 >324821，记录新值并解释)；census 的 Unclassified 里 BP_* 渲染产出含 torii/灯笼/巨石/古树；
确认 271 处 OverrideMaterials 不再无脑跳过；Actors/ 目录已开始产出(占位即可)；scene-manifest.json 写出。

返回 {filesCreated[], interfaceSignatures(IComponentExporter/PlacedComponent/GlbSceneContext/CompleteSceneDataExporter/
DependencyClosureExporter/SceneManifest/GlbMaterialFactory/MaterialTextureWriter 的精确签名,供 cell mirror), packageLayout(输出目录结构),
buildClean, staticParityHeld, parityEvidence, blueprintGeometryNowExported, remainingStubs[], sourceCitations(每处移植 源=file:line)}。`,
  { label: 'foundation:scene-package', phase: 'Foundation', model: 'opus', schema: FOUNDATION_SCHEMA })
log(`Foundation: buildClean=${foundation?.buildClean} staticParity=${foundation?.staticParityHeld} bpGeometry=${foundation?.blueprintGeometryNowExported}`)

if (!foundation?.buildClean) {
  log('⛔ Foundation 未干净编译 —— 中止 fan-out（地基不稳）。人工介入后 resume。')
  return { aborted: 'foundation-build-failed', foundation }
}

// ============ Phase 2：Cells（7 并行，EDIT-ONLY，各 owns 自己文件，禁互改、禁并行 build）============
phase('Cells')
const CELL_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    cell: { type: 'string' },
    filesOwned: { type: 'array', items: { type: 'string' } },
    implemented: { type: 'boolean' },
    completenessSelfReview: { type: 'string' },
    acceptanceVsTask: { type: 'string' },
    sourceCitations: { type: 'string' },
    deviations: { type: 'string' },
  },
  required: ['cell', 'filesOwned', 'implemented', 'completenessSelfReview', 'acceptanceVsTask', 'sourceCitations', 'deviations'],
}
const FOUND_CONTRACT = `【地基契约（foundation 已定，Cells 必须 mirror、禁改）】\n${foundation?.interfaceSignatures}\n输出包结构：${foundation?.packageLayout}\n你只填你 owns 文件的 body（已注册/已接好），绝不动 WorldGlbExporter/GlbSceneContext/ComponentResolver/IComponentExporter/PlacedComponent/SceneManifest。`

const CELLS = [
  {
    key: 'Material', own: 'GlbMaterialFactory.cs + MaterialTextureWriter.cs',
    task: `零妥协完整材质：既无损 sidecar 又嵌入 PBR。真源：${CONV}/Materials/MaterialExporter2.cs(GetParams+Decode/Encode)、
CMaterialParams2 字段集(Diffuse/Normal/Specular/Emissive/PackedPBR…)、${CONV}/Meshes/glTF/Gltf.cs(按材质名建 MaterialBuilder)。
实现：(1) 无损 sidecar：每唯一材质写参数 JSON + **全部贴图解码全 mip 落图**(包路径镜像)；健壮性：原生 Decode 串行化在全局锁下 +
按贴图 PathName 全局去重 + 逐贴图 try/catch 续跑 + 解码前 log 名。(2) 嵌入 PBR：SharpGLTF MaterialBuilder(WithChannelImage
KnownChannel.BaseColor/Normal/Emissive/MetallicRoughness…)，"导出后按 material.Name 回绑"对每 part 的 ModelRoot.LogicalMaterials 设贴图通道，
几何 1:1 不动；贴图字节复用 sidecar 解码结果。材质默认开启。
自测验收：单 cell --with-materials 出 .png 贴图+材质 JSON；.glb 二进制含 "images"。`,
  },
  {
    key: 'Spline', own: 'SplineMeshComponentExporter.cs',
    task: `1:1 样条形变网格(River_spline/BP_Rope_spline 的 SplineMeshComponent×N)。真源：${FMODEL}/Views/Snooper/Models/SplineModel.cs
(CalcSliceTransform 每顶点形变)、${CUE}/CUE4Parse/UE4/Assets/Exports/Component/SplineMesh/USplineMeshComponent.cs(SplineParams/ForwardAxis/
SplineUpDir/Boundary)、FSplineMeshParams.cs(SplineEvalPos/SplineEvalTangent/SplineEvalDir + Start/End Pos/Tangent/Scale/Roll/Offset)、
UE 5.7.4 兜底 ${UE}/Engine/Classes/Components/SplineMeshComponent.h(USplineMeshComponent::CalcSliceTransform 原版)。
实现：component as USplineMeshComponent -> StaticMesh -> 每顶点按 SplineParams 沿样条形变(位置/法线/切线都按 slice 变换旋转)，
ForwardAxis 决定参数化轴；**1:1 抄 CalcSliceTransform 的 Hermite 求值+slice 基构造**，禁简化成直网格；形变在 component 局部空间做再乘组件世界变换。
OverrideMaterials 带上。自测验收：含 spline 的 cell 日志确认导出非跳过；.glb 里 river/rope 弯曲。`,
  },
  {
    key: 'Light', own: 'LightComponentExporter.cs',
    task: `1:1 导出**全部灯光类型**到 glTF(Point/Spot/Directional 用 KHR_lights_punctual；**SkyLight/RectLight 也不许跳过**——
RectLight 用 area->point/spot 近似+把完整参数塞 node.extras，SkyLight 把 cubemap/SourceType/参数走 extras+无损JSON，二者都 log 依据)。
真源：${FMODEL}/Views/Snooper/Renderer.cs(WorldLight:513-531/664-673)、${CUE}/CUE4Parse/UE4/Assets/Exports/Component/Lights/ULightComponent.cs+
LightUtils.cs(Intensity/LightColor/AttenuationRadius/SourceRadius/Inner-OuterConeAngle/IntensityUnits)、UE 5.7.4 ${UE}/Engine/Classes/Components/
PointLightComponent.h,SpotLightComponent.h,DirectionalLightComponent.h,RectLightComponent.h,SkyLightComponent.h,LocalLightComponent.h。
实现：建 SharpGLTF LightBuilder(Point/Spot/Directional)：颜色=LightColor(注意 sRGB/线性)；强度按 UE 单位->glTF(point/spot=candela,
directional=lux)换算**公式抄 UE 源/注出处**；Spot 内外锥角(弧度)；AttenuationRadius->range；经 GlbSceneContext.AddLight(builder,
SceneTransform.NodeMatrix(placed.WorldTransform))，朝向 glTF -Z vs UE +X 在节点矩阵转对(注依据)。**每个灯的完整属性也要进无损层**(由 CompleteSceneData cell 兜，
但你确保灯组件不被漏当成无渲染)。自测验收：census Light=153；.glb 含 "KHR_lights_punctual"，灯数接近实际可表达数(其余进 extras/JSON)。`,
  },
  {
    key: 'Landscape', own: 'LandscapeComponentExporter.cs',
    task: `1:1 地形(Landscape×1 + LandscapeStreamingProxy×16，每 proxy 16 component)。真源：${CONV}/Landscape/LandscapeExporter.cs
(ALandscapeProxy.TryConvert(components, ELandscapeExportFlags.Mesh, out lod, out heightMaps, out weightMaps)->CStaticMesh->new Gltf(...))、
${CONV}/Landscape/LandscapeDataAccess.cs、${CUE}/CUE4Parse/UE4/Assets/Exports/Component/Landscape/ULandscapeComponent.cs、Exports/Actor/ALandscape.cs
(LandscapeComponents/LandscapeMaterial/LandscapeSectionOffset/LandscapeGuid)、UE 5.7.4 ${UE}/Landscape/。
实现：component as ALandscapeProxy -> LandscapeComponents -> TryConvert(...,Mesh,...) 得 CStaticMesh -> lod.LODs.First() 经
**与 StaticMesh 一致的 Gltf.ExportStaticMeshSections 路径**放进场景，节点变换=proxy 世界变换(RootComponent+LandscapeSectionOffset,
单位/缩放抄 LandscapeDataAccess)；LandscapeMaterial 带上；heightMaps/weightMaps 落 sidecar(无损)。**禁简化成平面/低模**。
自测验收：census Landscape=17；.glb 含地形网格(三角数显著)；ls 出 heightmap/weightmap。`,
  },
  {
    key: 'Camera', own: 'CameraComponentExporter.cs',
    task: `把相机导成 glTF 相机(CineCameraActor×5；CameraComponent/CineCameraComponent)。真源：${CUE}/CUE4Parse/UE4/Assets/Exports/
(搜 CameraComponent/CineCamera 相关类与字段:FieldOfView/AspectRatio/ProjectionMode/Ortho/Filmback/FocalLength/CurrentFocalLength)、
UE 5.7.4 ${UE}/Engine/Classes/Components/CameraComponent.h、${UE}/CinematicCamera/Public/CineCameraComponent.h(Filmback/Lens/FocalLength->FOV 换算)。
实现：component as 相机组件 -> SharpGLTF CameraBuilder(Perspective:VFOV/aspect/znear/zfar 或 Orthographic)，CineCamera 用 Filmback+FocalLength
算 FOV(**公式抄 UE 源/注出处**)，经 GlbSceneContext.AddCamera(builder, SceneTransform.NodeMatrix(placed.WorldTransform))，朝向 glTF -Z vs UE +X 转对。
完整相机参数进 node.extras+无损JSON。自测验收：.glb 二进制含 "cameras"；相机数=5。`,
  },
  {
    key: 'CompleteSceneData', own: 'CompleteSceneDataExporter.cs',
    task: `**核心"每个字节"无损层**：对场景**每一个 actor + 每一个 component**写完整属性树 JSON，**一个字段不丢**——尤其 FModel 全部
忽略的非渲染类型(NiagaraActor/NiagaraComponent、PostProcessVolume、ExponentialHeightFog、SkyAtmosphere、SphereReflectionCapture、
LevelSequenceActor、RuntimeVirtualTextureVolume、WorldDataLayers、CullDistanceVolume、PlayerStart、SkyLight、所有 *Component)。
真源/真值：FModel 无损序列化路径 = \`JsonConvert.SerializeObject(obj, Formatting.Indented)\`（见 ${FMODEL}/ViewModels/CUE4ParseViewModel.cs
的 Save-Properties；CUE4Parse 对每个 UObject 的属性反序列化即 uasset 真值，序列化回 JSON 逐字段无损）。对每个类型的字段含义/完整性用
UE 5.7.4 源核对（${UE}/Niagara/、Engine/Classes/Engine/{ExponentialHeightFog,PostProcessVolume,SkyLight,...}.h、
Engine/Classes/Components/SkyAtmosphereComponent.h、${UE}/MovieSceneTracks|LevelSequence/ 等）确认没有被 CUE4Parse 漏读的子结构(若有,记录)。
实现 CompleteSceneDataExporter.ExportAll(actors, dir, manifest)：遍历**所有**收集到的 actor(含 collector 的 embedded/cell/external 全部)，
对每个 actor 写 \`Actors/<World>/<ActorName>.json\` = 该 actor 完整 JSON(含其全部 component，递归 component 的完整属性)；
并把关键摘要(ExportType/世界变换/component 列表/引用的 asset PathName 集)写进 manifest，作为闭包层的引用种子。
**正交于渲染**：不管 actor 渲不渲染，全都无损导出。0-GC 不强求(IO 主导)，但全核并行写(每 actor 一文件,Parallel.ForEach,线程安全注意 manifest 聚合用并发结构)。
自测验收：Actors/ 下 JSON 文件数 ≈ 2537(每 actor 一个)；抽查 NiagaraActor/PostProcessVolume/ExponentialHeightFog 的 JSON 字段完整(对 UE 源点名几个关键字段在)。`,
  },
  {
    key: 'DependencyClosure', own: 'DependencyClosureExporter.cs',
    task: `**核心"每个字节"闭包层**：umap 递归引用的**每一个 asset** 无损导出，一个引用不漏。真值/API：CUE4Parse
\`IoPackage.ImportedPackages\`(Lazy<IoPackage?[]> 直接导入包) + 遍历每个 export 的属性树里 FPackageIndex/FSoftObjectPath/FSoftObjectPath[]
解析出的引用包，**BFS 传递闭包**(visited 去重)。对每个闭包包：用 provider.LoadPackage 载入，对其每个 export 按类型无损导出到 Assets/<包路径>/：
- 网格(UStaticMesh/USkeletalMesh) -> .glb(走 CUE4Parse Gltf/对应 Exporter) + 完整属性 .json
- 贴图(UTexture2D…) -> 解码图(全 mip,复用材质 cell 的健壮解码思路:串行+去重) + .json
- 材质(UMaterialInterface) -> 参数 .json
- Niagara/Curve/Sound/DataAsset/AnimSequence/其它 -> 完整属性 .json(+能解的二进制)
全部走 \`JsonConvert.SerializeObject\` 无损 + 已有 CUE4Parse-Conversion exporter(若该类型有)。健壮:逐包/逐 export try/catch 续跑+log；
按包 PathName 全局去重(一个 asset 只导一次)；写进 manifest(assets 计数 + 每个 asset 的源包/导出文件)。
真源参考：${CUE}/CUE4Parse/UE4/Assets/IoPackage.cs(ImportedPackages/ImportMap)、AbstractUePackage.cs、${CONV}/(各类型 Exporter)。
实现 DependencyClosureExporter.ExportClosure(rootPackage, provider, dir, manifest)。
自测验收：Assets/ 下资产数与"从 umap BFS 出的引用包数"一致(对账,dropped=0)；抽查 OniValley 的某材质引用的贴图确实在 Assets/ 里。`,
  },
]

const cells = await parallel(CELLS.map(c => () =>
  agent(`${COMMON}

${FOUND_CONTRACT}

【蜂窝单元=${c.key}】你**只 owns**：${c.own}。绝不改其它文件（多 agent 并行，互改=clobber）。
${c.task}

§B：先 Read 上面每个真源逐行核再写；同义替换不算偏离；缺平台能力开 flag 别降级；**禁占位/"先能跑"/跳过/简化**（违最高铁令）。
§D：能并行就全核并行；热路径 0-GC/span。代码注释英文、一文件一单元、禁缩写。
⚠**本阶段 EDIT-ONLY**：只实现你 owns 的文件。**绝不执行 \`dotnet build\`，绝不跑 CLI**（多 cell 并行会撞 obj/bin）。
把你 task 的"自测验收"当**验收标准**(必须满足)，但**不要真运行**；构建+运行由后续串行 gate 统一做。务必让文件**自洽可编译**(签名匹配地基契约、using 齐全)。
返回 {cell:'${c.key}', filesOwned[], implemented, completenessSelfReview(诚实自评:是否真做到"零丢失/每字节",哪些边角可能漏),
acceptanceVsTask(逐条验收->怎么满足), sourceCitations(每处 源=file:line), deviations(偏离真源处+理由；理想"无")}。`,
    { label: `cell:${c.key}`, phase: 'Cells', model: 'opus', schema: CELL_SCHEMA })
)).then(r => r.filter(Boolean))
log(`Cells 完成 ${cells.length}/7：${cells.map(c => `${c?.cell}=${c?.implemented ? 'impl' : 'INCOMPLETE'}`).join(' ')}`)

// ============ Phase 2.5：Compile-gate（串行，单 agent：唯一 build 点）============
phase('Compile-gate')
const gate = await agent(
  `${COMMON}

【任务=Compile-gate（串行唯一构建点）】foundation + 7 个并行 cell(EDIT-ONLY)已落地(材质/样条/灯光/地形/相机/无损场景数据/依赖闭包)。
第一次编译整套并修通：
1. \`${BUILD}\` -> 把所有 error 修到 **0 error**（缺 using/签名不符地基契约/类型不匹配/SharpGLTF/CUE4Parse API 用错）。
   只动编译层面，尊重各 cell 算法；逻辑疑点记录留 Verify，别擅自重写。缺引用就在 ${ROOT}/Source/Ruri.FModelHook 的 csproj 或 AssetRipperRefs.props 加 HintPath。
2. \`${KILL}\`；census 冒烟：env RURI_GLB_CENSUS_ONLY=1 \`${EXE} ${BASEARGS} --map "MainGrid_L8_X0_Y0_DL0.umap" --with-materials\` -> exit 0。
3. 小 cell 真跑(全包)：\`${KILL}\` 后 \`${EXE} ${BASEARGS} --map "MainGrid_L8_X0_Y0_DL0.umap" --with-materials\` -> exit 0；
   确认产出 .glb + Actors/*.json + Assets/ + scene-manifest.json，各分发路径在真数据上不崩。
返回 {buildClean, errorsFixed[file:line+改了啥], smokeExit, geomExit, packageProduced(.glb/Actors/Assets/manifest 是否都出), suspectedLogicIssues, notes}。`,
  { label: 'compile-gate', phase: 'Compile-gate', model: 'opus', schema: {
    type: 'object', additionalProperties: false,
    properties: {
      buildClean: { type: 'boolean' }, errorsFixed: { type: 'array', items: { type: 'string' } },
      smokeExit: { type: 'integer' }, geomExit: { type: 'integer' },
      packageProduced: { type: 'string' }, suspectedLogicIssues: { type: 'string' }, notes: { type: 'string' },
    },
    required: ['buildClean', 'errorsFixed', 'smokeExit', 'geomExit', 'packageProduced', 'suspectedLogicIssues', 'notes'],
  } })
log(`Compile-gate: buildClean=${gate?.buildClean} smokeExit=${gate?.smokeExit} geomExit=${gate?.geomExit}`)
if (!gate?.buildClean) {
  log('⛔ Compile-gate 未能编译干净 —— 中止（Verify 需读可编译代码）。人工介入后 resume。')
  return { aborted: 'compile-gate-failed', foundation, cells, gate }
}

// ============ Phase 3：Verify（8 路独立对抗验证，强制；§E）============
phase('Verify')
const VERIFY_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    target: { type: 'string' },
    verdict: { type: 'string', enum: ['clean', 'fixed', 'broken'] },
    realBugsFound: { type: 'integer' },
    bugs: { type: 'array', items: { type: 'string' } },
    fixesApplied: { type: 'array', items: { type: 'string' } },
    lineByLineCorrespondence: { type: 'string' },
    completenessAudit: { type: 'string' },
  },
  required: ['target', 'verdict', 'realBugsFound', 'bugs', 'fixesApplied', 'lineByLineCorrespondence', 'completenessAudit'],
}
const VERIFY_TARGETS = [
  { t: 'Foundation+StaticMesh', files: `${GLB}/WorldGlbExporter.cs, ComponentResolver.cs, StaticMeshComponentExporter.cs, GlbSceneContext.cs, PlacedComponent.cs, SceneTransform.cs, SceneManifest.cs`,
    src: `${FMODEL}/Views/Snooper/Renderer.cs(WorldMesh/ProcessMesh/CalculateTransform/OverrideVertexColors) + ${CONV}/Meshes/glTF/Gltf.cs`,
    inv: '网格共享 key 写侧vs读侧逐字节一致(含 override 集合)；ISM 每实例变换=Renderer.cs:549-555；BlueprintCreatedComponents 去重无漏无重；OverrideMaterials MaterialIndex 边界(<vs<=)；节点矩阵全程 SceneTransform.NodeMatrix；静态 324821 无丢；渲染层/无损层/闭包层都被 Export 调到。' },
  { t: 'Material', files: `${GLB}/GlbMaterialFactory.cs, MaterialTextureWriter.cs`,
    src: `${CONV}/Materials/MaterialExporter2.cs + CMaterialParams2 + ${CONV}/Meshes/glTF/Gltf.cs`,
    inv: '贴图 Decode 真串行化在全局锁下；按 PathName 去重；嵌入 PBR 材质名回绑 key 与 Gltf 产出 material.Name 逐字符一致(否则静默失效)；sidecar 无损(全 mip/格式/sRGB)；解码失败续跑不崩。' },
  { t: 'Spline', files: `${GLB}/SplineMeshComponentExporter.cs`,
    src: `${FMODEL}/Views/Snooper/Models/SplineModel.cs + USplineMeshComponent.cs + FSplineMeshParams.cs + ${UE}/Engine/Classes/Components/SplineMeshComponent.h`,
    inv: 'CalcSliceTransform 的 Hermite 逐行对 UE 源；ForwardAxis 参数化对；slice 基(切/上/副)构造与符号；法线/切线一并形变；Scale/Roll/Offset 插值；注册表 spline 在 static 前。' },
  { t: 'Light', files: `${GLB}/LightComponentExporter.cs`,
    src: `${FMODEL}/Views/Snooper/Renderer.cs(WorldLight) + ULightComponent.cs/LightUtils.cs + ${UE}/Engine/Classes/Components/PointLightComponent.h,SpotLightComponent.h,DirectionalLightComponent.h,RectLightComponent.h,SkyLightComponent.h`,
    inv: '强度单位换算有 UE 源出处非拍脑袋；颜色空间;Spot 锥角弧度;朝向 glTF -Z vs UE +X 转对;range;**SkyLight/RectLight 未被简单跳过**(走 extras/近似且完整参数留存);153 个灯无一漏(可表达进 punctual,不可表达进 extras+无损JSON)。' },
  { t: 'Landscape', files: `${GLB}/LandscapeComponentExporter.cs`,
    src: `${CONV}/Landscape/LandscapeExporter.cs + LandscapeDataAccess.cs + ALandscape.cs + ${UE}/Landscape/`,
    inv: 'TryConvert 调用与 LandscapeExporter.cs 一致；SectionOffset 单位/缩放对；几何走 Gltf.ExportStaticMeshSections；proxy 世界变换对；未简化成平面；16 proxy×16 component 全覆盖；heightmap/weightmap 无损。' },
  { t: 'Camera', files: `${GLB}/CameraComponentExporter.cs`,
    src: `${UE}/Engine/Classes/Components/CameraComponent.h + ${UE}/CinematicCamera/Public/CineCameraComponent.h + CUE4Parse 相机类`,
    inv: 'Filmback+FocalLength->FOV 换算有 UE 源出处；透视/正交分支；znear/zfar 合理；朝向 -Z vs +X 转对；5 个相机全出；完整参数留 extras/JSON。' },
  { t: 'CompleteSceneData', files: `${GLB}/CompleteSceneDataExporter.cs`,
    src: `${FMODEL}/ViewModels/CUE4ParseViewModel.cs(Save-Properties JSON) + UE 5.7.4 各非渲染类型头文件(Niagara/Fog/PostProcess/SkyAtmosphere/SkyLight/ReflectionCapture/LevelSequence/VirtualTexture/DataLayers)`,
    inv: '**每个 actor 都写 JSON(数≈2537,零漏)**;**每个 component 的完整属性递归在内**;JSON 真无损(JsonConvert 全字段,非摘要);非渲染类型(Niagara/Fog/PostProcess/Atmosphere/Sequence/Capture/VT/DataLayers/PlayerStart/SkyLight)逐一抽查字段完整;并发写线程安全(manifest 聚合无 race);引用 asset 种子喂给闭包层无漏。' },
  { t: 'DependencyClosure', files: `${GLB}/DependencyClosureExporter.cs`,
    src: `${CUE}/CUE4Parse/UE4/Assets/IoPackage.cs(ImportedPackages/ImportMap) + AbstractUePackage.cs + ${CONV}/(各 Exporter)`,
    inv: '**BFS 传递闭包**(ImportedPackages + 属性树 FPackageIndex/FSoftObjectPath 递归,visited 去重,无漏);每个闭包包的每个 export 无损导出;按 PathName 全局去重(不重不漏);贴图解码健壮(串行+去重);dropped=0(引用集==导出集);逐包 try/catch 续跑。' },
]
const verifications = await parallel(VERIFY_TARGETS.map(v => () =>
  agent(`${COMMON}

【对抗验证：${v.t}】你是独立顶级审计 agent。**预设这份移植/实现是错的且不完整**，带敌意逐行挑。**绝不盲信实现 agent 的"已 1:1/已完整"自报**
（本机经验：agent 端口必带偏离，含灾难级坐标/索引 key 错 + 完整性漏项）。
方法（§E 强制闭环）：① Read 真源(ground truth)：${v.src}；② Read 被审代码：${v.files}；③ 逐行结构 diff，产出**逐步 file:line 1:1 对应表**
或逐条列偏离；④ **就地 Edit 修每个真 bug**；⑤ **完整性审计(最高铁令)**：${v.inv}
这是对真源的结构验证 + 对"每个字节零丢失"的完整性验证。
⚠**EDIT-ONLY**：8 个验证并行，**绝不执行 \`dotnet build\`/跑 CLI**（撞构建）。修复保持文件自洽可编译；只 Edit 你 target 的文件。
返回 {target:'${v.t}', verdict(clean/fixed/broken), realBugsFound, bugs[file:line+错在哪+为何], fixesApplied[],
lineByLineCorrespondence(被审file:line<->真源file:line), completenessAudit(逐条完整性不变量结论+有没有发现"会丢字节"的漏项及修复)}。`,
    { label: `verify:${v.t}`, phase: 'Verify', model: 'opus', schema: VERIFY_SCHEMA })
)).then(r => r.filter(Boolean))
const totalBugs = verifications.reduce((s, v) => s + (v?.realBugsFound || 0), 0)
log(`Verify 完成 ${verifications.length}/8；真 bug ${totalBugs} 处；${verifications.map(v => `${v?.target}=${v?.verdict}`).join(' ')}`)

// ============ Phase 4：Integration（OniValley 全量 + 每字节验收 + manifest 对账）============
phase('Integration')
const integration = await agent(
  `${COMMON}

【集成自测=闭环收口（每字节验收）】foundation + 7 cell + 8 验证都落地了。最终全量集成（你有权就地修任何编译/运行/集成/完整性错误，缺则深挖修复复跑）：
1. \`${BUILD}\` -> 0 error。 2. \`${KILL}\`。
3. **全量导出**：\`${EXE} ${BASEARGS} --map "Levels/Oni_Valley.umap" --with-materials\` -> exit 0，记录耗时。
4. **每字节验收（缺一即不合格）**：
   - 渲染层 .glb：part 存在；placements>=324821；含 "images"(嵌入PBR)、"KHR_lights_punctual"(灯)、"cameras"(相机)；地形/样条网格在。
   - 蓝图几何：torii/灯笼/蜡烛/巨石/古树 在产物里；OverrideMaterials 271 处不再跳过。
   - **无损层 Actors/**：JSON 文件数对账 census 的 actor 数(≈2537,零漏)；**逐一确认 FModel 忽略的类型都在且字段完整**：
     NiagaraActor/Component、PostProcessVolume、ExponentialHeightFog、SkyAtmosphere、SphereReflectionCapture、LevelSequenceActor、
     RuntimeVirtualTextureVolume、WorldDataLayers、CullDistanceVolume、PlayerStart、SkyLight、CineCamera。抽样 cat 几个 JSON 验字段无损。
   - **闭包层 Assets/**：从 umap BFS 出的引用包集 vs Assets/ 实际导出集对账，dropped=0；抽查材质->贴图链路在。
   - manifest：scene-manifest.json 的计数(actors/components/assets/dropped)自洽，dropped=0。
5. 读各 .glb 的 JSON chunk 统计 {meshes,materials(贴图通道),lights,cameras,nodes,triangles}，与 census 对账，列差异并修到 0。
6. grep 整个 ${GLB}：无遗留 TODO/"not yet implemented"/"future phase"/"skip"/"ignore"/占位 stub（Niagara 的"几何无法 glTF 表达,数据已走无损JSON+闭包"可作显式豁免说明，但数据必须在）。
返回 {buildClean, runExit, elapsedSec, renderLayer(glb 统计+对账), losslessLayer(Actors/ 数+FModel忽略类型逐项在/缺), closureLayer(Assets/ 数 vs 引用集 dropped),
familiesComplete{geometry,blueprint,overrideMaterials,materialsTextures,lights,cameras,landscape,spline,losslessAllActors,dependencyClosure}(各 bool),
everyByteVerdict(总判:是否真做到"每个字节零丢失"), gapsFoundAndFixed[], residualTodos(应为空或仅 Niagara 几何豁免), problems}。`,
  { label: 'integration:full-oni-valley', phase: 'Integration', model: 'opus', schema: {
    type: 'object', additionalProperties: false,
    properties: {
      buildClean: { type: 'boolean' }, runExit: { type: 'integer' }, elapsedSec: { type: 'number' },
      renderLayer: { type: 'string' }, losslessLayer: { type: 'string' }, closureLayer: { type: 'string' },
      familiesComplete: {
        type: 'object', additionalProperties: false,
        properties: {
          geometry: { type: 'boolean' }, blueprint: { type: 'boolean' }, overrideMaterials: { type: 'boolean' },
          materialsTextures: { type: 'boolean' }, lights: { type: 'boolean' }, cameras: { type: 'boolean' },
          landscape: { type: 'boolean' }, spline: { type: 'boolean' }, losslessAllActors: { type: 'boolean' }, dependencyClosure: { type: 'boolean' },
        },
        required: ['geometry', 'blueprint', 'overrideMaterials', 'materialsTextures', 'lights', 'cameras', 'landscape', 'spline', 'losslessAllActors', 'dependencyClosure'],
      },
      everyByteVerdict: { type: 'string' },
      gapsFoundAndFixed: { type: 'array', items: { type: 'string' } },
      residualTodos: { type: 'array', items: { type: 'string' } },
      problems: { type: 'string' },
    },
    required: ['buildClean', 'runExit', 'elapsedSec', 'renderLayer', 'losslessLayer', 'closureLayer', 'familiesComplete', 'everyByteVerdict', 'gapsFoundAndFixed', 'residualTodos', 'problems'],
  } })

return {
  foundation: { buildClean: foundation?.buildClean, staticParityHeld: foundation?.staticParityHeld, blueprintGeometryNowExported: foundation?.blueprintGeometryNowExported },
  cells: cells.map(c => ({ cell: c?.cell, implemented: c?.implemented })),
  compileGate: { buildClean: gate?.buildClean, smokeExit: gate?.smokeExit, geomExit: gate?.geomExit },
  verifyTotalBugsFixed: totalBugs,
  verifications: verifications.map(v => ({ target: v?.target, verdict: v?.verdict, bugs: v?.realBugsFound })),
  integration,
}
