using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public static class CoreDatasets
{
    public const string IdPrefix = "core.";
    public const string DatasetsId = "core.datasets";
    public const string SessionId = "core.session";

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
