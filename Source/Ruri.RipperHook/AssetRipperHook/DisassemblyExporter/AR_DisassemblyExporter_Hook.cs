using System;
using System.Collections.Generic;
using System.Linq;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.Import.Configuration;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 反汇编导出：只导出代码（忽略一切 asset 资产），并把所有程序集都反编译成带原生汇编注释的 .cs。
/// 配合 <see cref="AR_Il2CppMethodDump_Hook"/>，每个方法体内会注入对应的 x86/ARM 反汇编。
///
/// 实现方式（三处 before-Ret IL 注入，和能用的 DecompilerHook 同一套路）：
/// ① 钩 <c>ProjectExporter.CreateCollections</c>，把返回的集合列表过滤成“只剩脚本集合”，于是 Export
///    后续只遍历脚本集合，跳过一切资产；
/// ② 钩 <c>ScriptExporter.GetExportType(string)</c>，把所有 <see cref="AssemblyExportType.Save"/>
///    （Hybrid 模式下本会被原样塞进 Plugins/ 的游戏程序集）强制改成 <see cref="AssemblyExportType.Decompile"/>，
///    于是每个游戏程序集都反编译成带汇编的 .cs；框架引用程序集仍保持 Skip。
/// ③ 钩 <see cref="ImportSettings"/> 无参 ctor，把默认置为 <c>IgnoreStreamingAssets=true</c>：代码-only 导出
///    从不消费 StreamingAssets，却会因结构加载默认 Extract 而被全量载入（Endfield 高达 ~59 GB）——跳过它，
///    只载入 IL2CPP/Mono 程序集与核心序列化文件，加载侧与“只出代码”的语义一致。
/// hook 未启用时不安装、零影响；启用时所有导出都只出代码、且全部反编译。
/// </summary>
[RipperHook(GameType.AR_DisassemblyExporter)]
public partial class AR_DisassemblyExporter_Hook : RipperHookCommon
{
    [RetargetMethodFunc(typeof(ProjectExporter), "CreateCollections")]
    public static bool ProjectExporter_CreateCollections(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            // 此刻栈顶就是即将 return 的 List<IExportCollection>，过滤后再 ret。
            cursor.EmitDelegate(FilterToScriptsOnly);
            cursor.Index++; // 跳过这个 ret，避免对刚插入的代码重复匹配
            injected++;
        }

        Console.WriteLine($"    [+] AR_DisassemblyExporter: injected scripts-only filter at {injected} return site(s)");
        return injected > 0;
    }

    [RetargetMethodFunc(typeof(ScriptExporter), "GetExportType", typeof(string))]
    public static bool ScriptExporter_GetExportType(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            // 栈顶是即将 return 的 AssemblyExportType，把 Save 强制改成 Decompile。
            cursor.EmitDelegate(ForceDecompileSavedAssemblies);
            cursor.Index++;
            injected++;
        }

        Console.WriteLine($"    [+] AR_DisassemblyExporter: forced decompile-all at {injected} GetExportType return site(s)");
        return injected > 0;
    }

    /// <summary>只保留脚本反编译集合，丢掉其它一切资产集合。</summary>
    private static List<IExportCollection> FilterToScriptsOnly(List<IExportCollection> collections)
    {
        if (collections == null)
        {
            return collections!;
        }

        List<IExportCollection> scriptsOnly = collections.Where(static c => c is ScriptExportCollectionBase).ToList();
        Console.WriteLine($"    [+] AR_DisassemblyExporter: {collections.Count} collections -> kept {scriptsOnly.Count} script collection(s), all assets skipped");
        return scriptsOnly;
    }

    /// <summary>Save（会被存成 Plugins/ DLL 的游戏程序集）-> Decompile；Skip（框架引用）与 Decompile 原样保留。</summary>
    private static AssemblyExportType ForceDecompileSavedAssemblies(AssemblyExportType exportType)
        => exportType == AssemblyExportType.Save ? AssemblyExportType.Decompile : exportType;

    [RetargetMethodCtorFunc(typeof(ImportSettings))]
    public static bool ImportSettings_Ctor(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            // ctor 末尾（字段初始化器已跑完）把 StreamingAssetsMode 翻成 Ignore。
            cursor.Emit(OpCodes.Ldarg_0); // 栈推入正在构造的 ImportSettings
            cursor.EmitDelegate(SkipStreamingAssets);
            cursor.Index++; // 跳过这个 ret，避免对刚插入的代码重复匹配
            injected++;
        }

        Console.WriteLine($"    [+] AR_DisassemblyExporter: forced IgnoreStreamingAssets at {injected} ImportSettings ctor return site(s)");
        return injected > 0;
    }

    /// <summary>代码-only 导出从不消费 StreamingAssets，跳过其加载（默认 Extract 会全量载入 bundle）。</summary>
    private static void SkipStreamingAssets(ImportSettings settings) => settings.IgnoreStreamingAssets = true;
}
