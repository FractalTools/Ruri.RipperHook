using System;

namespace Ruri.RipperHook;

/// <summary>
/// Everything a hook can be about. Two kinds live here, told apart by
/// <see cref="GameTypes.FeaturePrefix"/> rather than by where they sit in the list:
/// <para>A <b>GAME</b> is one title. Its hook is that game's whole C# side -- decryption where
/// there is any, class-layout and type-tree fixes, and the readers that turn the game's own
/// containers into data a host can draw. Exactly one game can be active at a time (see
/// <see cref="Ruri.Hook.RuriHook.ApplyHooks"/>): two games' class hooks patch the same
/// AssetRipper methods, so "both" is not a state that decodes anything correctly.</para>
/// <para>An <b>AR_ feature</b> is an AssetRipper-wide processor that is about no game at all
/// (humanoid muscle resolution for Unity-bound exports, shader decompilation, a different
/// exporter). Any number of them combine freely, with each other and with whichever game is
/// active.</para>
/// </summary>
public enum GameType
{
    Unknown = 0,
    Arknights,
    EndField,
    GirlsFrontline2,
    StarRail,
    PunishingGrayRaven,
    AzurPromilia,
    ExAstris,
    Koikatsu,

    AR_ShaderDecompiler,
    AR_SkipStreamingAssetsCopy,
    AR_StaticMeshSeparation,
    AR_SkipProcessingAnimation,
    AR_PrefabOutlining,
    AR_Il2CppMethodDump,
    AR_DisassemblyExporter,
    AR_TypeFilterExport,
    AR_GlbExporter,
    AR_HumanoidToGeneric,
    AR_SerializeReference,
}

public static class GameTypes
{
    /// <summary>
    /// What marks a member as an AssetRipper feature rather than a game. This prefix is the
    /// enum's own vocabulary and the only thing that distinguishes the two kinds, so it is
    /// declared once, here, next to the members that carry it.
    /// </summary>
    public const string FeaturePrefix = "AR_";

    /// <summary>Whether this member names a game, as opposed to an AssetRipper feature.</summary>
    public static bool IsGame(this GameType type) =>
        type != GameType.Unknown &&
        !type.ToString().StartsWith(FeaturePrefix, StringComparison.Ordinal);
}
