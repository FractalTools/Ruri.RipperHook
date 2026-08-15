using System;

namespace Ruri.RipperHook;

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
    Koikatu,
    KoikatsuSunshine,
    HoneyCome,
    SamabakeScramble,

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
    public const string FeaturePrefix = "AR_";

    public static bool IsGame(this GameType type) =>
        type != GameType.Unknown &&
        !type.ToString().StartsWith(FeaturePrefix, StringComparison.Ordinal);
}
