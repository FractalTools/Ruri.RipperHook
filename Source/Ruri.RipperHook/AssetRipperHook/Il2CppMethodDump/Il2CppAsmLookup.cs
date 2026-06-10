using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cpp2IL.Core.Model.Contexts;
using ICSharpCode.Decompiler.TypeSystem;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 把 ILSpy 反编译出的方法（<see cref="IMethod"/>）对应回 Cpp2IL 分析上下文里的方法
/// （<see cref="MethodAnalysisContext"/>），并返回其原生反汇编文本。
/// 关联键 = 程序集名 | 规范化类型全名 :: 方法名 / 形参个数。规范化会把嵌套分隔符（+ / \）统一成点、
/// 去掉泛型元数（`1）——以此对齐 ILSpy 与 Cpp2IL 两边的命名差异。非消费式、幂等，可重复导出。
/// </summary>
internal static class Il2CppAsmLookup
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, List<MethodAnalysisContext>> _map = new();
    private static ApplicationAnalysisContext _builtFor;

    private static string Normalize(string typeFullName)
    {
        if (typeFullName == null) return null;
        string s = typeFullName.Replace('/', '.').Replace('+', '.').Replace('\\', '.');
        return Regex.Replace(s, "`\\d+", ""); // CyclicalList`1 -> CyclicalList
    }

    private static string Key(string assembly, string type, string method, int paramCount)
        => assembly + "|" + type + "::" + method + "/" + paramCount;

    // Caller must hold _gate.
    private static void EnsureBuilt(ApplicationAnalysisContext app)
    {
        if (ReferenceEquals(_builtFor, app)) return;
        _map.Clear();
        foreach (AssemblyAnalysisContext assembly in app.Assemblies)
        {
            string assemblyName = assembly.CleanAssemblyName;
            foreach (TypeAnalysisContext type in assembly.Types)
            {
                if (type?.Methods == null) continue;
                string typeName = Normalize(type.FullName);
                foreach (MethodAnalysisContext method in type.Methods)
                {
                    if (method.UnderlyingPointer == 0) continue; // abstract / extern / no native body
                    string key = Key(assemblyName, typeName, method.Name, method.Parameters.Count);
                    if (!_map.TryGetValue(key, out List<MethodAnalysisContext> list))
                    {
                        list = new List<MethodAnalysisContext>();
                        _map[key] = list;
                    }
                    list.Add(method);
                }
            }
        }
        _builtFor = app;
    }

    /// <summary>
    /// Returns the native disassembly (VA/RVA header + asm lines) for <paramref name="method"/>,
    /// or null if this isn't an IL2CPP game or no native body maps to it.
    /// Serialized: Cpp2IL's X86 PrintAssembly uses a static formatter (not thread-safe) and ILSpy
    /// decompiles files in parallel.
    /// </summary>
    public static string GetDisassembly(IMethod method)
    {
        ApplicationAnalysisContext app = Cpp2IlApi.CurrentAppContext;
        if (app == null) return null;

        lock (_gate)
        {
            EnsureBuilt(app);
            string key = Key(method.ParentModule?.Name, Normalize(method.DeclaringTypeDefinition?.FullName), method.Name, method.Parameters.Count);
            if (!_map.TryGetValue(key, out List<MethodAnalysisContext> list) || list.Count == 0)
            {
                return null;
            }
            MethodAnalysisContext ctx = list[0];
            try
            {
                string asm = app.InstructionSet.PrintAssembly(ctx);
                asm = Il2CppAsmAnnotator.Annotate(app, asm); // 裸地址 → 符号注释
                return $"VA=0x{ctx.UnderlyingPointer:X}  RVA=0x{ctx.Rva:X}\n{asm}";
            }
            catch
            {
                return null;
            }
        }
    }
}
