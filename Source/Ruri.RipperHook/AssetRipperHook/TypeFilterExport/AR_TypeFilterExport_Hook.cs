using System;
using System.Collections.Generic;
using System.Linq;
using AssetRipper.Export.UnityProjects;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 按类型导出（统一的“仅导出某些 ClassID 的资产”过滤器）。导出前把 <see cref="TargetClassIds"/> 设成想要的
/// ClassID 集合，则工程导出阶段只保留“资产属于这些类型”的集合，其余一切跳过。集合为空 = 不过滤（全量）。
///
/// 这是 shader / 任意按类型导出共用的底座：
///   * 导出 shader：TargetClassIds = { Shader(48), ComputeShader(72) }，再配 <see cref="AR_ShaderDecompiler_Hook"/>；
///   * 按类型批量导出：TargetClassIds = 用户在对话框里勾选的类型。
/// 配合 CAB map 的“按类型解析”（只加载含目标类型的 bundle + 依赖），就能不把整张游戏读进内存再过滤。
///
/// 实现：before-Ret IL 注入钩 <c>ProjectExporter.CreateCollections</c>，过滤其返回的集合列表。
/// </summary>
[RipperHook(GameType.AR_TypeFilterExport)]
public partial class AR_TypeFilterExport_Hook : RipperHookCommon
{
    /// <summary>导出前设置：只导出资产 ClassID 在此集合内的导出集合。空 = 不过滤。GUI/CLI 跨程序集写同一静态。</summary>
    public static readonly HashSet<int> TargetClassIds = new();

    [RetargetMethodFunc(typeof(ProjectExporter), "CreateCollections")]
    public static bool ProjectExporter_CreateCollections(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            cursor.EmitDelegate(FilterToTargetTypes);
            cursor.Index++;
            injected++;
        }

        Console.WriteLine($"    [+] AR_TypeFilterExport: injected type filter at {injected} return site(s)");
        return injected > 0;
    }

    private static List<IExportCollection> FilterToTargetTypes(List<IExportCollection> collections)
    {
        if (collections == null || TargetClassIds.Count == 0)
        {
            return collections!; // empty target set => passthrough
        }

        List<IExportCollection> kept = collections
            .Where(static c => c.Assets.Any(static a => TargetClassIds.Contains(a.ClassID)))
            .ToList();
        Console.WriteLine($"    [+] AR_TypeFilterExport: {collections.Count} collections -> kept {kept.Count} (types: {string.Join(",", TargetClassIds)})");
        return kept;
    }
}
