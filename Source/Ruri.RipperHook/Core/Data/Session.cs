namespace Ruri.RipperHook.Data;

public static class Session
{
    private static Func<string, string[]>? _layout;

    public static string GameRoot { get; private set; } = string.Empty;

    public static string[] ContentRoots { get; private set; } = [];

    public static string[] HookIds { get; private set; } = [];

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

    public static void Open(string gameRoot, IEnumerable<string> hookIds)
    {
        GameRoot = (gameRoot ?? string.Empty).TrimEnd('/', '\\');
        HookIds = hookIds?.ToArray() ?? [];
        Resolve();
    }

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
