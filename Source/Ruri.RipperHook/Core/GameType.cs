namespace Ruri.RipperHook;

/// <summary>
/// The games this build can decode. A member's NAME is the Unity productName the game's own
/// player carries (the second line of its <c>&lt;Product&gt;_Data/app.info</c>, which is
/// PlayerSettings.productName) -- spelled exactly, case included, so an install joins to its
/// decoder by string equality with nothing translating in between. Rename the member when the
/// game renames itself; never adapt the game's word to ours.
///
/// Host capabilities are NOT in here: a feature is about no game, carries no version and
/// excludes nothing, so it declares itself with <see cref="Attributes.RipperFeatureAttribute"/>.
///
/// StarRail ships no decoder yet and is still a real member: <c>Ruri.Tpk</c> joins a type tree
/// dump's <see cref="Core.CustomEngineType"/> to this enum BY NAME, so the two spellings are
/// one contract.
/// </summary>
public enum GameType
{
    Unknown = 0,
    Arknights,
    Endfield,
    EXILIUM,
    StarRail,
    PunishingGrayRaven,
    AzurPromilia,
    ExAstris,
    Koikatu,
    KoikatsuSunshine,
    HoneyCome,
    SamabakeScramble,
}
