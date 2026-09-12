using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Structure.Assembly.Managers;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Ruri.RipperHook.AR;

public partial class AR_ShaderDecompiler_Hook
{
    [RetargetMethodCtorFunc(typeof(ProjectExporter), [typeof(FullConfiguration), typeof(IAssemblyManager)])]
    public static bool Ctor(ILContext il)
    {
        var ilCursor = new ILCursor(il);

        if (ilCursor.TryGotoNext(instr =>
            instr.OpCode == OpCodes.Newobj &&
            instr.Operand is MethodReference methodRef &&
            methodRef.DeclaringType.Name == "DummyShaderTextExporter"))
        {
			var newCtor = typeof(ShaderRuriDecompileExporter).GetConstructor(Type.EmptyTypes);

            ilCursor.Next.Operand = il.Module.ImportReference(newCtor);

            return true;
        }

        return false;
    }
}
