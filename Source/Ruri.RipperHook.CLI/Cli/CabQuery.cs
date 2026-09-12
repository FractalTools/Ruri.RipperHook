using System.Text;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.CLI;

internal static class CabQuery
{
    private const int DefaultLimit = 200;

    private static readonly string[] DefaultFields =
        [CabPathQuery.ContainerField, CabPathQuery.CabField, CabPathQuery.TypeNamesField];

    internal static int RunPaths(CliOptions options, TextWriter output)
    {
        if (!Load(options, out CabTable table, out int failure))
        {
            return failure;
        }
        string[] fields = Fields(options.QueryFields, DefaultFields);
        foreach (string field in fields)
        {
            if (!CabPathQuery.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[Ruri.CLI] --cab-fields has no field '{field}'; known fields are {string.Join(", ", CabPathQuery.Fields)}.");
                return 1;
            }
        }

        FilterRule[] rules;
        try
        {
            rules = CabPathQuery.ParseRules(options.CabRules);
        }
        catch (ArgumentException bad)
        {
            Console.Error.WriteLine($"[Ruri.CLI] {bad.Message}");
            return 1;
        }

        CabPathRow[] rows = CabPathQuery.Rows(table, options.CabQuery ?? string.Empty, rules);
        int limit = options.QueryLimit == 0 ? rows.Length : Math.Min(options.QueryLimit, rows.Length);
        output.WriteLine(string.Join('\t', fields));
        StringBuilder line = new();
        for (int index = 0; index < limit; index++)
        {
            line.Clear();
            for (int field = 0; field < fields.Length; field++)
            {
                if (field > 0)
                {
                    line.Append('\t');
                }
                line.Append(CabPathQuery.Field(table, rows[index], fields[field]));
            }
            output.WriteLine(line.ToString());
        }
        Console.Error.WriteLine($"[Ruri.CLI] cab-query: {rows.Length} path row(s)"
            + $"{(limit < rows.Length ? $", printed {limit} (raise --query-limit, 0 = all)" : string.Empty)}"
            + $", via {table.Count}-entry map ({options.CabMapPath})");
        return rows.Length == 0 ? 4 : 0;
    }

    internal static int RunDataset(CliOptions options, TextWriter output)
    {
        if (options.DatasetId is not { Length: > 0 } id)
        {
            Console.Error.WriteLine("[Ruri.CLI] --data needs a dataset id (see --data-list).");
            return 1;
        }
        CabTable? table = null;
        if (options.CabMapPath is { Length: > 0 } && !Load(options, out table, out int failure))
        {
            return failure;
        }

        if (!TryNamedArgs(options, out string[] namedArgs))
        {
            return 1;
        }

        ColumnTable produced;
        try
        {
            (_, produced) = Datasets.Table(id, namedArgs, CancellationToken.None, table);
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine($"[Ruri.CLI] dataset '{id}' failed: {failed.GetType().Name}: {failed.Message}");
            Console.Error.WriteLine($"[Ruri.CLI] dataset stack:\n{failed}");
            return 1;
        }

        string[] fields = Fields(options.QueryFields, produced.Columns.Select(column => column.Name).ToArray());
        Column[] columns = new Column[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            try
            {
                columns[index] = produced[fields[index]];
            }
            catch (KeyNotFoundException)
            {
                Console.Error.WriteLine($"[Ruri.CLI] dataset '{id}' has no column '{fields[index]}'; it has: "
                    + $"{string.Join(", ", produced.Columns.Select(column => column.Name))}.");
                return 1;
            }
        }

        int limit = options.QueryLimit == 0 ? produced.RowCount : Math.Min(options.QueryLimit, produced.RowCount);
        output.WriteLine(string.Join('\t', fields));
        StringBuilder line = new();
        for (int row = 0; row < limit; row++)
        {
            line.Clear();
            for (int index = 0; index < columns.Length; index++)
            {
                if (index > 0)
                {
                    line.Append('\t');
                }
                line.Append(Cell(columns[index], row));
            }
            output.WriteLine(line.ToString());
        }
        Console.Error.WriteLine($"[Ruri.CLI] dataset {id}: {produced.RowCount} row(s)"
            + $"{(limit < produced.RowCount ? $", printed {limit} (raise --query-limit, 0 = all)" : string.Empty)}");
        return 0;
    }

    internal static int RunDatasetBlob(CliOptions options, string target)
    {
        if (options.DatasetId is not { Length: > 0 } id)
        {
            Console.Error.WriteLine("[Ruri.CLI] --data-out needs --data <id> (see --data-list).");
            return 1;
        }
        CabTable? table = null;
        if (options.CabMapPath is { Length: > 0 } && !Load(options, out table, out int failure))
        {
            return failure;
        }
        if (!TryNamedArgs(options, out string[] namedArgs))
        {
            return 1;
        }

        byte[] payload;
        try
        {
            payload = Datasets.Blob(id, namedArgs, CancellationToken.None, table);
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine($"[Ruri.CLI] dataset '{id}' failed: {failed.GetType().Name}: {failed.Message}");
            return 1;
        }

        string full = Path.GetFullPath(target);
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }
        File.WriteAllBytes(full, payload);
        Console.Error.WriteLine($"[Ruri.CLI] dataset {id}: wrote {payload.Length} byte(s) -> {full}");
        return 0;
    }

    private static bool TryNamedArgs(CliOptions options, out string[] namedArgs)
    {
        List<string> collected = [];
        foreach (string entry in options.DatasetArgs)
        {
            int split = entry.IndexOf('=');
            if (split < 1)
            {
                Console.Error.WriteLine($"[Ruri.CLI] --data-arg '{entry}' must be name=value.");
                namedArgs = [];
                return false;
            }
            collected.Add(entry[..split]);
            collected.Add(entry[(split + 1)..]);
        }
        namedArgs = collected.ToArray();
        return true;
    }

    internal static int RunDatasetList(TextWriter output)
    {
        output.WriteLine(string.Join('\t', "id", "role", "arguments", "description"));
        foreach (Datasets.Dataset dataset in Datasets.Available())
        {
            output.WriteLine(string.Join('\t', dataset.Id, dataset.Role.ToString(),
                Datasets.Signature(dataset), dataset.Description.Replace('\t', ' ')));
        }
        return 0;
    }

    private static bool Load(CliOptions options, out CabTable table, out int failure)
    {
        table = null!;
        failure = 0;
        if (options.CabMapPath is not { Length: > 0 } path)
        {
            Console.Error.WriteLine("[Ruri.CLI] this query reads a CABMap; pass --cab-map <file>.");
            failure = 1;
            return false;
        }
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[Ruri.CLI] CABMap not found: {path}");
            failure = 1;
            return false;
        }
        try
        {
            table = CabMap.LoadTable(path);
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine($"[Ruri.CLI] cannot load CABMap '{path}': {failed.GetType().Name}: {failed.Message}");
            failure = 1;
            return false;
        }
        return true;
    }

    private static string[] Fields(string[] declared, string[] fallback) =>
        declared.Length == 0
            ? fallback
            : declared.SelectMany(entry => entry.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToArray();

    private static string Cell(Column column, int row) => column switch
    {
        Utf8Column text => text.Text(row).Replace('\t', ' '),
        IntegerColumn integers => integers.Values[row].ToString(),
        RealColumn reals => Format(reals.Values[row]),
        BlobColumn blob => blob.Bytes(row).Length + " byte(s)",
        _ => string.Empty,
    };

    private static string Format(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString()
            : value.ToString("R");
}
