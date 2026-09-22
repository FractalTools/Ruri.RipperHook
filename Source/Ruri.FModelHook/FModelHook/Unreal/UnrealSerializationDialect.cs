using System.Collections.Frozen;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
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
    /// Leave a material parameter's name record at the width the build wrote it, so the value that
    /// follows is read from where it actually is.
    ///
    /// The engine's reader ends the record by aligning, which states the stock record's width as
    /// an arithmetic consequence rather than as a width. A build that widened the record lands
    /// four bytes short of every value and, worse, four bytes short of every NEXT parameter, so
    /// the name lookup -- which is by exact position in the frozen image's patch table -- misses
    /// from the second parameter onward and only resolves again where the two strides happen to
    /// meet. Landing at the stated width instead fixes scalars, vectors and textures at once,
    /// because all three carry this same record and differ only in the value after it.
    /// </summary>
    [RetargetMethodCtorFunc(typeof(FMemoryImageMaterialParameterInfo), typeof(FMemoryImageArchive))]
    public static bool MaterialParameterRecord(ILContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MethodReference land = context.Import(typeof(UnrealSerializationDialect)
            .GetMethod(nameof(LandAfterRecord), BindingFlags.NonPublic | BindingFlags.Static)!);
        MethodReference reads = context.Import(typeof(FArchive).GetProperty(nameof(FArchive.Position))!.GetGetMethod()!);

        VariableDefinition entry = new(context.Import(typeof(long)));
        context.Body.Variables.Add(entry);

        ILCursor cursor = new(context);
        cursor.Goto(0, MoveType.Before);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Callvirt, reads);
        cursor.Emit(OpCodes.Stloc, entry);

        int rewritten = 0;
        while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Ldloc, entry);
            cursor.Emit(OpCodes.Call, land);
            cursor.Index++;
            rewritten++;
        }
        return rewritten > 0;
    }

    /// <summary>
    /// End a scalar parameter where its own record ends rather than at the next eight-byte mark.
    ///
    /// The engine's reader aligns to eight after the value, which costs nothing while the record
    /// plus a float happens to be a multiple of eight and swallows the next parameter's first four
    /// bytes as soon as it is not. The alignment a build's scalars actually have is asked for here
    /// instead of assumed, so a stock build keeps the answer it already had.
    /// </summary>
    [RetargetMethodCtorFunc(typeof(FMaterialScalarParameterInfo), typeof(FMemoryImageArchive))]
    public static bool MaterialScalarParameter(ILContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MethodReference stated = context.Import(typeof(UnrealSerializationDialect)
            .GetMethod(nameof(ScalarRecordAlignment), BindingFlags.NonPublic | BindingFlags.Static)!);

        ILCursor cursor = new(context);
        int rewritten = 0;
        while (cursor.TryGotoNext(MoveType.Before, instruction => IsCallTo(instruction, nameof(AlignUtils), nameof(AlignUtils.Align))))
        {
            cursor.Emit(OpCodes.Pop);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call, stated);
            cursor.Index++;
            rewritten++;
        }
        return rewritten > 0;
    }

    /// <summary>Where the record ends: the width the title states, or wherever the engine's own reader stopped.</summary>
    private static void LandAfterRecord(FMemoryImageArchive archive, long entry)
    {
        if (MaterialParameterRecords.TryGetValue(archive.Game, out int stated))
        {
            archive.Position = entry + stated;
        }
    }

    /// <summary>What a scalar parameter is aligned to: its own four bytes where the title states a record, else the engine's eight.</summary>
    private static int ScalarRecordAlignment(FMemoryImageArchive archive) =>
        MaterialParameterRecords.ContainsKey(archive.Game) ? sizeof(float) : sizeof(long);

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

    /// <summary>The width each declaring title's material parameter record is read at, by the dialect it was cooked with.</summary>
    private static readonly Lazy<FrozenDictionary<EGame, int>> DeclaredRecords = new(CollectRecords);

    private static FrozenDictionary<EGame, int> MaterialParameterRecords => DeclaredRecords.Value;

    private static FrozenDictionary<EGame, int> CollectRecords()
    {
        Dictionary<EGame, int> stated = new();
        foreach (UnrealTitle title in UnrealTitles.All)
        {
            if (title.MaterialParameterRecordBytes is { } bytes)
            {
                stated[title.Game] = bytes;
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
