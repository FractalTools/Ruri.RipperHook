// UE -> Unity 导出修正 workflow（ruri-engineering-discipline §E 闭环：foundation -> 并行实现 -> 逐个对抗验证 -> 集成自测）
// 核心修正：导出必须走 AssetRipper 真·工程导出管线(ProjectExporter/ExportHandler)，让 AR 自己把 Texture2D 解码成 PNG、
// 按类型路由(.mat/.prefab/.anim/...)。我们只是"数据回填者"：把 UE 数据 + 全部依赖引用完美回填进 AR GameBundle 即可。
// 现状 bug：UnityYamlExportSession.ExportAll 用 DefaultYamlExporter 逐资产写 -> 产出 AR 中间 .texture2D YAML 而非 PNG。
// 用法：Workflow scriptPath 执行本文件。全程 opus；验证 agent 独立、预设代码错、读 AR reader/exporter 真源逐行核、就地修。

export const meta = {
  name: 'unity-export-png-fix',
  description: '把 UE->Unity 导出从 DefaultYamlExporter 改走 AR 真·ProjectExporter(texture->PNG/类型路由) + 并行对抗审计每类资产的数据&依赖回填完整性 + 集成自测验证 PNG 产出',
  phases: [
    { title: '导出管线重写', detail: 'ExportAll 改用 AR ProjectExporter/ExportHandler，texture->PNG，依赖图交给 AR', model: 'opus' },
    { title: '回填完整性对抗审计', detail: '每类资产独立 opus 审计：数据+全部 UE 引用是否完美回填进 AR(对 AR reader 逐字段核)，就地修', model: 'opus' },
    { title: '集成自测', detail: 'build CLI + 跑 OniValley + 验证 .png 贴图 + 资产清册 + 跨引用解析', model: 'opus' },
  ],
}

const ROOT = 'D:/Ruri/GitHub/Ruri-RipperHook'
const UE = `${ROOT}/Source/Ruri.FModelHook/Game/SBUE/UnityExport`
const AR = `${ROOT}/AssetRipper/Source`
const AREXP = `${AR}/AssetRipper.Export.UnityProjects`
const SKILL = 'D:/Tools/Users/Administrator/.claude/skills/ruri-engineering-discipline/SKILL.md'
const EXE = `${ROOT}/FModel/FModel/bin/Debug/net8.0-windows/win-x64/Ruri.FModelHook.CLI.exe`
const TESTARGS = `--export-unity --game-dir "D:/Game/OniValleyDemo5.1/Oni_Valley_VFX/Content/Paks" --ue-version GAME_UE5_1 --mappings "D:/Game/OniValleyDemo5.1/Mappings.usmap" --export-out "${ROOT}/TestLoopOutput"`

const COMMON = `项目：Ruri-RipperHook 的 FModelHook UE->Unity 导出子系统。先读 ${SKILL}（§B 1:1 忠实移植 + §D 黑洞性能 + §E 闭环 + 代码=英文/一文件一单元/禁缩写）。
【架构铁律】这是"牛头蛇尾"：CUE4Parse 读 UE 二进制(牛头) -> Ruri Mapper 回填数据(蛇身,我们写) -> AssetRipper 导出(蛇尾,AR 自己干)。**我们只是数据回填者**：把 UE 的数据 + 全部依赖引用(PPtr)完美回填进 AR 的 source-generated Unity 对象，剩下交给 AR。绝不自己写导出/编码/序列化。
【落点】全部代码在 ${UE}/(Engine/ Mappings/ Hook/ UnityYamlExportRunner.cs)，命名空间 Ruri.FModelHook.Game.SBUE.UnityExport.*。
【依赖约束】AssetRipper 是冻结区，**绝不改 ${AR} 下任何文件**，绝不新建 assembly。AR 引用走预构建 DLL 闭包(${ROOT}/Source/AssetRipperRefs.props，HintPath 到 0Bins/AssetRipper/<cfg>/)——因为 AR 的源生成器 pin Roslyn 5.3 而 CLI SDK 只有 5.0，无法命令行重编译冻结树。需要新 AR 程序集引用时只在 AssetRipperRefs.props 加 HintPath。
【类型模型】用 NuGet AssetRipper.SourceGenerated 1.3.14.2(命名空间 AssetRipper.SourceGenerated.*)，不是 Ruri.SourceGenerated.dll。Mesh(43)是 sealed group 字段无 _C43 后缀(VertexData/IndexBuffer/SubMeshes/Name…裸名)；Texture2D(_C28)/Material(_C21)有后缀。set PPtr 用 pptr.SetAsset(collection, asset)；AssetDictionary 用 AddNew()。
【构建/自测】仅构建 CLI：\`dotnet build Source/Ruri.FModelHook.CLI/Ruri.FModelHook.CLI.csproj -c Debug --nologo\`(会带 FModelHook;绝不构建 slnx/Ruri.SourceGenerated)。自测：\`${EXE} ${TESTARGS} --max-packages 40\`。输出在 ${ROOT}/TestLoopOutput(每次清空)。`

// ============ Phase 1：导出管线重写（foundation，必须先成）============
phase('导出管线重写')
const foundation = await agent(
  `${COMMON}

【任务=导出管线重写(foundation)】当前 BUG：${UE}/Engine/UnityYamlExportSession.cs 的 ExportAll() 用 DefaultYamlExporter + AssetExportCollection 逐资产写出 -> 贴图变成 AR 中间格式 .texture2D(裸 YAML) 而非 **PNG**。用户要求：把回填好的 GameBundle 交给 **AR 的真·工程导出管线**，让 AR 自己 texture->PNG、按类型路由(.mat/.prefab/.anim)、按它自己的 ProjectAssetContainer 解析我们回填的依赖图。

必读真源(逐个 Read,搞清真实 API 签名)：
- ${AREXP}/ExportHandler.cs（真导出入口；看 Export(GameData/GameBundle, path, fileSystem) 之类签名 + 它怎么 new + Process 流程）
- ${AREXP}/ProjectExporter.cs + ProjectExporter.Overrides.cs（per-type exporter 注册：TextureExportCollection 等;看默认 exporter 列表 + Export 方法签名）
- ${AREXP}/Textures/TextureAssetExporter.cs + Textures/TextureExportCollection.cs（确认 Texture2D 走这条 -> .png，需要哪些字段:Format/ImageData/Width/Height/CompleteImageSize/ColorSpace 等）
- ${AREXP}/ProjectAssetContainer.cs、ExportHandler 用的 GameData/GameBundle 类型
- 现有：${UE}/Engine/UnityYamlExportSession.cs、MinimalExportContainer.cs、UnityYamlExportRunner.cs

实现要求：
1. ExportAll 改为：用 AR 的 ExportHandler/ProjectExporter 对 _bundle(GameBundle) 跑完整导出到 projectDirectory。优先复用 AR 默认 exporter 集合(含 texture->PNG)。若 ExportHandler 需要 GameData/Library 配置,构造最小可用配置。
2. 若 AR 的 ProjectExporter 自带容器/GUID 解析,**删掉自定义 MinimalExportContainer 的导出用途**(改由 AR 管),但保留我们回填阶段建立的 collection + PPtr。我们回填的 cross-ref(material->texture/prefab->mesh) 必须被 AR 的导出容器识别成正确的 {fileID,guid,type}。
3. 不破坏 UnityYamlExportRunner/CLI 接口(ConvertAndExport 签名尽量不变)。
4. **务必构建通过**：\`dotnet build ${ROOT}/Source/Ruri.FModelHook.CLI/Ruri.FModelHook.CLI.csproj -c Debug --nologo -v minimal\`，把所有报错修到 0 error(缺 AR 程序集就在 AssetRipperRefs.props 加 HintPath)。
5. 跑一次自测 \`${EXE} ${TESTARGS} --max-packages 15\`，确认 exit 0 且 TestLoopOutput 下贴图变成 **.png**(不是 .texture2D)。贴 ls 证据。

返回 {approach:用了哪个AR入口+签名, filesChanged[], buildClean:bool, pngConfirmed:bool, evidence:自测输出关键行, remainingIssues}。`,
  { label: 'foundation:export-rewrite', phase: '导出管线重写', model: 'opus', schema: {
    type: 'object', additionalProperties: false,
    properties: {
      approach: { type: 'string' }, filesChanged: { type: 'array', items: { type: 'string' } },
      buildClean: { type: 'boolean' }, pngConfirmed: { type: 'boolean' },
      evidence: { type: 'string' }, remainingIssues: { type: 'string' },
    }, required: ['approach', 'filesChanged', 'buildClean', 'pngConfirmed', 'evidence', 'remainingIssues'] } })
log(`导出重写完成: buildClean=${foundation?.buildClean} pngConfirmed=${foundation?.pngConfirmed}`)

// ============ Phase 2：每类资产 回填完整性 对抗审计 + 就地修（并行）============
phase('回填完整性对抗审计')
const AUDIT_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    cell: { type: 'string' }, verdict: { type: 'string', enum: ['clean', 'fixed'] },
    gapsFound: { type: 'integer' }, fixesApplied: { type: 'array', items: { type: 'string' } },
    refsBackfilled: { type: 'string' }, correspondence: { type: 'string' },
  }, required: ['cell', 'verdict', 'gapsFound', 'fixesApplied', 'refsBackfilled', 'correspondence'] }

const CELLS = [
  { key: 'Texture', map: `${UE}/Mappings/TextureMappings.cs`, arReader: `${AREXP}/Textures/TextureAssetExporter.cs + ${AR}/AssetRipper.SourceGenerated.Extensions/Texture2DExtensions.cs`,
    inv: 'AR 解码 PNG 需要的全部字段都回填了吗:Format_C28E/ImageData_C28(全 mip)/Width/Height/CompleteImageSize/MipCount/ColorSpace(sRGB)/TextureSettings(wrap/filter from AddressX/Y)。CompleteImageSize=0 会不会让 PNG 解码失败?BC7/ASTC 等格式 EnumMaps.Pixel 是否覆盖 OniValley 实际格式。' },
  { key: 'Material', map: `${UE}/Mappings/MaterialMappings.cs`, arReader: `${AR}/AssetRipper.SourceGenerated.Extensions/MaterialExtensions.cs + ${AR}/AssetRipper.SourceGenerated.Extensions/UnityPropertySheetExtensions.cs`,
    inv: 'GetParams(AllLayers) 的 Textures/Colors/Scalars/Switches 是否全量进 m_TexEnvs/m_Floats/m_Colors;texture PPtr 是否 SetAsset 成功解析;Shader PPtr 现在是 null({fileID:0})——UE 依赖等价传入要求至少把材质引用的贴图全回填(已做?);UMaterialInstanceConstant 的 parent 链参数是否合并。' },
  { key: 'StaticMesh', map: `${UE}/Mappings/StaticMeshMappings.cs + ${UE}/Engine/VertexPacker.cs + ${UE}/Engine/MeshDataFactory.cs`, arReader: `${AR}/AssetRipper.SourceGenerated.Extensions/VertexDataBlob.cs(reader 部分) + MeshData.cs`,
    inv: 'VertexPacker flush 出的 VertexData 能被 AR VertexDataBlob.ReadData 逐字节读回吗(channel/stream/offset/stride/currentChannels 全对?);submesh FirstByte/index/bounds;UV 1-V 翻转;法线/切线 W 符号;顶点色;多 UV 通道。AR 导出 mesh 时(可能转 glTF/FBX 或 Unity mesh)需要的字段是否齐。' },
  { key: 'SkeletalMesh', map: `${UE}/Mappings/SkeletalMeshMappings.cs`, arReader: `${AR}/AssetRipper.SourceGenerated.Extensions/MeshExtensions.cs(FillWithCompressedMeshData/CompressedMesh)`,
    inv: '骨权重 SetWeights/bindpose/BoneNameHashes(CRC32)/morph->BlendShape 是否全回填;FillWithCompressedMeshData 的 CompressedMesh 路径 AR 导出时能正确读回吗;Skin 的 boneIndex 映射(section.BoneMap->全局骨索引)是否正确;材质引用是否回填。' },
  { key: 'Animation', map: `${UE}/Mappings/AnimationMappings.cs`, arReader: `${AR}/AssetRipper.Processing/AnimationClips/* + ${AR}/AssetRipper.SourceGenerated.Extensions(AnimationClip 相关)`,
    inv: 'legacy 曲线 m_RotationCurves/m_PositionCurves/m_ScaleCurves 的 path(骨骼层级路径)/keyframe(time/value/inTangent/outTangent)/clip 设置(m_SampleRate/m_Legacy/m_WrapMode);ACL 已由 CUE4Parse 解;曲线绑定的 bone path 是否与 mesh 骨名一致(否则动画绑不上)。' },
  { key: 'World', map: `${UE}/Mappings/WorldMappings.cs`, arReader: `${AR}/AssetRipper.Processing/Prefabs/* + ${AREXP}/(prefab/scene 导出)`,
    inv: 'UWorld->prefab:GameObject/Transform 层级(父子/AttachParent)/MeshFilter.mesh PPtr/MeshRenderer.materials PPtr 是否全回填解析;actor 的 component 是否漏(光/碰撞/相机);World Partition cell/streaming/OFPA actor 是否聚合(参考 ${ROOT}/Source/Ruri.FModelHook/Game/SBUE/GlbSceneExport/WorldActorCollector.cs);transform 坐标。AR 导出 prefab 需要的字段。' },
]
const audits = await parallel(CELLS.map(c => () =>
  agent(`${COMMON}

【蜂窝单元=回填完整性对抗审计: ${c.key}】**假设这个回填是不完整/错的**，带敌意找出"UE 数据/依赖没完美回填进 AR"的每一处缺口并**就地 Edit 修复**。你是独立审计 agent，绝不盲信"已实现"自报。
现在导出已改走 AR 真·ProjectExporter(foundation 已重写),所以 AR 的 exporter 拿到什么字段就导出什么——**回填不全 = 导出残缺**。
待审回填代码：${c.map}
AR 端真实读取/导出逻辑(逐行核 AR 到底需要哪些字段)：${c.arReader}
关键不变量/必查项：${c.inv}
方法：① Read 待审 mapping + 对应 CUE4Parse UE 源(${ROOT}/FModel/CUE4Parse) 确认数据来源齐全;② Read AR reader/exporter 真源,逐字段列出 AR 导出该类型**实际读取**的字段,对照我们**是否回填**;③ 每个 UE 引用(贴图/材质/mesh/骨骼)必须变成 AR PPtr 且能解析(SetAsset + 在同一 collection);④ 缺口就地 Edit 补全;⑤ 不臆造字段名,拿真 struct 核。
注意 §B：忠实、不降级、不写占位;§D：热路径 0-GC/span。代码注释英文。
返回 {cell, verdict(clean/fixed), gapsFound, fixesApplied[每条 file:line+改了什么], refsBackfilled(该类型所有 UE 引用如何变成 AR PPtr 的说明), correspondence(AR 需要字段 vs 我们回填字段 对照表)}。`,
    { label: `audit:${c.key}`, phase: '回填完整性对抗审计', model: 'opus', schema: AUDIT_SCHEMA })
)).then(r => r.filter(Boolean))
log(`回填审计完成 ${audits.length} 单元；查出缺口 ${audits.reduce((s, a) => s + (a?.gapsFound || 0), 0)} 处`)

// ============ Phase 3：集成 + 真编译 + 自测 + 验证 PNG ============
phase('集成自测')
const integration = await agent(
  `${COMMON}

【任务=集成自测(闭环收口)】foundation 重写了导出管线,6 个并行 agent 修了各类回填缺口。现在做最终集成验证(你有权就地修任何编译/运行错误)：
1. 构建 CLI：\`dotnet build ${ROOT}/Source/Ruri.FModelHook.CLI/Ruri.FModelHook.CLI.csproj -c Debug --nologo -v minimal\`。有 error 就读报错就地修到 0 error(冲突常见于多 agent 改同文件;缺 AR 程序集加 AssetRipperRefs.props HintPath)。
2. 杀残留：\`Get-Process Ruri.FModelHook.CLI -EA SilentlyContinue | Stop-Process -Force\`(若锁 DLL)。
3. 自测：\`${EXE} ${TESTARGS} --max-packages 60\`，要 exit 0。
4. **验证 PNG**：\`ls ${ROOT}/TestLoopOutput\` 递归,确认贴图是 **.png**(+ .png.meta 带 TextureImporter)而非 .texture2D;材质 .mat 里 texture PPtr 指向 png 的 guid;mesh/anim/prefab 各就位。统计每类产物数量。
5. 若仍有 .texture2D 或贴图没 PNG,深挖 AR ProjectExporter 为何没走 TextureExportCollection(可能要在导出前 ExportHandler.Process 跑处理器,或 texture 字段没回填全),就地修并复测。
返回 {buildClean, runExit, pngConfirmed, census(每类文件数), sampleMatRefsPng:bool, problems, fixesApplied[]}。`,
  { label: 'integration:build-test-verify', phase: '集成自测', model: 'opus', schema: {
    type: 'object', additionalProperties: false,
    properties: {
      buildClean: { type: 'boolean' }, runExit: { type: 'integer' }, pngConfirmed: { type: 'boolean' },
      census: { type: 'string' }, sampleMatRefsPng: { type: 'boolean' },
      problems: { type: 'string' }, fixesApplied: { type: 'array', items: { type: 'string' } },
    }, required: ['buildClean', 'runExit', 'pngConfirmed', 'census', 'sampleMatRefsPng', 'problems', 'fixesApplied'] } })

return {
  foundation: { buildClean: foundation?.buildClean, pngConfirmed: foundation?.pngConfirmed },
  auditCells: audits.length,
  totalGapsFixed: audits.reduce((s, a) => s + (a?.gapsFound || 0), 0),
  integration,
}
