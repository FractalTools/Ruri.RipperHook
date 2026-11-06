using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

public partial class AR_Il2CppMethodDump_Hook
{
    [RetargetMethodFunc(typeof(ManagedPEImageBuilder), nameof(ManagedPEImageBuilder.CreateImage), typeof(ModuleDefinition))]
    public static bool CaptureMethodIdentities(ILContext il)
    {
        ILCursor cursor = new(il);
        MethodInfo capture = typeof(AR_Il2CppMethodDump_Hook)
            .GetMethod(nameof(CaptureImage), BindingFlags.Public | BindingFlags.Static);
        bool patched = false;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call, capture);
            cursor.Index++;
            patched = true;
        }
        return patched;
    }

    public static void CaptureImage(PEImageBuildResult result, ModuleDefinition module)
    {
        if (result.ConstructedImage == null) return;
        Il2CppMethodIdentity.Capture(Cpp2IlApi.CurrentAppContext, module, result.TokenMapping);
    }
}
