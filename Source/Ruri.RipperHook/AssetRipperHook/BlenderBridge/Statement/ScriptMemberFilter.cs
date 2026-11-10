namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// Which members of the engine classes a scripts statement states, as data of the request: entries of the form
/// <c>Class.member.path</c>, each naming a member of a class the request's classes carry. A class with entries is stated as
/// those members and nothing else; a class with none is stated whole, as it always was. The request is what knows what its
/// reader needs of a class, so no class has a table in the code.
/// </summary>
public sealed class ScriptMemberFilter
{
    private readonly Dictionary<string, ClassMemberSet> _classes = new(StringComparer.Ordinal);

    private ScriptMemberFilter()
    {
    }

    public static ScriptMemberFilter None { get; } = new();

    /// <summary>The filter the entries make, refusing an entry that is not <c>Class.path</c> or names a class the request does not
    /// state.</summary>
    public static ScriptMemberFilter Parse(IReadOnlyList<string> entries, IReadOnlyList<string> classes)
    {
        if (entries.Count == 0)
        {
            return None;
        }
        ScriptMemberFilter filter = new();
        foreach (string entry in entries)
        {
            int dot = entry.IndexOf('.');
            if (dot <= 0 || dot == entry.Length - 1 || entry.Contains("..", StringComparison.Ordinal) || entry[^1] == '.')
            {
                throw new ArgumentException($"members entry '{entry}' is not Class.member[.member...].");
            }
            string className = entry[..dot];
            if (!classes.Contains(className))
            {
                throw new ArgumentException($"members entry '{entry}' names {className}, which classes does not carry.");
            }
            if (!filter._classes.TryGetValue(className, out ClassMemberSet? members))
            {
                members = new ClassMemberSet();
                filter._classes.Add(className, members);
            }
            members.Add(entry[(dot + 1)..]);
        }
        return filter;
    }

    /// <summary>The members of this class the request names, or null when it names none and the class is stated whole.</summary>
    public ClassMemberSet? Of(string className) => _classes.TryGetValue(className, out ClassMemberSet? members) ? members : null;
}
