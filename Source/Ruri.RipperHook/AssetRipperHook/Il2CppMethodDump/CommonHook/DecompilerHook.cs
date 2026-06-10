using System;
using System.Reflection;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Ruri.RipperHook.AR;

public partial class AR_Il2CppMethodDump_Hook
{
    /// <summary>
    /// Hook ILSpy's per-file decompiler factory (<c>WholeProjectDecompiler.CreateDecompiler</c>):
    /// before it returns, dup the freshly-built <see cref="CSharpDecompiler"/> and append our
    /// asm-comment AST transform. AR's <c>CustomWholeProjectDecompiler</c> does not override this,
    /// so the base (hooked) implementation runs during script export.
    /// </summary>
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

        cursor.Emit(OpCodes.Dup);                  // [decompiler, decompiler]
        cursor.Emit(OpCodes.Call, addTransform);   // AddTransform(decompiler) -> [decompiler]
        return true;
    }

    /// <summary>
    /// Append the asm-comment transform to a decompiler (idempotent). Public so the IL call injected
    /// into the ILSpy assembly can reach it.
    /// </summary>
    public static void AddTransform(CSharpDecompiler decompiler)
    {
        if (decompiler == null) return;
        foreach (ICSharpCode.Decompiler.CSharp.Transforms.IAstTransform transform in decompiler.AstTransforms)
        {
            if (transform is Il2CppAsmCommentTransform) return;
        }
        decompiler.AstTransforms.Add(new Il2CppAsmCommentTransform());
    }
}
