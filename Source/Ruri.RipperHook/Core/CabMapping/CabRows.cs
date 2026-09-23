using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
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

    /// <summary>How long, in bytes, the joined container display may get before it says "and N
    /// more" instead. A row addressed under hundreds of names is real; printing all of them is not
    /// a list any more.</summary>
    private const int MaxJoinBytes = 16384;

    /// <summary>The longest "...(+N more names)" an int can make.</summary>
    private const int MaxMoreBytes = 40;

    private static ReadOnlySpan<byte> JoinSeparator => "  |  "u8;

    private static ReadOnlySpan<byte> TypeSeparator => ", "u8;

    private static ReadOnlySpan<byte> AssetBundleName => "AssetBundle"u8;

    /// <summary>The fewest rows worth a piece of their own: below this, handing a piece to another
    /// core costs more than building it.</summary>
    private const int RowsPerPiece = 4096;

    /// <summary>Every row, built on every core: the rows are independent, so each core builds a
    /// piece by the same rules one row always followed, and the pieces are joined column by
    /// column -- a map of millions of rows is otherwise one core decoding and re-encoding five
    /// strings per row while the load waits.</summary>
    public static ColumnTable Table(CabTable map)
    {
        ArgumentNullException.ThrowIfNull(map);
        int count = map.Count;
        int pieces = Math.Clamp(count / RowsPerPiece, 1, Environment.ProcessorCount * 4);
        ColumnTable[] built = new ColumnTable[pieces];
        Parallel.For(0, pieces, piece => built[piece] = Piece(map,
            (int)((long)count * piece / pieces), (int)((long)count * (piece + 1) / pieces)));
        return ColumnTable.Concatenate(Id, built);
    }

    /// <summary>One piece of rows, each word written straight into its column's bytes.</summary>
    private static ColumnTable Piece(CabTable map, int start, int end)
    {
        int rows = end - start;
        ColumnBuilder names = new(ColumnKind.Text, rows);
        ColumnBuilder cabs = new(ColumnKind.Text, rows);
        ColumnBuilder containers = new(ColumnKind.Text, rows);
        ColumnBuilder types = new(ColumnKind.Text, rows);
        ColumnBuilder sources = new(ColumnKind.Text, rows);
        ColumnBuilder dependencies = new(ColumnKind.Real, rows);
        Dictionary<int, byte[]> classNames = [];
        ArrayBufferWriter<byte> scratch = new(256);
        for (int id = start; id < end; id++)
        {
            scratch.ResetWrittenCount();
            CabFolders.WriteName(map, id, scratch);
            names.Add(scratch.WrittenSpan);
            cabs.Add(map.CabNameUtf8(id));
            scratch.ResetWrittenCount();
            WriteContainer(map, id, scratch);
            containers.Add(scratch.WrittenSpan);
            scratch.ResetWrittenCount();
            WriteTypeNames(map, id, classNames, scratch);
            types.Add(scratch.WrittenSpan);
            int file = map.FileIndex[id];
            sources.Add(file < 0 ? ReadOnlySpan<byte>.Empty : map.DistinctFileUtf8(file));
            dependencies.Add((double)map.DependencyCount(id));
        }
        return new ColumnTable
        {
            Name = Id,
            RowCount = rows,
            Columns =
            [
                names.Build("name", ColumnRole.Label, "Name"),
                cabs.Build("cab", ColumnRole.Label | ColumnRole.Key | ColumnRole.Payload, "Cab"),
                containers.Build("container", ColumnRole.None, "Container"),
                types.Build("type_names", ColumnRole.Detail, "Type"),
                sources.Build("source", ColumnRole.None, "Source"),
                dependencies.Build("deps", ColumnRole.None, "Deps"),
            ],
        };
    }

    /// <summary>Every container path a row answers to, as ONE display string. A label, never a
    /// path: a caller that wants the folder an asset lives in asks the folder tree instead.</summary>
    private static void WriteContainer(CabTable map, int id, ArrayBufferWriter<byte> into)
    {
        int paths = map.ContainerPathCount(id);
        int start = into.WrittenCount;
        for (int path = 0; path < paths; path++)
        {
            if (path > 0)
            {
                into.Write(JoinSeparator);
            }
            ReadOnlySpan<byte> value = map.ContainerPathUtf8(id, path);
            if (into.WrittenCount - start + value.Length > MaxJoinBytes)
            {
                Utf8.TryWrite(into.GetSpan(MaxMoreBytes), CultureInfo.InvariantCulture,
                    $"…(+{paths - path} more names)", out int written);
                into.Advance(written);
                break;
            }
            into.Write(value);
        }
    }

    /// <summary>What a row CARRIES, in the engine's own class names. The bundle class itself is
    /// not information -- every row has one -- so it is the answer only for a row that carries
    /// nothing else.</summary>
    private static void WriteTypeNames(CabTable map, int id, Dictionary<int, byte[]> names,
        ArrayBufferWriter<byte> into)
    {
        bool carried = false;
        foreach (int classId in map.ClassIds(id))
        {
            if (classId == (int)ClassIDType.AssetBundle)
            {
                continue;
            }
            if (!names.TryGetValue(classId, out byte[]? name))
            {
                name = Encoding.UTF8.GetBytes(Enum.IsDefined(typeof(ClassIDType), classId)
                    ? ((ClassIDType)classId).ToString()
                    : classId.ToString(CultureInfo.InvariantCulture));
                names[classId] = name;
            }
            if (carried)
            {
                into.Write(TypeSeparator);
            }
            into.Write(name);
            carried = true;
        }
        if (!carried)
        {
            into.Write(AssetBundleName);
        }
    }
}
