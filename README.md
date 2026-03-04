# ⚠️⚠️⚠️ **WARNING** ⚠️⚠️⚠️
# **❌ 不要通过任何渠道宣传本项目，该项目仅供学习，严禁用于商业用途，下载后请于24小时内删除。 ❌**

# **❌ Please do not promote this project through any channels. This project is for learning and communication purposes only. Commercial use is strictly prohibited. Please delete it within 24 hours after downloading. ❌**

# **❌ 不要 star，更不要 fork 项目备份 保持低调 ❌**

# **❌ DO NOT star, and DO NOT fork this project for backup. Keep a low profile ❌**

# **小鬼太多想了想还是私有了 就怕有心狗举报 现在是邀请制**

## 不要问我怎么用
- 作者不喜欢回复小白问题 因为他很忙 并且曾经是小白的时候受过心理创伤现在极度反社会 你应该找ai帮你
- 把Ruri.RipperHook作为启动项目 直到编译完成就行了
- 有时候会hook失败 但这是因为增量HotReload导致的内存遗漏问题 通常加个空格然后触发重新编译就可以解决

## Feature
- AssemblyDumper support
- Free Shader Decompile (DX11)

## Todo
- ~~需要优化Block格式的AB包解析(WMW/VFS/BLK等) 内存拆分读取容易过于碎片化导致内存无法分配~~ 我发现这是AR作者去年9月的提交导致的问题 他抽象了文件中间层LocalFileSystem导致现在不会使用虚拟内存加载 问他也不理我说明他不想管这个问题 在此之前是可以用虚拟内存解决的
- 更小的AssemblyDumper生成 目前有太多代码实际上不需要生成 最小能优化到1mb以下的dll 只需要里面的定义和Read就够了
- AssemblyDumper生成工作流简化
- 如果不同游戏版本依赖同样的加密 新版本应该直接依赖旧版本 任何相同的代码都不应该出现2次
- 利用wrapper官方反编译保证完全准确，Unity 的 shader blob 是无符号的，cb 信息在 ShaderLab，理论上可以向 shader blob（DXBC / DXIL / SPIR-V）添加符号信息实现反编译出准确的 cb，(USC采用的是自行解析汇编所以能完美支持cb反编译 如果使用外置反编译器就只能解析shaderlab的cb符号并注入shader blob了)
- shader反编译目前已有思路 使用dxbc转dxil转spv 然后用 SPIRV-Cross反编译 注入spv符号绑定已经测试可行了 这样只需要维护spv中间码符号注入即可

## Special Thanks to:
- **ds5678**: Original author.
- **AnimeStudio**: For anything.
- **nesrak1**: USCSandbox author.
- **Razmoth**: For anything.
