using System.Text;
using AssetRipper.SourceGenerated;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// A whole cabmap as the ordinary column table every other list is: one row per entry, with
/// the words a browser actually draws -- the name it is addressed by, every container path it
/// answers to joined for display, the classes it carries, the file it came out of and how many
/// other entries it pulls in.
///
/// This is where those words are DECIDED. A host that derived them itself would be a second
/// statement of the same thing, and the two drift the first time one of them is edited.
/// </summary>
public static class CabRows
{
    public const string Id = "cab.rows";

    /// <summary>How long the joined container display may get before it says "and N more"
    /// instead. A row addressed under hundreds of names is real; printing all of them is not
    /// a list any more.</summary>
    private const int MaxJoinChars = 16384;

    private const string JoinSeparator = "  |  ";
    private const string AssetBundleName = "AssetBundle";

    public static ColumnTable Table(CabTable map)
    {
        ArgumentNullException.ThrowIfNull(map);
        TableBuilder table = new(Id,
            "name|Name", "cab|Cab", "container|Container", "type_names|Type", "source|Source",
            "deps#|Deps");
        table.Role(ColumnRole.Label, "name", "cab")
            .Role(ColumnRole.Key | ColumnRole.Payload, "cab")
            .Role(ColumnRole.Detail, "type_names");
        Dictionary<int, string> classNames = [];
        for (int id = 0; id < map.Count; id++)
        {
            table.Row(CabFolders.Name(map, id), map.CabName(id), Container(map, id),
                TypeNames(map, id, classNames), map.RelativePath(id), map.DependencyCount(id));
        }
        return table.Build();
    }

    /// <summary>Every container path a row answers to, as ONE display string. A label, never a
    /// path: a caller that wants the folder an asset lives in asks the folder tree instead.</summary>
    public static string Container(CabTable map, int id)
    {
        int paths = map.ContainerPathCount(id);
        StringBuilder joined = new();
        for (int path = 0; path < paths; path++)
        {
            if (path > 0)
            {
                joined.Append(JoinSeparator);
            }
            ReadOnlySpan<byte> value = map.ContainerPathUtf8(id, path);
            if (joined.Length + value.Length > MaxJoinChars)
            {
                joined.Append('…').Append("(+").Append(paths - path).Append(" more names)");
                break;
            }
            joined.Append(Encoding.UTF8.GetString(value));
        }
        return joined.ToString();
    }

    /// <summary>What a row CARRIES, in the engine's own class names. The bundle class itself is
    /// not information -- every row has one -- so it is the answer only for a row that carries
    /// nothing else.</summary>
    public static string TypeNames(CabTable map, int id, Dictionary<int, string> names)
    {
        List<string> carried = [];
        foreach (int classId in map.ClassIds(id))
        {
            if (!names.TryGetValue(classId, out string? name))
            {
                name = Enum.IsDefined(typeof(ClassIDType), classId)
                    ? ((ClassIDType)classId).ToString()
                    : classId.ToString();
                names[classId] = name;
            }
            if (name != AssetBundleName)
            {
                carried.Add(name);
            }
        }
        return carried.Count == 0 ? AssetBundleName : string.Join(", ", carried);
    }
}
