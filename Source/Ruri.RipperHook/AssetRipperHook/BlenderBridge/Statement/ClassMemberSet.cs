namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// The members of one engine class a scripts statement states, as paths from the top of the object down
/// (<c>m_MixerConstant.groups.volumeIndex</c>): the members the request named, everything under them, and the containers that
/// lead to them. A list is a container like any other -- the path of a member of its elements has no index in it.
/// </summary>
public sealed class ClassMemberSet
{
    private const char Separator = '.';

    private readonly HashSet<string> _kept = new(StringComparer.Ordinal);
    private readonly HashSet<string> _containers = new(StringComparer.Ordinal);

    internal void Add(string path)
    {
        _kept.Add(path);
        for (int dot = path.IndexOf(Separator); dot >= 0; dot = path.IndexOf(Separator, dot + 1))
        {
            _containers.Add(path[..dot]);
        }
    }

    /// <summary>Whether the member at this path is stated: it is named, it is under a member that is named, or a member that is
    /// named is under it.</summary>
    public bool Allows(string path)
    {
        string current = path;
        while (true)
        {
            if (_kept.Contains(current))
            {
                return true;
            }
            int dot = current.LastIndexOf(Separator);
            if (dot < 0)
            {
                return _containers.Contains(path);
            }
            current = current[..dot];
        }
    }
}
