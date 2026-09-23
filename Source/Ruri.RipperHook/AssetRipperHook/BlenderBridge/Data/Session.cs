namespace Ruri.RipperHook.BlenderBridge.Data;

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

    /// <summary>
    /// The language the HOST is showing its user, as that host names it ("en_US", "ja_JP",
    /// "zh_CN") -- the application's own UI language, never the machine's. A roster dataset
    /// that takes a locale argument reads this when the caller states none, so switching the
    /// host's language switches every roster it draws without the host naming it again per call.
    /// </summary>
    public static string Locale { get; private set; } = string.Empty;

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
            Datasets.ClearCache();
            OptionsChanged?.Invoke();
        }
    }

    /// <summary>
    /// How THIS game turns the host's locale into one of the languages it ships. Declared once
    /// by the game when it publishes its datasets, because the set of languages and the mapping
    /// are the game's facts; the locale is the host's. Nothing else may spell either half.
    /// </summary>
    public static void DeclareLanguage(Func<string, string> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _language = resolver;
    }

    /// <summary>The game language the host's current display language reads as, or "" when the
    /// active game declares no language rule.</summary>
    public static string Language => _language is null ? string.Empty : _language(Locale);

    private static Func<string, string>? _language;

    public static void SetLocale(string locale)
    {
        string stated = locale ?? string.Empty;
        if (string.Equals(stated, Locale, StringComparison.Ordinal))
        {
            return;
        }
        Locale = stated;
        Datasets.ClearCache();
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
