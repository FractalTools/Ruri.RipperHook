using Ruri.RipperHook.Attributes;
namespace Ruri.RipperHook.AR;

/// <summary>
/// IL2CPP 原生方法体反汇编（注入反编译后的 C# 脚本）。
/// 对 IL2CPP 游戏（存在 GameAssembly.dll），借助 AssetRipper 依赖的 Cpp2IL 库解析每个方法在
/// GameAssembly 中的函数指针并反汇编其原生方法体，再在 ILSpy 反编译 dummy DLL 生成 C# 脚本时，
/// 把对应汇编以行注释的形式注入到每个方法体内（含构造/属性/索引器访问器）。Mono 游戏不受影响。
/// 输出即 ExportedProject/Assets/Scripts/.../*.cs 里方法体内的 <c>// &lt;asm&gt;</c> 注释。
/// </summary>
[RipperHook(GameType.AR_Il2CppMethodDump)]
public partial class AR_Il2CppMethodDump_Hook : RipperHookCommon
{
}
