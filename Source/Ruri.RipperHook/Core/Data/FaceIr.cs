namespace Ruri.RipperHook.Data;

/// <summary>
/// The vocabulary two different faces meet in. A source states a channel and the pattern it
/// selected; a destination resolves that name against whatever it actually has -- a shape key,
/// a bone, both. A pattern is a PAIR of shapes (shut, open) blended by an openness rate, so
/// the shut end is named too: a destination may well drive it with something else, and a
/// vocabulary that only names the open end loses every half-lidded expression.
/// </summary>
public static class FaceIr
{
    public const string ClosedMark = "#closed";

    public static string Key(string channel, int pattern) => channel + ":" + pattern;

    public static string Key(string channel, int pattern, bool closed) =>
        closed ? Key(channel, pattern) + ClosedMark : Key(channel, pattern);
}
