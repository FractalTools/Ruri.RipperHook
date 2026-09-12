using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// The designer-authored data a package holds, as rows. One reading, used by the dataset that
/// publishes it and by every title that joins its own cast out of the same tables -- a second
/// reading would be a second answer to "what does this table say".
///
/// A DataTable gives one row per entry of its row map; anything else no asset family claims -- a
/// DataAsset and whatever a game derives from one -- gives a single row of its own properties.
/// </summary>
public static class UnrealDataTables
{
    private const string GameRoot = "/Game/";
    private static readonly string BrowserRoot = UnrealPaths.AssetsRoot + "/";
    private const string EngineRoot = "/Engine/";
    private const string EngineFolder = "Engine";
    private const string ContentFolder = "Content";

    /// <summary>
    /// The key the mount lists a package under. The same package is spelled three ways around
    /// here -- a build states its own references as content paths with the object appended
    /// (<c>/Game/Path/Name.Name</c>), a browser row states it under the shared <c>Assets/</c>
    /// head, and the mount lists it by the project path that content root stands for -- so every
    /// caller normalises here and none keeps its own idea of what a package path looks like.
    ///
    /// The extension is the mount's to say. A stripped reference names no extension and a package
    /// is written under either of the engine's two, so the mount is asked which file the name
    /// stands for; only a name it holds no file for falls back to the spelling alone, which is
    /// what a caller's own "no such package" message then reports.
    /// </summary>
    public static string Key(AbstractFileProvider provider, string path)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string rooted = Rooted(provider, path);
        int slash = rooted.LastIndexOf('/');
        int dot = rooted.LastIndexOf('.');
        string stripped = dot > slash && !GameFile.UePackageExtensionsSet.Contains(rooted[(dot + 1)..])
            ? rooted[..dot]
            : rooted;
        return provider.TryGetGameFile(stripped, out GameFile? file) ? file.Path : provider.FixPath(stripped);
    }

    /// <summary>
    /// A content-root alias spelled as the mount lists it. Every reference a build states inside
    /// itself is written against the engine's own roots -- <c>/Game/</c> for the project's content,
    /// <c>/Engine/</c> for the engine's -- while the mount lists files by the folders those roots
    /// stand for. Nothing translated between the two, so following any reference a build states
    /// found no file at all.
    /// </summary>
    private static string Rooted(AbstractFileProvider provider, string path)
    {
        // The browser's own head. A cabmap row and every list drawn from one spell a package
        // under "Assets/" so every title's rows read alike; the mount never heard of it.
        if (path.StartsWith(BrowserRoot, StringComparison.OrdinalIgnoreCase))
        {
            path = path[BrowserRoot.Length..];
        }
        if (path.StartsWith(GameRoot, StringComparison.Ordinal))
        {
            string project = provider.ProjectName ?? string.Empty;
            return project.Length == 0 ? path : project + "/" + ContentFolder + "/" + path[GameRoot.Length..];
        }
        return path.StartsWith(EngineRoot, StringComparison.Ordinal)
            ? EngineFolder + "/" + ContentFolder + "/" + path[EngineRoot.Length..]
            : path;
    }

    /// <summary>One row: what states it, what it is called, and its fields by name.</summary>
    public readonly record struct Row(string Table, string Name, string Struct, Dictionary<string, string> Values);

    public static List<Row> Rows(UnrealFileProvider provider, string package)
    {
        ArgumentNullException.ThrowIfNull(provider);
        List<Row> rows = new();
        if (!provider.Files.TryGetValue(Key(provider, package), out GameFile? file))
        {
            return rows;
        }
        CUE4Parse.MappingsProvider.TypeMappings? mappings =
            provider.MappingsContainer is SchemalessMappingsProvider ? null : provider.MappingsForGame;
        foreach (UObject export in provider.LoadPackage(file).GetExports())
        {
            if (export is UDataTable table)
            {
                foreach ((FName name, FStructFallback row) in table.RowMap)
                {
                    rows.Add(new Row(table.Name, name.Text, table.RowStructName ?? string.Empty, Values(row.Properties)));
                }
                continue;
            }
            if (!UnrealClasses.IsValueObject(export.ExportType, mappings))
            {
                continue;
            }
            rows.Add(new Row(export.Name, export.Name, export.ExportType, Values(export.Properties)));
        }
        return rows;
    }

    /// <summary>
    /// One field of a row, found by the name a designer typed. The engine writes a user-defined
    /// struct's fields as <c>&lt;name&gt;_&lt;index&gt;_&lt;guid&gt;</c>, so the name a build
    /// states and the name a column carries are never equal and nothing may compare them whole.
    /// </summary>
    public static string Field(Dictionary<string, string> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.TryGetValue(name, out string? exact))
        {
            return exact;
        }
        string prefix = name + "_";
        foreach ((string column, string value) in values)
        {
            if (column.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Suffixed(column, prefix.Length))
            {
                return value;
            }
        }
        return string.Empty;
    }

    /// <summary>Whether what follows a field's name is the engine's own index-and-guid tail.</summary>
    private static bool Suffixed(string column, int at) =>
        at < column.Length && char.IsAsciiDigit(column[at]);

    private static Dictionary<string, string> Values(List<FPropertyTag> properties)
    {
        Dictionary<string, string> values = new(properties.Count, StringComparer.Ordinal);
        foreach (FPropertyTag tag in properties)
        {
            values[tag.Name.Text] = UnrealValues.Rendered(tag.Tag?.GenericValue);
        }
        return values;
    }
}
