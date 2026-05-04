using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Structure.Assembly.Managers;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Ruri.RipperHook.AR;

public partial class AR_ShaderDecompiler_Hook
{
    // DXDecompile Retarget
    [RetargetMethodCtorFunc(typeof(ProjectExporter), [typeof(FullConfiguration), typeof(IAssemblyManager)])]
    public static bool Ctor(ILContext il)
    {
        var ilCursor = new ILCursor(il);

        // 寻找 newobj DummyShaderTextExporter 指令
        if (ilCursor.TryGotoNext(instr =>
            instr.OpCode == OpCodes.Newobj &&
            instr.Operand is MethodReference methodRef &&
            methodRef.DeclaringType.Name == "DummyShaderTextExporter"))
        {
            // 获取你自己的类的构造函数
			var newCtor = typeof(ShaderRuriDecompileExporter).GetConstructor(Type.EmptyTypes);

            // 注意：必须使用 il.Module.ImportReference 将反射的 ConstructorInfo 转换为 Cecil 的 MethodReference
            ilCursor.Next.Operand = il.Module.ImportReference(newCtor);

            return true;
        }

        return false;
    }
}
