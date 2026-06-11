using System;
using System.Collections.Generic;
using System.Linq;
using AssetRipper.Export.UnityProjects;
using AssetRipper.SourceGenerated;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 仅导出着色器（忽略一切其它资产）。启用后，工程导出阶段只保留 Shader / ComputeShader 的导出集合，
/// 其余资产（脚本、贴图、网格、材质、音频、场景等）一律跳过。
///
/// 通常和 <see cref="AR_ShaderDecompiler_Hook"/>（把 DummyShaderTextExporter 换成会反编译的
/// ShaderRuriDecompileExporter）以及 <c>ShaderExportMode.Decompile</c> 一起用，并需要游戏自身的
/// shader 绑定 hook（EndField 的 EndFieldShaderBindingHook / Endfield ShaderBuilder，随游戏 hook 自动装上），
/// 这样才能把 EndField 的 Vulkan 着色器正确反编译出来。
///
/// 实现：用 before-Ret IL 注入钩 <c>ProjectExporter.CreateCollections</c>，把返回的集合列表过滤成
/// “只剩资产是 Shader/ComputeShader 的集合”。hook 未启用时不安装、零影响。
/// </summary>
[RipperHook(GameType.AR_ShaderOnlyExport)]
public partial class AR_ShaderOnlyExport_Hook : RipperHookCommon
{
    [RetargetMethodFunc(typeof(ProjectExporter), "CreateCollections")]
    public static bool ProjectExporter_CreateCollections(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            cursor.EmitDelegate(FilterToShadersOnly);
            cursor.Index++;
            injected++;
        }

        Console.WriteLine($"    [+] AR_ShaderOnlyExport: injected shaders-only filter at {injected} return site(s)");
        return injected > 0;
    }

    /// <summary>只保留资产为 Shader / ComputeShader 的导出集合，丢掉其它一切。</summary>
    private static List<IExportCollection> FilterToShadersOnly(List<IExportCollection> collections)
    {
        if (collections == null)
        {
            return collections!;
        }

        List<IExportCollection> shadersOnly = collections
            .Where(static c => c.Assets.Any(static a => a.ClassID == (int)ClassIDType.Shader || a.ClassID == (int)ClassIDType.ComputeShader))
            .ToList();
        Console.WriteLine($"    [+] AR_ShaderOnlyExport: {collections.Count} collections -> kept {shadersOnly.Count} shader collection(s), everything else skipped");
        return shadersOnly;
    }
}
