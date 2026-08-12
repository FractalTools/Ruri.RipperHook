using System;

namespace Ruri.RipperHook.Core;

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

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }
}
