using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Ruri.RipperHook.RuriEngine;

[RipperFeature("UnityYamlProject")]
public partial class AR_UnityYamlProject_Hook : RipperHookCommon
{
    protected AR_UnityYamlProject_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RuriEngineSession.Reset();
        HookManager.RegisterCleanup(RuriEngineSession.Reset);
        base.InitAttributeHook();
    }

    [RetargetMethodFunc(typeof(ExportHandler), "Load")]
    public static bool ExportHandler_Load(ILContext il)
    {
        ILCursor cursor = new(il);
        Instruction original = cursor.Next;
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.EmitDelegate<Func<IReadOnlyList<string>, FileSystem, GameData?>>(UnityProjectReader.TryLoad);
        cursor.Emit(OpCodes.Dup);
        cursor.Emit(OpCodes.Brfalse, original);
        Instruction branch = cursor.Prev;
        cursor.Emit(OpCodes.Ret);
        cursor.Emit(OpCodes.Pop);
        branch.Operand = cursor.Prev;
        return true;
    }
}
