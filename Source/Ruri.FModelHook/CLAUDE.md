查看md 然后继续强化UE的符号探索 派遣大量专家Agent并行分析源码
要用烟雾测试shader反编译 因为同shader不同变体基本上都是没什么新东西的 随机抽选几个解包分析测试不要浪费时间全部反编译
"D:\GameStudy\OniValleyDemo"
对应的解包配置"D:\GameStudy\AppSettings_OniValleyDemo.json"
对应引擎版本"D:\GameStudy\UnrealEngine-5.1.1-release"

"D:\GameStudy\InfinityNikkiGlobal Launcher\InfinityNikkiGlobal"
对应的解包配置"D:\GameStudy\AppSettings_InfinityNikkiGlobal.json"
对应引擎版本"D:\GameStudy\UnrealEngine-5.4.4-release"

"D:\Ruri\Github\FractalTools\Ruri-RipperHook\Source\Ruri.FModelHook"和Ruri.FModelHook.CLI进行自测试循环
"D:\Ruri\Github\FractalTools\Ruri-RipperHook\Source\Ruri.ShaderDecompiler"
直接先反编译 然后查看有哪些cb符号仍然不足 如果编译后根本不可能还原 就实现一套外部cb符号定义 不要硬编码在代码里 而是程序直接读取特定文件夹中的 特定命名规范的metadata [cbname]_MetaData.json这种 但必须是穷举所有源码真理 并且所有Agent都一致认为绝对不可能的情况下才允许这样做 否则哪怕是再细小的线索也给我找遍 然后系统化整理UE的符号来源md 统一为1个md 并且直接极简化不需要太多废话 把所有符号的来源源码都写明