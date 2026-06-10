using Ruri.RipperHook.Attributes;
namespace Ruri.RipperHook.AR;

/// <summary>
/// IL2CPP 原生方法反汇编导出。
/// 对 IL2CPP 游戏（存在 GameAssembly.dll）在导出 dummy DLL 的同时，借助 AssetRipper 依赖的 Cpp2IL 库，
/// 直接解析每个方法在 GameAssembly 中的函数指针，并反汇编其原生方法体，按程序集写出 .asm 文件，
/// 与 dummy DLL 一起落在 AuxiliaryFiles/Il2CppMethodDump 下。Mono 游戏不受影响。
/// </summary>
[RipperHook(GameType.AR_Il2CppMethodDump)]
public partial class AR_Il2CppMethodDump_Hook : RipperHookCommon
{
}
