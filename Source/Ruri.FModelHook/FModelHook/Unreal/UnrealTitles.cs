using System.Reflection;
using AssetRipper.Import.Logging;
using CUE4Parse.UE4.Versions;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// Where a value an install is read with is PUBLISHED rather than typed: a document on the web
/// and the JSONPath into it that names the value. The same two fields FModel keeps per game
/// directory and addressed the same way, so an endpoint a user already tested there is stated
/// here unchanged. Read by <see cref="UnrealKeyring"/>, which says why not by FModel's own.
/// </summary>
public sealed record UnrealPublication(string Url, string Path);

/// <summary>
/// One Unreal TITLE: the facts about a build that cannot be read off the build itself.
///
/// The family decoder reads every Unreal install, and everything it needs -- the archive
/// folders, the engine version, the project name -- it reads off the disk. What it cannot read
/// is what a studio did to its own archives that the engine does not: which of CUE4Parse's
/// per-studio container dialects they were cooked with, where that studio publishes the keys
/// that open them, and where its reflection schema is. A title states exactly those, as data.
/// No reading, no branch, no behaviour -- everything a stock unencrypted build needs is in this
/// folder instead, and a title that needs nothing extra needs no row.
///
/// A row claims an install by files THE BUILD ships, never by a folder a user named:
/// <see cref="Project"/> is the project folder's own name (the folder holding
/// <c>Content/Paks</c>) and <see cref="Markers"/> are paths under it that must all exist. So one
/// row claims the install whether the user points at the launcher folder, the install folder or
/// the project folder.
/// </summary>
public sealed record UnrealTitle
{
    /// <summary>The name this build is known by -- what the install probe reports as its product.</summary>
    public required string Product { get; init; }

    /// <summary>The project folder's own name, or empty to claim any project folder the markers fit.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Paths under the project folder, all of which must exist for this row to claim the install.</summary>
    public required string[] Markers { get; init; }

    /// <summary>The container dialect CUE4Parse reads this studio's archives and packages with.</summary>
    public required EGame Game { get; init; }

    /// <summary>Where this build's archive keys are published, or null for a build that needs none.</summary>
    public UnrealPublication? Keys { get; init; }

    /// <summary>Where this build's reflection schema is published: a document naming the .usmap's own url and file name.</summary>
    public UnrealPublication? Mappings { get; init; }

    /// <summary>A path under the project folder whose text IS the build's own version, or empty when it publishes none there.</summary>
    public string VersionFile { get; init; } = string.Empty;

    /// <summary>
    /// The //UE5/Main version this build's skeletal mesh SECTIONS were cooked at, for a studio
    /// that backported that layout onto an older branch and ships packages declaring no custom
    /// versions at all. Null for a build whose sections are its own engine's, which is almost
    /// every build. Applied by <see cref="UnrealSerializationDialect"/>, and deliberately narrow:
    /// it says what the sections are, not what the whole cook is.
    /// </summary>
    public FUE5MainStreamObjectVersion.Type? SkeletalMeshSectionVersion { get; init; }

    /// <summary>
    /// Content paths this build keeps its characters under, as they read in a container path.
    /// Which folder a studio files its cast in is a fact about the build that nothing in the
    /// build states, the same kind of fact as <see cref="Markers"/> -- no character is named
    /// here, only the shelf they sit on, and a title that ships none states none.
    /// </summary>
    public string[] CharacterRoots { get; init; } = [];

    /// <summary>How much this row asks for before it claims an install -- how specific its claim is.</summary>
    public int Specificity => Markers.Length + (Project.Length > 0 ? 1 : 0);
}

/// <summary>
/// Marks a class that declares Unreal titles. It must expose
/// <c>public static IEnumerable&lt;UnrealTitle&gt; Titles()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UnrealTitlesAttribute : Attribute
{
}

/// <summary>
/// Marks a class that publishes datasets for ONE title. It must expose
/// <c>public static string Product</c> and <c>public static void Publish()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UnrealTitleDatasetsAttribute : Attribute
{
}

/// <summary>
/// What a title publishes INSTEAD of the family's own. The family decoder answers for every
/// Unreal build, and for most of them that is the whole story; a studio that changed the engine
/// enough that a family answer cannot be given -- its cast is not in Blueprint actors, its design
/// tables are not packages -- states its own here, and the registry's last word wins, so the
/// replacement is the ordinary way to publish and not a special case anyone has to gate on.
///
/// Only the title that CLAIMED the open install publishes: a build is one title, and two titles'
/// answers to the same question must never both be live.
/// </summary>
public static class UnrealTitleDatasets
{
    private const string PublishMethod = "Publish";
    private const string ProductProperty = "Product";

    private static readonly HashSet<string> Published = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void Forget()
    {
        lock (Gate)
        {
            Published.Clear();
        }
    }

    /// <summary>Let the claiming title replace what the family published, once per session.</summary>
    public static void PublishFor(string product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return;
        }
        lock (Gate)
        {
            if (!Published.Add(product))
            {
                return;
            }
        }
        foreach (Type type in typeof(UnrealTitleDatasets).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<UnrealTitleDatasetsAttribute>() is null)
            {
                continue;
            }
            PropertyInfo declaredProduct = type.GetProperty(ProductProperty, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"[Unreal] {type.FullName} declares [{nameof(UnrealTitleDatasetsAttribute)}] but has no public static string {ProductProperty}.");
            if (!string.Equals(declaredProduct.GetValue(null) as string, product, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            MethodInfo publish = type.GetMethod(PublishMethod, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"[Unreal] {type.FullName} declares [{nameof(UnrealTitleDatasetsAttribute)}] but has no public static void {PublishMethod}().");
            publish.Invoke(null, null);
            Logger.Info(LogCategory.Import, $"[Unreal] {product} publishes its own datasets.");
        }
    }
}

/// <summary>
/// Which title an install IS, and what that title publishes about it. Rows are declared where a
/// studio's container knowledge already lives -- the private game hooks compiled into this same
/// assembly -- and are found by attribute, so adding a title is a new file there and no edit
/// here.
/// </summary>
public static class UnrealTitles
{
    private const string TitlesMethod = "Titles";

    private static readonly Dictionary<string, Claim?> Claimed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static UnrealTitle[]? declared;

    /// <summary>A title, and the project folder of the install it claimed.</summary>
    public sealed record Claim(UnrealTitle Title, string ProjectFolder)
    {
        /// <summary>The build's own version as the title says to read it, or empty when it states none.</summary>
        public string Version
        {
            get
            {
                if (Title.VersionFile.Length == 0)
                {
                    return string.Empty;
                }
                string path = System.IO.Path.Combine(ProjectFolder, Title.VersionFile);
                try
                {
                    return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
                }
                catch (IOException)
                {
                    return string.Empty;
                }
            }
        }
    }

    /// <summary>Every declared title, most specific claim first.</summary>
    public static IReadOnlyList<UnrealTitle> All
    {
        get
        {
            lock (Gate)
            {
                return declared ??= Discover();
            }
        }
    }

    /// <summary>The title this install is, or null when no row claims it.</summary>
    public static UnrealTitle? For(string gameRoot) => Of(gameRoot)?.Title;

    /// <summary>The claim over this install -- the title, and the project folder it was recognised in.</summary>
    public static Claim? Of(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return null;
        }
        string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(gameRoot));
        lock (Gate)
        {
            if (Claimed.TryGetValue(root, out Claim? found))
            {
                return found;
            }
        }
        Claim? claim = Match(root);
        lock (Gate)
        {
            Claimed[root] = claim;
        }
        if (claim is not null)
        {
            string version = claim.Version;
            Logger.Info(LogCategory.Import,
                $"[Unreal] '{root}' is {claim.Title.Product} ({claim.Title.Game}), project "
                + $"'{System.IO.Path.GetFileName(claim.ProjectFolder)}'{(version.Length > 0 ? $", build {version}" : string.Empty)}.");
        }
        return claim;
    }

    private static Claim? Match(string root)
    {
        foreach (string pakFolder in UnrealInstall.PakFolders(root))
        {
            string? project = UnrealInstall.ProjectFolder(pakFolder);
            if (project is null)
            {
                continue;
            }
            foreach (UnrealTitle title in All)
            {
                if (Claims(title, project))
                {
                    return new Claim(title, project);
                }
            }
        }
        return null;
    }

    private static bool Claims(UnrealTitle title, string projectFolder)
    {
        if (title.Specificity == 0)
        {
            return false;
        }
        if (title.Project.Length > 0 &&
            !System.IO.Path.GetFileName(projectFolder).Equals(title.Project, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (string marker in title.Markers)
        {
            string path = System.IO.Path.Combine(projectFolder, marker);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
        }
        return true;
    }

    private static UnrealTitle[] Discover()
    {
        List<UnrealTitle> rows = new();
        foreach (Type type in typeof(UnrealTitles).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<UnrealTitlesAttribute>() is null)
            {
                continue;
            }
            MethodInfo method = type.GetMethod(TitlesMethod, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"[Unreal] {type.FullName} declares [{nameof(UnrealTitlesAttribute)}] but has no "
                    + $"public static IEnumerable<{nameof(UnrealTitle)}> {TitlesMethod}().");
            IEnumerable<UnrealTitle> declaredRows = method.Invoke(null, null) as IEnumerable<UnrealTitle>
                ?? throw new InvalidOperationException(
                    $"[Unreal] {type.FullName}.{TitlesMethod} must return IEnumerable<{nameof(UnrealTitle)}>.");
            rows.AddRange(declaredRows);
        }
        rows.Sort(static (left, right) => right.Specificity.CompareTo(left.Specificity));
        return rows.ToArray();
    }
}
