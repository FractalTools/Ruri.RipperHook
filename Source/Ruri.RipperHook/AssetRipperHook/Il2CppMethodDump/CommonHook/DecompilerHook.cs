using System;
using System.Reflection;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Ruri.RipperHook.AR;

public partial class AR_Il2CppMethodDump_Hook
{
    [RetargetMethodFunc(typeof(WholeProjectDecompiler), "CreateDecompiler")]
    public static bool CreateDecompiler(ILContext il)
    {
        ILCursor cursor = new(il);
        if (!cursor.TryGotoNext(MoveType.Before, instr => instr.OpCode == OpCodes.Ret))
        {
            return false;
        }

        MethodInfo addTransform = typeof(AR_Il2CppMethodDump_Hook)
            .GetMethod(nameof(AddTransform), BindingFlags.Public | BindingFlags.Static);

        cursor.Emit(OpCodes.Dup);        cursor.Emit(OpCodes.Call, addTransform);        return true;
    }

    public static void AddTransform(CSharpDecompiler decompiler)
    {
        if (decompiler == null) return;
        foreach (ICSharpCode.Decompiler.CSharp.Transforms.IAstTransform transform in decompiler.AstTransforms)
        {
            if (transform is Il2CppAsmCommentTransform) return;
        }
        decompiler.AstTransforms.Add(new Il2CppLayoutKindTransform());
        decompiler.AstTransforms.Add(new Il2CppAsmCommentTransform());
    }
}
