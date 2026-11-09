using System.Runtime;
using AssetRipper.Import.Logging;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Install;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge;

/// <summary>
/// The kernel's face to a host: session control (which decoders exist, what an install is,
/// opening the session, building and loading cabmaps, the install slots, letting a placed
/// selection go), and the four verbs
/// every question is asked through -- a dataset as a table, a dataset as bytes, a view over
/// a table, and a search over one. Everything a selection IS arrives as a statement dataset
/// (<see cref="Statements.StatementDatasets"/>); nothing else crosses.
/// </summary>
public static class RipperBlenderBridge
{
    private static readonly ReaderWriterLockSlim HostLifecycle = new(LockRecursionPolicy.SupportsRecursion);

    private sealed class ReaderScope(bool transition) : IDisposable
    {
        private bool disposed;

        internal void Enter()
        {
            if (transition) HostLifecycle.EnterWriteLock();
            else HostLifecycle.EnterReadLock();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (transition) HostLifecycle.ExitWriteLock();
            else HostLifecycle.ExitReadLock();
        }
    }

    /// <summary>Readers may overlap; a hook/session transition waits for them to finish.</summary>
    internal static T WithReaderLifecycle<T>(Func<T> operation)
    {
        ReaderScope scope = new(false);
        scope.Enter();
        using (scope) return operation();
    }

    internal static void WithReaderLifecycle(Action operation)
        => WithReaderLifecycle(() => { operation(); return true; });

    /// <summary>Exclusive reader-state mutation, including synchronous nested install operations.</summary>
    internal static T WithReaderTransition<T>(Func<T> operation)
    {
        ReaderScope scope = new(true);
        scope.Enter();
        using (scope) return operation();
    }

    internal static void WithReaderTransition(Action operation)
        => WithReaderTransition(() => { operation(); return true; });

    private static T WithDatasetLifecycle<T>(string id, Func<Data.Datasets.Dataset, bool, T> operation)
    {
        while (true)
        {
            Data.Datasets.Dataset dataset = Data.Datasets.Definition(id);
            bool current = false;
            T Invoke()
            {
                if (!Data.Datasets.IsCurrent(dataset)) return default!;
                current = true;
                return operation(dataset, dataset.ReaderIndependent);
            }
            T answer = dataset.ReaderTransition ? WithReaderTransition(Invoke)
                : dataset.ReaderIndependent ? Invoke() : WithReaderLifecycle(Invoke);
            if (current) return answer;
        }
    }

    private static bool _loggingConfigured;
    /// <summary>
    /// The features THIS host runs with, always. They are not a user choice, because each one
    /// states something about the ASSETS this path hands over rather than about writing a
    /// project to disk: a Unity humanoid rig has no equivalent in any host here and must arrive
    /// as a generic one; Unity's static batching combines many objects into one shared mesh and
    /// leaves each renderer a window into it, which this reverses. A name that no feature claims
    /// is a build error at the first Initialize, not a silent no-op.
    /// </summary>
    public static readonly string[] HostFeatures = { "HumanoidToGeneric", "StaticMeshSeparation" };
    private static readonly List<Func<string, IEnumerable<string>>> HostFeatureProviders = [];

    /// <summary>Register source-owned optional features to carry when a decoder is opened.
    /// Providers return nothing for decoders they do not own.</summary>
    internal static void RegisterHostFeatureProvider(Func<string, IEnumerable<string>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!HostFeatureProviders.Contains(provider)) HostFeatureProviders.Add(provider);
    }

    /// <summary>
    /// Every decoder compiled in, as flat triples (product, version, engineVersion) -- what a
    /// host's decoder picker lists. Ordered by product then version, newest version first within
    /// a product.
    /// </summary>
    public static string[] ListDecoders()
    {
        List<string> flat = new();
        foreach (string product in HookCatalog.Products)
        {
            foreach (DecoderHook decoder in HookCatalog.VersionsOf(product))
            {
                flat.Add(decoder.Product);
                flat.Add(decoder.Version);
                flat.Add(decoder.EngineVersion);
            }
        }
        return flat.ToArray();
    }

    /// <summary>
    /// What the players under <paramref name="gameRoot"/> say they are, as flat septuples
    /// (dataFolder, company, product, gameVersion, engineVersion, engine, isProject). Reads two
    /// small files per player and nothing else -- see <see cref="InstallProbe"/>; an install on
    /// another engine is answered by that engine's own probe, which may need the source options
    /// already stated (<see cref="SetSourceOptions"/>) to open its archives.
    /// </summary>
    public static string[] ReadInstall(string gameRoot)
    {
        List<PlayerIdentity> players = InstallProbe.Read(gameRoot);
        PlayerIdentity? project = InstallProbe.Project(gameRoot);
        List<string> flat = new(players.Count * 7);
        foreach (PlayerIdentity player in players)
        {
            flat.Add(player.DataFolder);
            flat.Add(player.Company);
            flat.Add(player.Product);
            flat.Add(player.GameVersion);
            flat.Add(player.EngineVersion);
            flat.Add(player.Engine);
            flat.Add(project is not null && player.DataFolder == project.DataFolder ? "1" : "0");
        }
        return flat.ToArray();
    }

    /// <summary>
    /// The decoder id this install is read through, or "" when none applies (a plain Unity build
    /// needs no decoder). See <see cref="HookCatalog.Resolve(string, string, string, string)"/>.
    /// </summary>
    public static string ResolveDecoder(string product, string gameVersion, string engineVersion, string engineFamily) =>
        HookCatalog.Resolve(product, gameVersion, engineVersion, engineFamily)?.Id ?? string.Empty;

    /// <summary>
    /// State how the install is to be READ beyond its folder, as flat name/value pairs: the
    /// values a source needs before it can open its files (an archive key, an engine version,
    /// a schema file). Stated before <see cref="Initialize"/>, <see cref="ReadInstall"/> or
    /// <see cref="BuildCabMap"/>, and again whenever the host changes one. The kernel carries
    /// them verbatim (<see cref="Data.Session.Options"/>); the decoder publishes which names it
    /// reads as a dataset, so a host draws the form from that and never spells a name itself.
    /// </summary>
    public static void SetSourceOptions(string[] flatPairs)
    {
        ArgumentNullException.ThrowIfNull(flatPairs);
        if (flatPairs.Length % 2 != 0)
        {
            throw new ArgumentException("Source options are name/value pairs; got an odd number of strings.", nameof(flatPairs));
        }
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 0; index < flatPairs.Length; index += 2)
        {
            options[flatPairs[index]] = flatPairs[index + 1] ?? string.Empty;
        }
        WithReaderTransition(() => Data.Session.SetOptions(options));
    }

    /// <summary>
    /// State the language the host is showing its user, as that host names it ("en_US",
    /// "ja_JP", "zh_CN") -- see <see cref="Data.Session.Locale"/>. Pushed once when the host
    /// opens or its user switches language; pushing the same value again is free.
    /// </summary>
    public static void SetLocale(string locale) => Data.Session.SetLocale(locale);

    /// <summary>
    /// Load one more hook assembly -- a module built into another host's output, with its
    /// dependencies beside it -- so every decoder and install probe it declares answers
    /// <see cref="ListDecoders"/>, <see cref="ReadInstall"/> and <see cref="ResolveDecoder"/>
    /// from then on. A path that does not exist is an error, never a silent skip.
    /// </summary>
    public static void LoadModule(string assemblyPath) => Bootstrap.LoadModule(assemblyPath);

    /// <summary>Load every module the build declared beside the kernel (see <see cref="Bootstrap.LoadDeclaredModules"/>); the paths a host adds come on top.</summary>
    public static int LoadDeclaredModules() => Bootstrap.LoadDeclaredModules().Count;

    /// <summary>
    /// Open the session on ONE install through ONE decoder. The host states which install and
    /// which decoder; which features run is this host's own fact (<see cref="HostFeatures"/>),
    /// never part of that statement. An empty decoder id is a valid configuration: a plain
    /// un-bundled Unity build is read by the generic path.
    /// </summary>
    public static void Initialize(string decoderId, string gameRoot)
        => WithReaderTransition(() => InitializeCore(decoderId, gameRoot));

    private static void InitializeCore(string decoderId, string gameRoot)
    {
        Bootstrap.InstallAssemblyResolver();

        if (!_loggingConfigured)
        {
            _loggingConfigured = true;
            Logger.Clear();
            Logger.Add(new BridgeLogger { MinLevel = LogType.Info });
        }

        Data.CoreDatasets.Register();
        Data.Installs.Opener = Initialize;

        HookConfig config = new();
        IEnumerable<string> optional = HostFeatureProviders.SelectMany(provider => provider(decoderId) ?? []);
        foreach (string feature in HostFeatures.Concat(optional).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (HookCatalog.FeatureByName(feature) is null)
            {
                throw new InvalidOperationException(
                    $"[RipperBlenderBridge] Host feature '{feature}' names no RipperFeature in this build.");
            }
            config.EnabledHooks.Add(feature);
        }
        if (!string.IsNullOrEmpty(decoderId))
        {
            if (HookCatalog.DecoderById(decoderId) is null)
            {
                throw new InvalidOperationException(
                    $"[RipperBlenderBridge] '{decoderId}' names no decoder in this build. "
                    + $"Known: {string.Join(", ", HookCatalog.Decoders.Select(static decoder => decoder.Id))}.");
            }
            config.EnabledHooks.Add(decoderId);
        }
        HookConfig previous = new();
        previous.EnabledHooks.UnionWith(Ruri.Hook.RuriHook.AppliedHookIds);
        string previousRoot = Data.Session.GameRoot;
        try
        {
            Data.Session.Open(gameRoot ?? string.Empty, ApplyHooksChecked(config));
        }
        catch (Exception error)
        {
            try { Data.Session.Open(previousRoot, ApplyHooksChecked(previous)); }
            catch (Exception restoreError)
            {
                throw new AggregateException("Reader initialization and restoration both failed.", error, restoreError);
            }
            throw;
        }
    }

    /// <summary>Hook initialization failures are reported by the hook framework rather than thrown.
    /// A reader transition succeeds only when every requested hook actually became active.</summary>
    internal static string[] ApplyHooksChecked(HookConfig config)
    {
        string[] requested = config.EnabledHooks.ToArray();
        Bootstrap.ApplyHooks(config);
        string[] applied = Ruri.Hook.RuriHook.AppliedHookIds;
        string[] missing = requested.Except(applied, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Reader hooks failed to initialize: " + string.Join(", ", missing));
        return applied;
    }

    public static int BuildCabMap(string gameRoot, string outPath) => WithReaderLifecycle(() => CabMap.Build(gameRoot, outPath));

    public static CabMapHandle LoadCabMap(string cabMapPath) => new(cabMapPath, CabMap.LoadTable(cabMapPath));

    /// <summary>Open (or restate) one install slot: its folder, decoder and read options. The
    /// first slot opened becomes the active one; see <see cref="UseInstall"/>.</summary>
    public static void OpenInstall(string key, string gameRoot, string decoderId, string[] flatOptions)
        => WithReaderTransition(() =>
        {
            Data.Installs.Opener = Initialize;
            Data.Installs.Open(key, gameRoot, decoderId, flatOptions ?? []);
        });

    /// <summary>Load one install's CABMap once and return a handle over the exact
    /// table retained by that install slot. The host must keep this handle rather
    /// than separately loading the same file into a second CabTable instance.</summary>
    public static CabMapHandle LoadInstallCabMap(string key, string cabMapPath)
        => new(cabMapPath, Data.Installs.LoadCabMap(key, cabMapPath));

    /// <summary>A retained in-memory install map, through the same handle as a disk-loaded map.</summary>
    public static CabMapHandle GetInstallMap(string key) => new(string.Empty, Data.Installs.MapOf(key));

    public static string[] GetInstallOptions(string key) => Data.Installs.OptionsOf(key);

    /// <summary>Make one install the live one, re-opening the session onto its decoder and
    /// folder when they differ from what is active.</summary>
    public static void UseInstall(string key)
        => WithReaderTransition(() =>
        {
            Data.Installs.Opener = Initialize;
            Data.Installs.Use(key);
        });

    public static void RenameInstall(string oldKey, string newKey) => Data.Installs.Rename(oldKey, newKey);

    public static void CloseInstall(string key) => Data.Installs.Close(key);

    private static CabTable TableOf(CabMapHandle? map) =>
        map?.Table ?? Data.Installs.ActiveMap
        ?? throw new InvalidOperationException("no cabmap is loaded -- load one, or open an install with one.");

    private static CabTable? OptionalTableOf(CabMapHandle? map) => map?.Table ?? Data.Installs.ActiveMap;

    public static Data.PinnedTable GameDataTable(CabMapHandle? map, string datasetId, string[] args,
        CancellationToken cancellation)
        => WithDatasetLifecycle(datasetId, (dataset, independent) =>
        {
            (string handle, ColumnTable table) = Data.Datasets.Table(dataset, args ?? [], cancellation,
                independent ? map?.Table : OptionalTableOf(map));
            return Data.ColumnTablePacking.Pin(handle, table);
        });

    public static Views.View OpenView(string handle, string facet, string search, string[]? flatRules,
        string note, bool shippedOnly, string sortColumn, int sortDirection, int window,
        bool ordered, string labelColumn, string groupColumn) =>
        Views.View.Open(handle, facet, search, flatRules, note, shippedOnly, sortColumn,
            sortDirection, window, ordered, labelColumn, groupColumn);

    /// <summary>
    /// A dataset that answers with bytes. <paramref name="payload"/> is what the CALLER states
    /// and the install cannot -- a baked performance, a rig's own rest -- and is empty for every
    /// dataset that is purely a question about the install.
    /// </summary>
    public static byte[] GameDataBlob(CabMapHandle? map, string datasetId, string[] args, byte[]? payload,
        CancellationToken cancellation)
        => WithDatasetLifecycle(datasetId, (dataset, independent) => Data.Datasets.Blob(dataset, args ?? [], cancellation,
            independent ? map?.Table : OptionalTableOf(map), payload ?? []));

    /// <summary>
    /// The host has placed the selection these statement arguments asked about and is done with
    /// it: the flattening its tables were cut from is let go, and the memory handed back. A
    /// collection the runtime schedules on its own sweeps the same garbage but keeps the heap it
    /// grew committed for the next load, so the process the host runs in stays as large as its
    /// largest load until something else asks for the memory; the aggressive, compacting one
    /// returns it, once per load, when the host has nothing left to read.
    /// </summary>
    public static void ReleaseStatement(CabMapHandle? map, string[] args)
    {
        WithReaderTransition(() => Statements.StatementDatasets.Release(
            Data.Datasets.Request(Statements.StatementDatasets.RootsId, args ?? [], OptionalTableOf(map))));
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    public static byte[] SearchDataTable(string handle, string query, string[]? flatRules)
    {
        int[] rows = TableRegistry.Search(handle, query, RuleFilter.Parse(flatRules));
        byte[] bytes = new byte[rows.Length * sizeof(int)];
        Buffer.BlockCopy(rows, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static string OpenHostTable(string handle, string[] columns, int[]? roles, string[] flatValues)
        => TableRegistry.OpenHostTable(handle, columns, roles, flatValues);
}

public sealed class CabMapHandle
{
    private CabTableSearch? _search;

    public string CabMapPath { get; }
    public CabTable Table { get; }
    public string BaseFolder => Table.BaseFolder;

    public CabTableSearch Search => _search ??= CabTableSearch.For(Table);

    internal CabMapHandle(string cabMapPath, CabTable table)
    {
        CabMapPath = cabMapPath;
        Table = table;
    }
}
