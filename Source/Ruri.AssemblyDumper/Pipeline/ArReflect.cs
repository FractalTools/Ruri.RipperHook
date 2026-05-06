using System.Reflection;

namespace Ruri.AssemblyDumper.Pipeline;

/// <summary>
/// 反射门面 — AssetRipper.AssemblyDumper 大多数 Pass 是 internal，且 IgnoresAccessChecksToGenerator
/// 对 ProjectReference 不生效（实测 CS0436 与 MonoMod.Utils 冲突）。
/// </summary>
internal static class ArReflect
{
    public static readonly Assembly Asm = typeof(global::AssetRipper.AssemblyDumper.Passes.Pass100_FillReadMethods).Assembly;
    public static readonly Assembly UtilsAsm = typeof(global::AssetRipper.AssemblyDumper.Utils.VersionedList).Assembly;

    public static Type Type(string fullName) =>
        Asm.GetType(fullName, throwOnError: false)
        ?? UtilsAsm.GetType(fullName, throwOnError: false)
        ?? throw new TypeLoadException($"Type not found: {fullName}");

    public static MethodInfo? TryMethod(string typeFullName, string methodName,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
    {
        Type? t = Asm.GetType(typeFullName, throwOnError: false) ?? UtilsAsm.GetType(typeFullName, throwOnError: false);
        return t?.GetMethod(methodName, flags);
    }

    public static object SharedStateInstance =>
        Type("AssetRipper.AssemblyDumper.SharedState")
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    public static AsmResolver.DotNet.ModuleDefinition SharedStateModule =>
        (AsmResolver.DotNet.ModuleDefinition)SharedStateInstance.GetType().GetProperty("Module")!.GetValue(SharedStateInstance)!;

    public static void InvokeStaticVoid(string typeFullName, string methodName, params object[] args)
    {
        var mi = TryMethod(typeFullName, methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeFullName, methodName);
        mi.Invoke(null, args);
    }
}
