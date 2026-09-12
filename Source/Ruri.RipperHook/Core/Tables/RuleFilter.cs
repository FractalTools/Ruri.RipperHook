using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Tables;

public sealed record FilterRule(string Field, string Relation, string Value, bool Include, bool Enabled);

public static class RuleFilter
{
    public static bool AnyEnabled(IReadOnlyList<FilterRule>? rules)
        => rules is not null && rules.Any(static rule => rule.Enabled);

    public static int[] Apply(int[] candidates, IReadOnlyList<FilterRule> rules,
        Func<int, string, string> field)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(field);
        FilterRule[] enabled = (rules ?? []).Where(static rule => rule.Enabled).ToArray();
        if (enabled.Length == 0 || candidates.Length == 0)
        {
            return candidates;
        }
        bool[] keep = new bool[candidates.Length];
        int partitions = Math.Clamp(Environment.ProcessorCount, 1, candidates.Length);
        int perPartition = (candidates.Length + partitions - 1) / partitions;
        Parallel.For(0, partitions, partition =>
        {
            int first = partition * perPartition;
            if (first >= candidates.Length)
            {
                return;
            }
            int last = Math.Min(first + perPartition, candidates.Length);
            Regex?[] regexes = new Regex?[enabled.Length];
            for (int r = 0; r < enabled.Length; r++)
            {
                if (enabled[r].Relation is "matches_regex" or "not_matches_regex")
                {
                    try
                    {
                        regexes[r] = new Regex(enabled[r].Value, RegexOptions.IgnoreCase);
                    }
                    catch (ArgumentException)
                    {
                        regexes[r] = null;                    }
                }
            }
            for (int i = first; i < last; i++)
            {
                keep[i] = RowPasses(candidates[i], enabled, regexes, field);
            }
        });
        List<int> result = new(candidates.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            if (keep[i])
            {
                result.Add(candidates[i]);
            }
        }
        return result.ToArray();
    }

    private static bool RowPasses(int id, FilterRule[] rules, Regex?[] regexes,
        Func<int, string, string> field)
    {
        for (int r = 0; r < rules.Length; r++)
        {
            FilterRule rule = rules[r];
            string value = field(id, rule.Field);
            bool matched = RelationMatches(value, rule.Relation, rule.Value, regexes[r]);
            if (rule.Include)
            {
                if (!matched)
                {
                    return false;
                }
            }
            else if (matched)
            {
                return false;
            }
        }
        return true;
    }

    private static bool RelationMatches(string value, string relation, string filter, Regex? regex)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        switch (relation)
        {
            case "is": return value.Equals(filter, ic);
            case "is_not": return !value.Equals(filter, ic);
            case "contains": return value.Contains(filter, ic);
            case "excludes": return !value.Contains(filter, ic);
            case "begins_with": return value.StartsWith(filter, ic);
            case "ends_with": return value.EndsWith(filter, ic);
            case "less_than":
            case "more_than":
            {
                if (!double.TryParse(value, out double left) || !double.TryParse(filter, out double right))
                {
                    return false;
                }
                return relation == "less_than" ? left < right : left > right;
            }
            case "matches_regex": return regex is not null && regex.IsMatch(value);
            case "not_matches_regex": return regex is not null && !regex.IsMatch(value);
            default: return false;
        }
    }
}
