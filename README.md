# Ruri-RipperHook

一个面向跨引擎资产管线的 AOP Hook 框架。在不修改上游源码的前提下,扩展 AssetRipper、FModel 等 Unity / UE 数据处理工具链的读取、转换与导出能力,把来源各异的资产——不同引擎版本、不同项目自定义的容器与序列化格式——收敛成统一的 Unity 数据表示(YAML / Unity 对象语义)。Shader 链路是这条转换通路当前最明确的先行落点。

## 下游:面向 Blender 的技术美术管线

本仓库产出的标准 Unity YAML,是 [RuriRipperImporter](https://github.com/ShiyumeMeguri/RuriRipperImporter) Blender 插件的直接输入——该插件零依赖解析 Unity 原生 `Force Text` 序列化文本,把网格、真实骨架、材质与动画 clip 原样重建进 Blender,不经过 FBX 转换、不重新绑骨、不丢顶点流。这条管线服务于角色建模、绑骨、MMD 风格动画等 3D 内容创作与技术美术研究工作流:Ruri-RipperHook 负责让来源各异的资产最终都能落成同一份干净的 Unity 项目结构,Blender 插件只需要认识"标准 Unity YAML"这一种形态。

## 架构定位

Ruri-RipperHook 是一个独立于上游工具的 AOP 扩展层——上游代码(AssetRipper、FModel)作为只读 submodule 冻结,差异化行为一律通过运行时方法 Hook 注入,而不是直接修改或 fork 上游。核心是一套基于 attribute 的 Hook 注册框架(`Ruri.Hook`),覆盖方法重定向、构造接管、IL 重写、引用计数与生命周期管理;在此之上,是一条把 FModel / UE 等异源工具链产出向 Unity 数据模型靠拢的转换通路。

这种拆分的工程意义:上游升级时不需要 rebase 补丁集,新增数据来源不需要 fork 新工具,跨工具链共通的导出 / shader / 网格处理逻辑只在 Ruri 侧实现一次。

## 工作机制

所有扩展都是从一个继承 `RipperHookCommon` 的类开始的,通过 `[RipperHook(...)]` 标注其适用范围,内部成员通过 attribute 描述要 hook 的目标方法:

```csharp
[RipperHook(...)]
public class MyHook : RipperHookCommon
{
    [RetargetMethod(typeof(TargetClass), nameof(TargetClass.TargetMethod),
                    isBefore: true, isReturn: false)]
    static void Patch(TargetClass self, /* original args */)
    {
        // prefix-continue logic
    }

    [RetargetMethodCtorFunc(typeof(SomeSettings))]
    static bool MutateDefaults(ILContext il) { /* IL rewrite */ return true; }
}
```

启动期 `Bootstrap.ApplyHooks(config)` 会发现所有匹配当前配置的 Hook 类,调用其 `Initialize()`,完成 attribute 扫描与方法重定向注册。整个生命周期由 `Ruri.Hook` 维护,业务代码不直接持有 MonoMod 句柄。

详细的 attribute 语义、AssetRipper 内部数据流、路径处理、自定义 processor 注入等,见 [FRAMEWORK.md](FRAMEWORK.md)。

## 仓库结构

| 路径 | 职责 |
|---|---|
| `Source/Ruri.Hook` | AOP 框架本体。Attribute 定义、Hook 注册、方法重定向、反射工具 |
| `Source/Ruri.RipperHook` | AssetRipper 主增强层。读取、导出、网格拆分、prefab 处理、路径修复等通用 Hook |
| `Source/Ruri.RipperHook.GUI` | 图形界面入口(AssetRipper 流程) |
| `Source/Ruri.RipperHook.CLI` | 命令行入口,headless 导出与 Hook 列表查询 |
| `Source/Ruri.FModelHook` | FModel / UE 侧增强,shader bytecode 导出与统一 metadata 整理 |
| `Source/Ruri.FModelHook.GUI` | FModel 流程的图形界面入口 |
| `Source/Ruri.ShaderDecompiler` | DX11 shader 反编译实现 |
| `Source/Ruri.AssemblyDumper` | AssetRipper 结构定义与 Read 代码的生成链路 |
| `Source/Ruri.SourceGenerated` | AssemblyDumper 产物,以 `HintPath` 形式被 Ruri.RipperHook 引用 |
| `AssetRipper/`, `FModel/`, `Tpk/` | 上游 submodule,**只读冻结**,差异通过 Hook 注入而非直接修改 |

## 主要能力

- **运行时 AOP**:Attribute 驱动的方法重定向,支持前置 / 后置 / 完全替换 / 构造拦截 / 完整 IL 重写
- **导出流程接管**:可插入自定义 `IAssetProcessor`、覆写 `ExportCollection` 的产物路径与命名、跳过冗余处理阶段
- **结构与格式适配**:对 Unity Bundle / SerializedFile / VFS / 流式资源等的解析点提供 Hook 切入位
- **Shader 链路**:metadata 补全、绑定信息组织、bytecode 反编译,以及朝 Unity shader 表示形态的转换
- **跨工具链统一**:FModel / UE 侧的数据按需转换为 Unity YAML 或等价对象语义,复用 AssetRipper 后续工作流
- **AssemblyDumper 集成**:从 metadata 生成结构定义,产物可被 AR 流程作为类型来源
- **GUI / CLI 双入口**:同一份 Hook 实现,GUI 用于交互式调试,CLI 用于批处理 / 脚本化导出 / 列表查询

## 适用场景

- 为 Blender / MMD 等 3D 内容创作与技术美术研究流程准备干净、可直接导入的 Unity 项目资产(网格、材质、骨架、动画)
- 给已有数据管线工具链补齐读取或导出短板,而不想直接 fork
- 把零散的导出流程修补整理为可组合、可复用的模块
- 在多个工具之间共用一套 shader / 网格 / prefab 处理逻辑
- 需要在上游频繁升级的同时维护稳定的差异化行为

## 使用说明

| 操作 | 命令 / 路径 |
|---|---|
| 编译单个 Ruri 项目 | `dotnet build Source/Ruri.RipperHook/Ruri.RipperHook.csproj -c Debug --nologo` |
| GUI 入口 | `Ruri.RipperHook.GUI.exe` |
| CLI 入口 | `Ruri.RipperHook.CLI.exe` |
| 列出可用 Hook | `Ruri.RipperHook.CLI.exe --list-hooks` |
| Headless 导出 | `Ruri.RipperHook.CLI.exe --hook <Id> --load <path> --export <dir>` |

**不要直接 build 整个 slnx** —— `Ruri.SourceGenerated` 是 `HintPath` 引用的预编译 DLL,只由 `Ruri.AssemblyDumper` pipeline 重新生成,误 build 会消耗大量时间。

Hot Reload 状态下偶发的 Hook 失效一般来自增量残留,重新触发编译可恢复。

## Todo

- 把 UE / FModel 侧的 Prefab / Model / Material / Shader 等主要数据转换为 Unity YAML 数据导出
- 进一步缩小 `Ruri.AssemblyDumper` 生成的 DLL 体积 —— 当前生成内容超出实际所需,只需要类型定义与 Read 路径(目标 < 1 MB)
- `Ruri.AssemblyDumper` 的生成工作流简化
- 实现cpp2il hook 使其导出汇编代码体

## Special Thanks

- **ds5678** — AssetRipper original author
- **AnimeStudio**
- **nesrak1** — USCSandbox author
- **Razmoth**
