using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Ruri.RipperHook.AR;

public partial class AR_Il2CppMethodDump_Hook
{
    [RetargetMethodFunc(typeof(WholeProjectDecompiler), "IncludeTypeWhenDecompilingProject")]
    public static bool IncludeTypeWhenDecompilingProject(ILContext il)
    {
        ILCursor cursor = new(il);
        MethodInfo include = typeof(AR_Il2CppMethodDump_Hook)
            .GetMethod(nameof(IncludeMethodInformation), BindingFlags.Public | BindingFlags.Static);
        bool patched = false;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Ldarg_2);
            cursor.Emit(OpCodes.Call, include);
            cursor.Index++;
            patched = true;
        }
        return patched;
    }

    [RetargetMethodFunc(typeof(CSharpDecompiler), nameof(CSharpDecompiler.MemberIsHidden),
        typeof(MetadataFile), typeof(EntityHandle), typeof(DecompilerSettings))]
    public static bool MemberIsHidden(ILContext il)
    {
        ILCursor cursor = new(il);
        MethodInfo preserve = typeof(AR_Il2CppMethodDump_Hook)
            .GetMethod(nameof(PreserveMethodInformation), BindingFlags.Public | BindingFlags.Static);
        bool patched = false;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call, preserve);
            cursor.Index++;
            patched = true;
        }
        return patched;
    }

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

        cursor.Emit(OpCodes.Dup);
        cursor.Emit(OpCodes.Call, addTransform);
        return true;
    }

    public static bool IncludeMethodInformation(bool included, MetadataFile module, TypeDefinitionHandle type)
        => included || HasMethodInformation(module, type);

    public static bool PreserveMethodInformation(bool hidden, MetadataFile module, EntityHandle member)
        => hidden && (member.Kind != HandleKind.TypeDefinition || !HasMethodInformation(module, (TypeDefinitionHandle)member));

    private static bool HasMethodInformation(MetadataFile module, TypeDefinitionHandle type)
    {
        Il2CppMethodIdentity.ModuleIdentity identity = Il2CppMethodIdentity.ForModule(Cpp2IlApi.CurrentAppContext, module.Metadata);
        if (identity == null || module.Metadata.GetString(module.Metadata.GetTypeDefinition(type).Name) == "<Module>") return false;
        return identity.Types.TryGetValue(MetadataTokens.GetToken(type), out var source) && source != null && HasMethods(source);
    }

    private static bool HasMethods(Cpp2IL.Core.Model.Contexts.TypeAnalysisContext type)
        => type.Methods.Count > 0 || type.NestedTypes.Any(HasMethods);

    public static void AddTransform(CSharpDecompiler decompiler)
    {
        if (decompiler == null) return;
        if (!Il2CppMethodIdentity.OwnsModule(Cpp2IlApi.CurrentAppContext, decompiler.TypeSystem.MainModule.MetadataFile.Metadata)) return;
        foreach (ICSharpCode.Decompiler.CSharp.Transforms.IAstTransform transform in decompiler.AstTransforms)
        {
            if (transform is Il2CppAsmCommentTransform) return;
        }
        decompiler.AstTransforms.Add(new Il2CppLayoutKindTransform());
        decompiler.AstTransforms.Add(new Il2CppAsmCommentTransform());
    }
}
