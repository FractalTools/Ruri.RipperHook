namespace Ruri.RipperHook.Data;

public static class Session
{
    private static Func<string, string[]>? _layout;

    public static string GameRoot { get; private set; } = string.Empty;

    public static string[] ContentRoots { get; private set; } = [];

    public static string[] HookIds { get; private set; } = [];

    /// <summary>
    /// What the host stated about how to READ this install, beyond its folder: the values a
    /// source needs before it can open the files at all (an archive key, an engine version
    /// override, a schema file). The kernel carries them verbatim and interprets none; the
    /// decoder that opens the source reads the names it published a schema for.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Options { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Raised after <see cref="Options"/> changed. A source holding an open reader built from
    /// the previous values drops it here, so the next read opens with the new ones.
    /// </summary>
    public static event Action? OptionsChanged;

    public static void DeclareLayout(Func<string, string[]> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        Resolve();
    }

    public static void ForgetLayout()
    {
        _layout = null;
        Resolve();
    }

    /// <summary>
    /// Raised after a session opened on an install. A decoder that answers differently for the
    /// title it recognises replaces what it published here -- which install is open is a fact
    /// only the session has, and which title that install IS is a fact only a decoder has.
    /// </summary>
    public static event Action? Opened;

    public static void Open(string gameRoot, IEnumerable<string> hookIds)
    {
        GameRoot = (gameRoot ?? string.Empty).TrimEnd('/', '\\');
        HookIds = hookIds?.ToArray() ?? [];
        Resolve();
        Opened?.Invoke();
    }

    public static void SetOptions(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<string, string> copy = new(StringComparer.Ordinal);
        foreach ((string name, string value) in options)
        {
            copy[name] = value ?? string.Empty;
        }
        bool changed = copy.Count != Options.Count
            || copy.Any(pair => !Options.TryGetValue(pair.Key, out string? previous) || previous != pair.Value);
        Options = copy;
        if (changed)
        {
            // An option may name content to read, so the layout is asked again before anything
            // reads the roots: a source that widened its own tree here would otherwise keep
            // answering out of the roots resolved when the install was opened.
            Resolve();
            OptionsChanged?.Invoke();
        }
    }

    public static string Option(string name) =>
        Options.TryGetValue(name, out string? value) ? value : string.Empty;

    public static string[] RootsOrThrow(string datasetId)
    {
        if (ContentRoots.Length == 0)
        {
            throw new InvalidOperationException(
                $"dataset '{datasetId}' reads the install and no game root is open -- "
                + "Initialize(hookIds, gameRoot) with the folder the game is installed in.");
        }
        return ContentRoots;
    }

    private static void Resolve()
    {
        ContentRoots = GameRoot.Length == 0
            ? []
            : _layout is null ? [GameRoot] : _layout(GameRoot);
        Datasets.ClearCache();
    }
}
