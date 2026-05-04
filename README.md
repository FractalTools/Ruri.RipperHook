# Ruri-RipperHook

Ruri-RipperHook 是一个通用式 AOP 数据处理与导出增强框架，用于为现有工具链补充更好的读取、导出、转换与 shader 处理能力。它通过运行时 Hook、方法重定向、读取流程接管和结构适配，在不重写上游解析器的前提下扩展既有工作流。

从当前代码结构看，这个仓库同时覆盖通用 Hook 基础设施、AssetRipper 侧增强、FModel / UE 侧增强、GUI / CLI 工作流和 AssemblyDumper 生成链路，并不是单一面向某一个上游工具的补丁集合。

当前的终极目标是把 Unity 数据作为统一的中间表示层：将 FModel / UE 侧的数据逐步转换为等价的 Unity YAML 或等价 Unity 数据结构，再传输给 AssetRipper 侧工作流继续处理。这样 Unity 侧现有的浏览、导出、组织和后处理能力就可以复用到更多来源的数据上。Shader 链路已经在朝这个方向推进，是当前最明确的先行落点之一。

## 项目定位

这是一个通用式 AOP 扩展框架，重点不在“自己重写一整套解析器”，而在“给现有工具链增加更好的读取、导出、转换和反编译能力”，并逐步把不同来源的数据统一收敛到同一套处理模型中。

它的工作方式大致是：
- 在现有程序或目标流程中注入 Hook。
- 通过 AOP 风格的切面拦截关键方法、构造、读取流程和导出流程。
- 在不大规模重写上游逻辑的前提下替换关键节点行为。
- 把特定数据格式、资源封装、序列化差异和 shader 处理需求适配到统一框架中。
- 把异构工具链输出逐步转换为统一的 Unity 数据表示，交给后续统一流程处理。

这意味着它既可以作为现有工具链的行为修正层，也可以作为统一数据流和高质量导出流程的承载层。

## 核心思路

Ruri-RipperHook 的设计核心是“通用切面 + 场景特化模块”：

- 通用层提供 Hook 注册、方法重定向、反射包装、运行时调度和可复用的数据处理管线。
- AssetRipper 侧模块负责读取、导出、shader 处理、静态网格拆分和流程裁剪等能力。
- FModel / UE 侧模块负责 shader bytecode 导出、统一 metadata 生成、进程内反编译，以及向 Unity 侧统一数据表示靠拢的前置转换工作。
- 具体项目层只描述真正有差异的部分，例如块结构、头部格式、流式资源、虚拟文件系统、类型结构差异或 shader 绑定信息。
- 版本层尽量只覆盖发生变化的节点，避免重复实现。

统一方向上，框架希望逐步形成这样的链路：

- 上游工具负责拿到原始数据和上下文。
- 中间层负责把不同来源的数据整理成统一的结构描述。
- 条件成熟时，将外部数据转换为等价的 Unity YAML 或等价 Unity 对象语义。
- 最终复用 AssetRipper 侧已有的浏览、导出、后处理和 shader 工作流。

这种拆分方式的好处是：
- 新项目或新数据链路接入成本更低。
- 同类格式处理逻辑、导出逻辑和 shader 管线可以在多个模块之间复用。
- 当上游工具升级后，只需要修正少数切面，而不是整体重写。
- 一次性的补丁逻辑可以沉淀为长期可维护的模块。

## 仓库结构

这个仓库主要由几部分组成：

- `Ruri.Hook`: 通用 Hook 基础设施，提供 `HookManager`、特性标记和运行时注册能力。
- `Ruri.RipperHook`: AssetRipper 增强层，覆盖读取、导出、shader、网格处理和统一数据落地后的后续处理工作流。
- `Ruri.FModelHook`: FModel / UE 增强层，当前重点在 shader bytecode 导出、metadata 汇总、进程内反编译，以及向 Unity 统一表示转换所需的前置数据整理。
- `Ruri.AssemblyDumper`: 用于生成和组织结构定义与类读取辅助代码。
- `Ruri.RipperHook.GUI` / `Ruri.RipperHook.CLI`: 图形界面和命令行入口。

## 主要能力

- 运行时 AOP Hook 与方法重定向。
- 读取、导出、转换流程接管。
- AssetRipper、FModel、AssemblyDumper 等工具链行为扩展。
- 更细粒度的导出控制，例如直接导出、跳过冗余流程、静态网格拆分、BundledAssets 导出模式增强。
- 特殊 Block / VFS / 流式资源格式的数据接入与自定义读取。
- 结构差异和版本差异的读取流程修补与适配。
- 更好的 shader 数据支持，包括 metadata 补全、符号组织和反编译链路扩展。
- 面向 Unity 统一数据模型的跨工具链收敛能力。
- Unity 与 UE / FModel 两类工具栈的统一扩展方式。

## 适用场景

- 为现有资源导出工具补齐缺失能力。
- 提升复杂资源格式下的数据读取成功率。
- 构建更稳定、更细粒度的导出流程。
- 为 shader 数据建立更完整的提取、映射和反编译支持链路。
- 把 UE / FModel 侧数据逐步整理为可复用的 Unity 表示层。
- 修补上游工具在特定版本、特定格式、特定流程上的兼容性问题。
- 将零散流程补丁整理为可组合、可复用的工程化模块。

## 使用说明

- 把 `Ruri.RipperHook` 作为启动项目并完成编译。
- 某些 Hook 偶发失效时，通常与增量 Hot Reload 残留状态有关，重新触发一次编译即可恢复。
- 这是偏工程化的扩展框架，不是面向完全零基础用户的一键工具。更适合已经理解上游工具链、数据结构和导出目标的人继续扩展。

## Feature

- AssemblyDumper support
- Free Shader Decompile (DX11)
- Generic AOP-style export and data processing workflow
- AssetRipper enhancement pipeline
- FModel / UE shader export and decompile workflow
- Long-term Unity data unification pipeline
- Extensible hook pipeline for project-specific data handling

## Todo

- ~~需要优化Block格式的AB包解析(WMW/VFS/BLK等) 内存拆分读取容易过于碎片化导致内存无法分配~~ 我发现这是AR作者去年9月的提交导致的问题 他抽象了文件中间层LocalFileSystem导致现在不会使用虚拟内存加载 问他也不理我说明他不想管这个问题 在此之前是可以用虚拟内存解决的
- 更小的AssemblyDumper生成 目前有太多代码实际上不需要生成 最小能优化到1mb以下的dll 只需要里面的定义和Read就够了
- AssemblyDumper生成工作流简化
- 如果不同游戏版本依赖同样的加密 新版本应该直接依赖旧版本 任何相同的代码都不应该出现2次
- FModel / UE 数据到等价 Unity YAML 的稳定转换链路
- 将 Unity 统一表示正式接入 AssetRipper 后续浏览、导出和后处理工作流
- 利用wrapper官方反编译保证完全准确，Unity 的 shader blob 是无符号的，cb 信息在 ShaderLab，理论上可以向 shader blob（DXBC / DXIL / SPIR-V）添加符号信息实现反编译出准确的 cb，(USC采用的是自行解析汇编所以能完美支持cb反编译 如果使用外置反编译器就只能解析shaderlab的cb符号并注入shader blob了)
- shader反编译目前已有思路 使用dxbc转dxil转spv 然后用 SPIRV-Cross反编译 注入spv符号绑定已经测试可行了 这样只需要维护spv中间码符号注入即可

## Special Thanks to

- **ds5678**: Original author.
- **AnimeStudio**: For anything.
- **nesrak1**: USCSandbox author.
- **Razmoth**: For anything.
