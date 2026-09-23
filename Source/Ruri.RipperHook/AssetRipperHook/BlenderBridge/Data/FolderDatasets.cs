using System.Runtime.InteropServices;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>The loaded map as a virtual folder tree, and the map's rows as the one list a
/// host browses -- published as datasets so a host draws them through the same view every
/// other list gets and never walks the tree itself.</summary>
public static class FolderDatasets
{
    public const string RowsId = "core.rows";
    public const string ChildrenId = "core.folders.children";
    public const string FilesId = "core.folders.files";
    public const string OrderId = "core.rows.order";
    public const string OfId = "core.folders.of";
    public const string ExistsId = "core.folders.exists";

    public const string Folder = "folder";
    public const string Row = "row";
    public const string Query = "query";
    public const string Column = "column";
    public const string Direction = "direction";

    public static void Register()
    {
        Datasets.Publish(RowsId, DataRole.Selection, [],
            "Every row of the loaded map with its display words already decided -- the bundle browser's own list.",
            request => CabRows.Table(request.Map));
        Datasets.Publish(ChildrenId, DataRole.Internal, [DataParam.Text(Folder, required: false)],
            "One virtual folder's child folders, with how many rows live at or below each.", Children);
        Datasets.PublishBlob(FilesId, DataRole.Internal, [DataParam.Text(Folder, required: false)],
            "The rows listed IN one virtual folder, as little-endian int32 row ids -- a folder can hold "
            + "millions, and a list of numbers crosses as the numbers.", Files);
        Datasets.PublishBlob(OrderId, DataRole.Internal,
            [DataParam.Text(Column, required: false), DataParam.Integer(Direction, required: false)],
            "The row ids the payload carries (little-endian int32), in the order one display column puts "
            + "them -- direction 1 up, 2 down -- or in load order when no column is sorted.", Order);
        Datasets.Publish(OfId, DataRole.Internal,
            [DataParam.Integer(Row), DataParam.Text(Query, required: false), DataParam.Text(Folder, required: false)],
            "The folder one row is shown under and what it is called there -- the two questions "
            + "'jump to this row's folder' asks, answered together so they cannot disagree.", Of);
        Datasets.Publish(ExistsId, DataRole.Internal, [DataParam.Text(Folder, required: false)],
            "Whether a remembered folder still exists in THIS map.", Exists);
    }

    private static ColumnTable Children(DataRequest request)
    {
        (string[] names, int[] counts) = CabFolders.Of(request.Map).Children(CabFolders.Segments(request.Text(Folder)));
        TableBuilder table = new(ChildrenId, "name|Folder", "count#|Rows");
        table.Role(ColumnRole.Label | ColumnRole.Key, "name").Role(ColumnRole.Detail, "count");
        for (int index = 0; index < names.Length; index++)
        {
            table.Row(names[index], counts[index]);
        }
        return table.Build();
    }

    private static byte[] Files(DataRequest request) =>
        MemoryMarshal.AsBytes(CabFolders.Of(request.Map).Files(CabFolders.Segments(request.Text(Folder))).AsSpan())
            .ToArray();

    private static byte[] Order(DataRequest request)
    {
        int[] ids = MemoryMarshal.Cast<byte, int>(request.Payload.Span).ToArray();
        int[] ordered = CabTableSearch.For(request.Map).SortIds(ids,
            request.Given(Column) ? request.Text(Column) : string.Empty,
            request.Given(Direction) ? request.Integer(Direction) : 0);
        return MemoryMarshal.AsBytes(ordered.AsSpan()).ToArray();
    }

    private static ColumnTable Of(DataRequest request)
    {
        CabTable map = request.Map;
        int row = request.Integer(Row);
        string[] currentDir = CabFolders.Segments(request.Text(Folder));
        int path = CabFolders.BestPathIndex(map, row, request.Text(Query), currentDir);
        TableBuilder table = new(OfId, "folder", "leaf");
        table.Row(CabFolders.Joined(CabFolders.FolderOf(map, row, path)), CabFolders.LeafName(map, row, currentDir));
        return table.Build();
    }

    private static ColumnTable Exists(DataRequest request)
    {
        TableBuilder table = new(ExistsId, "exists#");
        table.Row(CabFolders.Of(request.Map).Has(CabFolders.Segments(request.Text(Folder))) ? 1 : 0);
        return table.Build();
    }
}
