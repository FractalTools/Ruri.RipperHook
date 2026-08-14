using Ruri.RipperHook.BundleExport;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public static class CoreDatasets
{
    public const string IdPrefix = "core.";
    public const string DatasetsId = "core.datasets";
    public const string SessionId = "core.session";
    public const string SelectId = "core.select";
    public const string DepsId = "core.deps";
    public const string BundlesId = "core.bundles";
    public const string BundleRawId = "core.bundle.raw";
    public const string BundleStandardId = "core.bundle.standard";

    public const string Query = "query";
    public const string Rule = "rule";
    public const string Direction = "direction";
    public const string Depth = "depth";
    public const string Closure = "closure";
    public const string Bundle = "bundle";

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
            "Addressable paths of the loaded map matching every rule, one row per (cab, path), "
            + "carrying the bundle and chunk that hold it. "
            + "A rule is field|relation|value[|exclude] over "
            + string.Join(", ", CabPathQuery.Fields) + ".",
            Select);

        Datasets.Publish(DepsId, DataRole.Selection,
            [
                DataParam.Text(Query, required: false), DataParam.List(Rule),
                DataParam.Text(Direction, required: false), DataParam.Integer(Depth, required: false),
            ],
            "Dependency edges reached from a selection. direction is "
            + string.Join("|", CabGraphQuery.Directions)
            + " (default forward); depth 0 or absent is one hop, a negative depth walks the whole closure.",
            Deps);

        Datasets.Publish(BundlesId, DataRole.Selection,
            [
                DataParam.Text(Query, required: false), DataParam.List(Rule),
                DataParam.Flag(Closure, required: false), DataParam.Text(Direction, required: false),
                DataParam.Integer(Depth, required: false),
            ],
            "The distinct bundles a selection lives in -- the minimal set to extract. "
            + "closure=1 widens it to everything the selection depends on.",
            Bundles);

        Datasets.PublishBlob(BundleRawId, DataRole.Payload, [DataParam.Text(Bundle)],
            "One bundle exactly as the game stores it, decrypted only as far as the container demands.",
            RawBundle);

        Datasets.PublishBlob(BundleStandardId, DataRole.Payload, [DataParam.Text(Bundle)],
            "One bundle repacked as a stock uncompressed UnityFS AssetBundle that vanilla readers accept. "
            + "Lossy: the original compression and any custom header obfuscation are dropped.",
            StandardBundle);
    }

    private static ColumnTable Select(DataRequest request)
    {
        CabTable map = request.Map;
        TableBuilder table = new(SelectId, CabPathQuery.CabField, CabPathQuery.ContainerField,
            CabPathQuery.FolderField, CabPathQuery.LeafField, CabPathQuery.StemField,
            CabPathQuery.ExtensionField, CabPathQuery.TypeNamesField, CabPathQuery.BundleField,
            CabPathQuery.SourceField, CabPathQuery.DependencyCountField + "#");
        foreach (CabPathRow row in Rows(request))
        {
            table.Row(map.CabName(row.CabId), CabPathQuery.Container(map, row),
                CabPathQuery.Field(map, row, CabPathQuery.FolderField),
                CabPathQuery.Field(map, row, CabPathQuery.LeafField),
                CabPathQuery.Field(map, row, CabPathQuery.StemField),
                CabPathQuery.Field(map, row, CabPathQuery.ExtensionField),
                CabPathQuery.Field(map, row, CabPathQuery.TypeNamesField),
                map.EntryFileName(row.CabId), map.RelativePath(row.CabId),
                map.DependencyCount(row.CabId));
        }
        return table.Build();
    }

    private static ColumnTable Deps(DataRequest request)
    {
        CabTable map = request.Map;
        CabEdge[] edges = CabGraphQuery.Walk(map, Seeds(request),
            CabGraphQuery.ParseDirection(request.Text(Direction)), request.Integer(Depth));
        TableBuilder table = new(DepsId, "from_cab", "cab", "depth#", "direction",
            CabPathQuery.ContainerField, CabPathQuery.TypeNamesField, CabPathQuery.BundleField,
            CabPathQuery.SourceField);
        foreach (CabEdge edge in edges)
        {
            table.Row(map.CabName(edge.FromCabId), map.CabName(edge.ToCabId), edge.Depth,
                edge.Direction.ToString().ToLowerInvariant(),
                Field(map, edge.ToCabId, CabPathQuery.ContainerField),
                Field(map, edge.ToCabId, CabPathQuery.TypeNamesField),
                map.EntryFileName(edge.ToCabId), map.RelativePath(edge.ToCabId));
        }
        return table.Build();
    }

    private static ColumnTable Bundles(DataRequest request)
    {
        CabTable map = request.Map;
        int[] cabs = Seeds(request);
        if (request.Flag(Closure))
        {
            cabs = CabGraphQuery.Closure(map, cabs,
                CabGraphQuery.ParseDirection(request.Text(Direction)), request.Integer(Depth));
        }

        Dictionary<string, (string Source, int Cabs, string Sample)> grouped = new(StringComparer.Ordinal);
        foreach (int cab in cabs)
        {
            string bundle = map.EntryFileName(cab);
            if (grouped.TryGetValue(bundle, out (string Source, int Cabs, string Sample) entry))
            {
                grouped[bundle] = (entry.Source, entry.Cabs + 1, entry.Sample);
            }
            else
            {
                grouped[bundle] = (map.RelativePath(cab), 1, Field(map, cab, CabPathQuery.ContainerField));
            }
        }

        TableBuilder table = new(BundlesId, CabPathQuery.BundleField, CabPathQuery.SourceField, "cabs#",
            "sample");
        foreach ((string bundle, (string source, int count, string sample)) in
                 grouped.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            table.Row(bundle, source, count, sample);
        }
        return table.Build();
    }

    private static byte[] RawBundle(DataRequest request) => Fetch(request.Text(Bundle), BundleRawId);

    private static byte[] StandardBundle(DataRequest request)
    {
        string bundle = request.Text(Bundle);
        return BundleRepack.ToStandardBundle(Fetch(bundle, BundleStandardId), bundle).Data;
    }

    private static byte[] Fetch(string bundle, string datasetId)
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            throw new ArgumentException($"dataset '{datasetId}' needs a bundle path, for example one that "
                + $"'{BundlesId}' listed.");
        }
        if (GameBundleHook.ExtractVfsFile is { } extract)
        {
            return extract(Session.RootsOrThrow(datasetId), bundle);
        }
        foreach (string root in Roots())
        {
            string candidate = Path.Combine(root, bundle);
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }
        throw new FileNotFoundException(
            $"dataset '{datasetId}' cannot find bundle '{bundle}' under any content root, and the active "
            + "hook publishes no archive extractor.", bundle);
    }

    private static IEnumerable<string> Roots()
    {
        foreach (string root in Session.ContentRoots)
        {
            yield return root;
        }
        if (Session.GameRoot is { Length: > 0 } game)
        {
            yield return game;
        }
    }

    private static CabPathRow[] Rows(DataRequest request) =>
        CabPathQuery.Rows(request.Map, request.Text(Query), CabPathQuery.ParseRules(request.List(Rule)));

    private static int[] Seeds(DataRequest request)
    {
        HashSet<int> seeds = [];
        foreach (CabPathRow row in Rows(request))
        {
            seeds.Add(row.CabId);
        }
        int[] ordered = seeds.ToArray();
        Array.Sort(ordered);
        return ordered;
    }

    private static string Field(CabTable map, int cabId, string field) =>
        CabTableSearch.For(map).Field(cabId, field);

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
