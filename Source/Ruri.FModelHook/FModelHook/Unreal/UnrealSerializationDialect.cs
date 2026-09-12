using System.Collections.Frozen;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.Hook.Attributes;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// Where a build's cooked layout is not the one its engine version implies.
///
/// Every version-gated field is decided by the custom versions the package declares. A build
/// that declares none leaves the reader to answer from the engine version alone, and that answer
/// is wrong for exactly the parts a studio backported from a newer engine onto its own branch.
/// Which parts those are is a fact about the build, so a title states it as data and this applies
/// it at the one read that follows from it.
///
/// The narrow statement is the point. Declaring the custom version for the whole mount would
/// claim that every other change of that stream is present too, which is a different and much
/// larger claim: the same version decides how materials, animation, textures, world partition and
/// the RigVM are read, and a build that backported one field did not necessarily backport those.
/// </summary>
public static class UnrealSerializationDialect
{
    /// <summary>
    /// Read a cooked skeletal mesh section at the version the build's SECTIONS were written at.
    ///
    /// The engine's own reader asks the //UE5/Main stream twice here -- once for the ray tracing
    /// visibility flag a section carries and once for the packed bone map -- and both follow from
    /// that one version, so both are answered by what the title states. A build no title states
    /// this for keeps the answer it already had, which is why this is installed for every Unreal
    /// build and changes nothing for almost all of them.
    /// </summary>
    [RetargetMethodFunc(typeof(FSkelMeshSection), nameof(FSkelMeshSection.SerializeRenderItem), typeof(FAssetArchive))]
    public static bool SkeletalMeshSection(ILContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MethodInfo stated = typeof(UnrealSerializationDialect)
            .GetMethod(nameof(SectionVersion), BindingFlags.NonPublic | BindingFlags.Static)!;
        ILCursor cursor = new(context);
        int rewritten = 0;
        while (cursor.TryGotoNext(MoveType.After, instruction => IsCallTo(instruction,
                   nameof(FUE5MainStreamObjectVersion), nameof(FUE5MainStreamObjectVersion.Get))))
        {
            cursor.MoveAfterLabels();
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call, stated);
            rewritten++;
        }
        return rewritten > 0;
    }

    /// <summary>What the section is read at: the title's statement when it makes one, else the engine's own answer.</summary>
    private static FUE5MainStreamObjectVersion.Type SectionVersion(FUE5MainStreamObjectVersion.Type engine, FArchive archive) =>
        SkeletalMeshSections.TryGetValue(archive.Game, out FUE5MainStreamObjectVersion.Type stated) ? stated : engine;

    /// <summary>
    /// The section version each declaring title's dialect is read at, by the dialect itself: what
    /// an archive carries at this point is the dialect it was opened with, and a title's dialect
    /// is what that title's row states it cooked with.
    /// </summary>
    private static readonly Lazy<FrozenDictionary<EGame, FUE5MainStreamObjectVersion.Type>> Declared = new(Collect);

    private static FrozenDictionary<EGame, FUE5MainStreamObjectVersion.Type> SkeletalMeshSections => Declared.Value;

    private static FrozenDictionary<EGame, FUE5MainStreamObjectVersion.Type> Collect()
    {
        Dictionary<EGame, FUE5MainStreamObjectVersion.Type> stated = new();
        foreach (UnrealTitle title in UnrealTitles.All)
        {
            if (title.SkeletalMeshSectionVersion is { } version)
            {
                stated[title.Game] = version;
            }
        }
        return stated.ToFrozenDictionary();
    }

    private static bool IsCallTo(Instruction instruction, string declaringType, string method) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference reference
        && reference.Name == method
        && reference.DeclaringType.Name == declaringType;
}
