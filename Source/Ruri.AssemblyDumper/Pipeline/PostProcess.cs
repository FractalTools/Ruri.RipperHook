using AsmResolver.DotNet;

namespace Ruri.AssemblyDumper.Pipeline;

/// <summary>
/// 替换 SharedState 里 <c>const string AssemblyName = "Ruri.SourceGenerated"</c> 这一行 diff 的
/// 运行时等价物。const 编译期 inline 没法 hook，所以让 AR pass 先按上游的 "AssetRipper.SourceGenerated"
/// 跑完，最后在 Pass998 之前把 Module / Assembly / 所有 TypeDefinition.Namespace / TypeReference.Namespace
/// 上的前缀替换成 "Ruri.SourceGenerated"。
/// </summary>
internal static class PostProcess
{
    private const string ArName = "AssetRipper.SourceGenerated";
    private const string RuriName = "Ruri.SourceGenerated";

    public static void RenameAssemblyAndNamespaces()
    {
        ModuleDefinition module = ArReflect.SharedStateModule;
        AssemblyDefinition asm = module.Assembly!;

        if (asm.Name == ArName) asm.Name = RuriName;
        if (module.Name == ArName + ".dll") module.Name = RuriName + ".dll";

        int typeRenamed = 0, refRenamed = 0;
        foreach (TypeDefinition type in module.GetAllTypes().ToList())
        {
            if (TryRewriteNamespace(type.Namespace, out string newNs))
            {
                type.Namespace = newNs;
                typeRenamed++;
            }
        }
        foreach (TypeReference typeRef in module.GetImportedTypeReferences())
        {
            if (TryRewriteNamespace(typeRef.Namespace, out string newNs))
            {
                typeRef.Namespace = newNs;
                refRenamed++;
            }
        }
        Console.WriteLine($"[PostProcess] Renamed assembly to {RuriName} (types={typeRenamed}, typeRefs={refRenamed}).");
    }

    private static bool TryRewriteNamespace(string? ns, out string result)
    {
        result = ns ?? "";
        if (ns is null) return false;
        if (ns == ArName) { result = RuriName; return true; }
        if (ns.StartsWith(ArName + ".", StringComparison.Ordinal))
        {
            result = RuriName + ns.Substring(ArName.Length);
            return true;
        }
        return false;
    }
}
