using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using ICSharpCode.Decompiler.TypeSystem;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

internal static class Il2CppAsmLookup
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, List<MethodAnalysisContext>> _map = new();
    private static ApplicationAnalysisContext _builtFor;

    private static string Normalize(string typeFullName)
    {
        if (typeFullName == null) return null;
        string s = typeFullName.Replace('/', '.').Replace('+', '.').Replace('\\', '.');
        return Regex.Replace(s, "`\\d+", "");    }

    private static string Key(string assembly, string type, string method, int paramCount)
        => assembly + "|" + type + "::" + method + "/" + paramCount;

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
                    if (method.UnderlyingPointer == 0) continue;                    string key = Key(assemblyName, typeName, method.Name, method.Parameters.Count);
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
                string asm = app.InstructionSet is X86InstructionSet
                    ? Il2CppX86Listing.Render(app, ctx)
                    : Il2CppAsmAnnotator.Annotate(app, app.InstructionSet.PrintAssembly(ctx));
                return $"VA=0x{ctx.UnderlyingPointer:X}  RVA=0x{ctx.Rva:X}\n{asm}";
            }
            catch
            {
                return null;
            }
        }
    }
}
