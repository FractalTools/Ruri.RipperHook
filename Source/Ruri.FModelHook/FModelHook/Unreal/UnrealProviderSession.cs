using AssetRipper.Import.Logging;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures.BC;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// The ONE mounted CUE4Parse provider of the session: every archive under the install's pak
/// folders (plus the extra folders stated), opened with the stated keys, versioned the way the
/// stated options say, its property schema the stated mappings. Built on first use, kept while
/// the install and the options stay what they were, dropped the moment either changes -- a
/// provider mounted under yesterday's key is not a provider for today's. The install is
/// known by its full path without a trailing separator, however a caller spelled it.
/// </summary>
public static class UnrealProviderSession
{
    private static readonly object Gate = new();
    private static UnrealFileProvider? provider;
    private static string fingerprint = string.Empty;

    static UnrealProviderSession()
    {
        Session.OptionsChanged += Close;
    }

    public static bool IsOpen => provider is not null;

    public static UnrealFileProvider Current => Open(Session.GameRoot);

    public static UnrealFileProvider Open(string gameRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        UnrealTitle? title = UnrealTitles.For(root);
        string wanted = root + "\n" + UnrealSourceOptions.Fingerprint(title);
        lock (Gate)
        {
            if (provider is not null && fingerprint == wanted)
            {
                return provider;
            }
            provider?.Dispose();
            provider = null;
            provider = Mount(root, title);
            fingerprint = wanted;
            return provider;
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            provider?.Dispose();
            provider = null;
            fingerprint = string.Empty;
        }
    }

    private static bool codecsReady;

    /// <summary>
    /// The native codecs CUE4Parse decompresses and decodes through: Oodle for archive blocks,
    /// zlib-ng for the older ones, Detex for block-compressed texels.
    ///
    /// A native library has to BE a file -- the loader maps it by path -- so this is the one
    /// thing the Unreal lane cannot keep in memory. It is a DEPENDENCY rather than a cache, so
    /// it lands beside the reader that loads it, next to the CUE4Parse natives that already ship
    /// there, and never in a cache folder for somebody to wonder about later. Oodle is asked of
    /// those bundled natives FIRST (passing no path is what makes CUE4Parse look there), so the
    /// common case fetches nothing at all; only a build whose natives lack the feature falls
    /// through to the release each reader tracks.
    ///
    /// The codecs option still names a folder for a machine that keeps them elsewhere. Natives
    /// bind once per process, so this runs once; a codec that cannot be had is named and the
    /// rest stay usable.
    /// </summary>
    private static void EnsureCodecs()
    {
        if (codecsReady)
        {
            return;
        }
        codecsReady = true;
        string stated = UnrealSourceOptions.Text(UnrealSourceOptions.Codecs);
        string folder = stated.Length > 0
            ? Path.GetFullPath(stated)
            : Path.GetDirectoryName(typeof(CUE4Parse.Utils.CUE4ParseNatives).Assembly.Location) ?? AppContext.BaseDirectory;
        LoadCodec("Oodle", () => OodleHelper.Initialize(
            CUE4Parse.Utils.CUE4ParseNatives.IsFeatureAvailable("Oodle\0"u8) ? null : Path.Combine(folder, OodleHelper.OodleFileName)));
        LoadCodec("zlib-ng", () => ZlibHelper.Initialize(Path.Combine(folder, ZlibHelper.DllName)));
        LoadCodec("Detex", () =>
        {
            string path = Path.Combine(folder, DetexHelper.DLL_NAME);
            DetexHelper.LoadDll(path);
            DetexHelper.Initialize(path);
        });
    }

    private static void LoadCodec(string codec, Action initialize)
    {
        try
        {
            initialize();
            Logger.Info(LogCategory.Import, $"[Unreal] {codec} codec ready.");
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import,
                $"[Unreal] {codec} codec could not be loaded ({exception.Message}); "
                + $"point the '{UnrealSourceOptions.Codecs}' option at a folder that holds it.");
        }
    }

    private static UnrealFileProvider Mount(string gameRoot, UnrealTitle? title)
    {
        EnsureCodecs();
        string[] pakFolders = UnrealInstall.PakFolders(gameRoot);
        if (pakFolders.Length == 0)
        {
            throw new DirectoryNotFoundException($"[Unreal] No Paks folder holding .pak/.utoc archives under '{gameRoot}'.");
        }
        string engineVersion = UnrealInstall.EngineVersion(pakFolders[0]);
        EGame game = UnrealSourceOptions.EngineChoice(title)
            ?? UnrealInstall.EngineFromVersion(engineVersion)
            ?? throw new InvalidOperationException(
                $"[Unreal] The executable of '{pakFolders[0]}' carries no '++UE<major>+Release-<major>.<minor>' build literal, so which engine cooked it is unknown: "
                + $"state it with the '{UnrealSourceOptions.Engine}' option (Load Options Form).");
        if (engineVersion.Length == 0)
        {
            engineVersion = UnrealInstall.EngineVersion(game);
        }

        VersionContainer versions = new(
            game: game,
            platform: UnrealSourceOptions.TexturePlatformChoice(),
            customVersions: UnrealSourceOptions.CustomVersionContainer(),
            optionOverrides: UnrealSourceOptions.OptionOverrideTable(),
            mapStructTypesOverrides: UnrealSourceOptions.MapStructTypeTable());

        DirectoryInfo[] extra = UnrealSourceOptions.ExtraDirectoryList()
            .Concat(pakFolders.Skip(1))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => new DirectoryInfo(path))
            .ToArray();
        UnrealFileProvider mounted = new(new DirectoryInfo(pakFolders[0]), extra, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
        mounted.ReadScriptData = false;
        mounted.ReadShaderMaps = UnrealSourceOptions.Flag(UnrealSourceOptions.ReadShaderMaps) || UnrealSourceOptions.Flag(UnrealSourceOptions.MaterialSemantics);
        mounted.ReadNaniteData = true;
        mounted.Initialize();
        mounted.Mount();
        List<KeyValuePair<CUE4Parse.UE4.Objects.Core.Misc.FGuid, CUE4Parse.Encryption.Aes.FAesKey>> keys = UnrealSourceOptions.AesKeys(title).ToList();
        if (keys.Count > 0)
        {
            mounted.SubmitKeys(keys);
        }
        mounted.PostMount();
        SubmitPublishedKeysAgain(mounted, title);

        string mappings = UnrealSourceOptions.Text(UnrealSourceOptions.Mappings, title);
        if (mappings.Length > 0)
        {
            if (!File.Exists(mappings))
            {
                throw new FileNotFoundException($"[Unreal] The '{UnrealSourceOptions.Mappings}' option names a .usmap that does not exist.", mappings);
            }
            mounted.MappingsContainer = new FileUsmapTypeMappingsProvider(mappings);
        }
        else if (UnrealKeyring.Mappings(title) is { Provider: not null } published)
        {
            mounted.MappingsContainer = published.Provider;
        }
        else
        {
            mounted.MappingsContainer = new SchemalessMappingsProvider();
            Logger.Warning(LogCategory.Import,
                $"[Unreal] No reflection schema stated ('{UnrealSourceOptions.Mappings}'): package headers read, but no object with unversioned properties can be converted until a .usmap is given.");
        }
        mounted.LoadVirtualPaths();

        Logger.Info(LogCategory.Import,
            $"[Unreal] Mounted {mounted.MountedVfs.Count}/{mounted.MountedVfs.Count + mounted.UnloadedVfs.Count} archives of '{mounted.ProjectName}' "
            + $"({game}, engine {engineVersion}) files={mounted.Files.Count} mappings={(mounted.MappingsForGame?.Types.Count ?? 0)} structs "
            + $"missingKeys={mounted.RequiredKeys.Count}");
        return mounted;
    }

    /// <summary>
    /// Archives still waiting for a key after the kept set was submitted mean the set is stale:
    /// the build was patched and re-keyed since it was fetched. That -- not a version string --
    /// is the honest test, so it is the one that asks the title to publish again. Asked once per
    /// mount, and only for a build whose keys are published at all.
    /// </summary>
    private static void SubmitPublishedKeysAgain(UnrealFileProvider mounted, UnrealTitle? title)
    {
        if (title?.Keys is null || mounted.RequiredKeys.Count == 0)
        {
            return;
        }
        int locked = mounted.RequiredKeys.Count;
        Logger.Info(LogCategory.Import,
            $"[Unreal] {title.Product}: {locked} archive(s) still locked; asking '{title.Keys.Url}' again.");
        UnrealKeyring.Keyset refreshed = UnrealKeyring.Keys(title, refresh: true);
        if (refreshed.Keys.Count == 0)
        {
            return;
        }
        mounted.SubmitKeys(refreshed.Keys);
        mounted.PostMount();
        Logger.Info(LogCategory.Import,
            $"[Unreal] {title.Product}: {locked - mounted.RequiredKeys.Count} of those opened, {mounted.RequiredKeys.Count} still locked.");
    }
}
