using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public static class CoreDatasets
{
    public const string IdPrefix = "core.";
    public const string DatasetsId = "core.datasets";
    public const string SessionId = "core.session";
    public const string SelectId = "core.select";

    public const string Query = "query";
    public const string Rule = "rule";

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;

        Datasets.Publish(DatasetsId, DataRole.Introspection, [],
            "Every dataset this session publishes, with the role a host binds it by.", Published);

        Datasets.Publish(SessionId, DataRole.Session, [],
            "What this session is open on: the install, the content roots it resolved, the hooks applied.",
            SessionState);

        Datasets.Publish(SelectId, DataRole.Selection,
            [DataParam.Text(Query, required: false), DataParam.List(Rule)],
            "Addressable paths of the loaded map matching every rule, one row per (cab, path). "
            + "A rule is field|relation|value[|exclude] over "
            + string.Join(", ", CabPathQuery.Fields) + ".",
            Select);
    }

    private static ColumnTable Select(DataRequest request)
    {
        CabTable map = request.Map;
        TableBuilder table = new(SelectId, CabPathQuery.CabField, CabPathQuery.ContainerField,
            CabPathQuery.FolderField, CabPathQuery.LeafField, CabPathQuery.StemField,
            CabPathQuery.ExtensionField, CabPathQuery.TypeNamesField);
        foreach (CabPathRow row in CabPathQuery.Rows(map, request.Text(Query),
                     CabPathQuery.ParseRules(request.List(Rule))))
        {
            table.Row(map.CabName(row.CabId), CabPathQuery.Container(map, row),
                CabPathQuery.Field(map, row, CabPathQuery.FolderField),
                CabPathQuery.Field(map, row, CabPathQuery.LeafField),
                CabPathQuery.Field(map, row, CabPathQuery.StemField),
                CabPathQuery.Field(map, row, CabPathQuery.ExtensionField),
                CabPathQuery.Field(map, row, CabPathQuery.TypeNamesField));
        }
        return table.Build();
    }

    private static ColumnTable Published(DataRequest request)
    {
        TableBuilder table = new(DatasetsId, "id", "role", "kind", "parameters", "description");
        foreach (Datasets.Dataset dataset in Datasets.Available())
        {
            table.Row(dataset.Id, dataset.Role.ToString(), dataset.Blob is null ? "table" : "blob",
                Datasets.Signature(dataset), dataset.Description);
        }
        return table.Build();
    }

    private static ColumnTable SessionState(DataRequest request)
    {
        TableBuilder table = new(SessionId, "key", "value");
        table.Row("gameRoot", Session.GameRoot);
        foreach (string root in Session.ContentRoots)
        {
            table.Row("contentRoot", root);
        }
        foreach (string hook in Session.HookIds)
        {
            table.Row("hook", hook);
        }
        return table.Build();
    }
}
