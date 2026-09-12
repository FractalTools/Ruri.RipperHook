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

[RipperFeature("DisassemblyExporter")]
public partial class AR_DisassemblyExporter_Hook : RipperHookCommon
{
    [RetargetMethodFunc(typeof(ProjectExporter), "CreateCollections")]
    public static bool ProjectExporter_CreateCollections(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            cursor.EmitDelegate(FilterToScriptsOnly);
            cursor.Index++;            injected++;
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
            cursor.EmitDelegate(ForceDecompileSavedAssemblies);
            cursor.Index++;
            injected++;
        }

        Console.WriteLine($"    [+] AR_DisassemblyExporter: forced decompile-all at {injected} GetExportType return site(s)");
        return injected > 0;
    }

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

    private static AssemblyExportType ForceDecompileSavedAssemblies(AssemblyExportType exportType)
        => exportType == AssemblyExportType.Save ? AssemblyExportType.Decompile : exportType;

    [RetargetMethodCtorFunc(typeof(ImportSettings))]
    public static bool ImportSettings_Ctor(ILContext il)
    {
        ILCursor cursor = new(il);

        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Ldarg_0);            cursor.EmitDelegate(SkipStreamingAssets);
            cursor.Index++;            injected++;
        }

        Console.WriteLine($"    [+] AR_DisassemblyExporter: forced IgnoreStreamingAssets at {injected} ImportSettings ctor return site(s)");
        return injected > 0;
    }

    private static void SkipStreamingAssets(ImportSettings settings) => settings.IgnoreStreamingAssets = true;
}
