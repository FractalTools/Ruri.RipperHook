using System;
using System.Collections.Generic;
using System.Linq;
using AssetRipper.Export.UnityProjects;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.AR;

[RipperHook(GameType.AR_TypeFilterExport)]
public partial class AR_TypeFilterExport_Hook : RipperHookCommon
{
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
            return collections!;        }

        List<IExportCollection> kept = collections
            .Where(static c => c.Assets.Any(static a => TargetClassIds.Contains(a.ClassID)))
            .ToList();
        Console.WriteLine($"    [+] AR_TypeFilterExport: {collections.Count} collections -> kept {kept.Count} (types: {string.Join(",", TargetClassIds)})");
        return kept;
    }
}
