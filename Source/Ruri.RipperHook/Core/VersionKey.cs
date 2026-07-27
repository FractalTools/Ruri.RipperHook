using System;

namespace Ruri.RipperHook.Core;

/// <summary>
/// Orders free-form version strings, the way a human reads one: split on the usual separators and
/// compare segment by segment, numerically when both segments are numeric.
///
/// This is the one ordering used everywhere a version is compared -- the type tree chain a lineage
/// inherits along, and the build a capability is <c>[Since]</c>. Nothing packs a version into a
/// number any more, so <c>1.4.4</c> follows <c>1.3.3</c>, <c>1.0.14</c> follows <c>1.0.9</c> instead
/// of preceding it the way an ordinal string compare would, and a game can number its builds however
/// it likes without colliding (<c>1.0.14</c> and <c>10.1.4</c> used to pack to the same 1014).
/// </summary>
public static class VersionKey
{
    private static readonly char[] Separators = ['.', '-', '_', '+'];

    public static int Compare(string left, string right)
    {
        string[] leftParts = left.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        string[] rightParts = right.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        int shared = Math.Min(leftParts.Length, rightParts.Length);
        for (int i = 0; i < shared; i++)
        {
            int result = CompareSegment(leftParts[i], rightParts[i]);
            if (result != 0)
            {
                return result;
            }
        }

        // "1.4" sorts before "1.4.1": the shorter key is the earlier release.
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareSegment(string left, string right)
    {
        bool leftNumeric = long.TryParse(left, out long leftValue);
        bool rightNumeric = long.TryParse(right, out long rightValue);

        if (leftNumeric && rightNumeric)
        {
            return leftValue.CompareTo(rightValue);
        }

        // A numeric segment sorts before an alphanumeric one ("1.4" before "1.4b"), matching how
        // pre-release suffixes read.
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }
}
