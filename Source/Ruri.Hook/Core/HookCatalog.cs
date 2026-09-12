using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ruri.Hook.Attributes;

namespace Ruri.Hook.Core;

/// <summary>One game decoder compiled into this process.</summary>
public sealed class DecoderHook
{
    public DecoderHook(Type type, GameHookAttribute attribute)
    {
        Type = type;
        Product = attribute.GameName;
        Version = attribute.Version;
        EngineVersion = attribute.EngineVersion;
    }

    public Type Type { get; }

    /// <summary>The Unity productName of the builds this decodes -- the install's own word for itself.</summary>
    public string Product { get; }

    /// <summary>The bundleVersion this decoder applies FROM (see <see cref="HookCatalog.Resolve"/>).</summary>
    public string Version { get; }

    /// <summary>The Unity version those builds report.</summary>
    public string EngineVersion { get; }

    /// <summary>
    /// What a host names this decoder. A version is only there to order several decoders of the
    /// SAME product, so a product shipping exactly one states none and is named by itself alone:
    /// a made-up version reads as a version the build actually has, which it is not.
    /// </summary>
    public string Id => string.IsNullOrEmpty(Version) ? Product : Product + "_" + Version;

    public override string ToString() => Id;
}

/// <summary>One host capability compiled into this process.</summary>
public sealed class FeatureHook
{
    public FeatureHook(Type type, FeatureHookAttribute attribute)
    {
        Type = type;
        Name = attribute.Name;
    }

    public Type Type { get; }

    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The ONE place this process reflects over its own hooks. Scanning every assembly's every
/// type's every attribute is not free, and it used to run again on each list call and each
/// Initialize; here it runs once and is invalidated exactly when an assembly loads, which is
/// the only event that can change the answer.
///
/// It also holds the whole of "which hook reads this install": a decoder's Version is the
/// bundleVersion it applies FROM, so a build is decoded by the newest decoder at or below its
/// own version. That is one total order over data the hooks already declare -- no coverage
/// lists to extend per patch, and no host reimplementing "pick the newest".
/// </summary>
public static class HookCatalog
{
    private sealed class Snapshot
    {
        public List<DecoderHook> Decoders = new();
        public List<FeatureHook> Features = new();
        public Dictionary<string, DecoderHook> DecoderById = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FeatureHook> FeatureByName = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DecoderHook>> ByProduct = new(StringComparer.OrdinalIgnoreCase);
        public List<Type> EngineFileReaders = new();
        public List<Type> InstallProbes = new();
    }

    private static Snapshot? _snapshot;
    private static readonly object SyncRoot = new();

    static HookCatalog()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => Invalidate();
    }

    private static Type? hostFamily;

    /// <summary>
    /// The hook family THIS host runs: the <see cref="GameHookAttribute"/> subclass its decoders
    /// carry. Once declared, decoders of every other family stay out of the catalog -- an
    /// assembly can hold hooks for more than one host (FModelHook carries both FModel's own and
    /// the Unreal decoder RipperHook hosts load), and a host must never list, let alone apply,
    /// hooks written against another host's types. Features, readers and probes are not
    /// family-bound and are always cataloged.
    /// </summary>
    public static void DeclareHost(Type gameHookAttributeType)
    {
        ArgumentNullException.ThrowIfNull(gameHookAttributeType);
        if (!typeof(GameHookAttribute).IsAssignableFrom(gameHookAttributeType))
        {
            throw new ArgumentException($"{gameHookAttributeType.FullName} is not a {nameof(GameHookAttribute)}.", nameof(gameHookAttributeType));
        }
        if (hostFamily == gameHookAttributeType)
        {
            return;
        }
        hostFamily = gameHookAttributeType;
        Invalidate();
    }

    public static void Invalidate()
    {
        lock (SyncRoot)
        {
            _snapshot = null;
        }
    }

    public static IReadOnlyList<DecoderHook> Decoders => Current().Decoders;

    public static IReadOnlyList<FeatureHook> Features => Current().Features;

    /// <summary>Every product that ships a decoder, alphabetical.</summary>
    public static IReadOnlyList<string> Products =>
        Current().ByProduct.Keys.OrderBy(static product => product, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>One product's decoders, newest version first.</summary>
    public static IReadOnlyList<DecoderHook> VersionsOf(string product)
    {
        return Current().ByProduct.TryGetValue(product ?? string.Empty, out List<DecoderHook>? found)
            ? found
            : Array.Empty<DecoderHook>();
    }

    /// <summary>
    /// The decoder that reads this install: of <paramref name="product"/>'s decoders, the newest
    /// one that applies at or below the build's own <paramref name="gameVersion"/>, minus any
    /// whose declared engine version says it reads a different build generation. Null when the
    /// product ships no decoder at all (a plain Unity build needs none) or when every one of
    /// them is newer than this build.
    ///
    /// Both versions are what the install itself published (PlayerSettings.bundleVersion and its
    /// serialized header), and a decoder's own Version is the game version it applies FROM. An
    /// unknown on either side constrains nothing -- a build that states no version is read by the
    /// newest decoder, and a decoder not yet checked against a real install declares no engine.
    /// </summary>
    public static DecoderHook? Resolve(string product, string gameVersion, string engineVersion) =>
        Resolve(product, gameVersion, engineVersion, string.Empty);

    /// <summary>
    /// The same rule with the install's engine family as a second identity: a product that
    /// ships no decoder of its own is read by the decoder declared for its ENGINE (the
    /// family name is a GameType member exactly like a product name), so an engine whose
    /// every build is decoded one way needs one declaration, not one per title.
    /// </summary>
    public static DecoderHook? Resolve(string product, string gameVersion, string engineVersion, string engineFamily)
    {
        DecoderHook? byProduct = ResolveProduct(product, gameVersion, engineVersion);
        if (byProduct is not null)
        {
            return byProduct;
        }
        string family = engineFamily ?? string.Empty;
        if (family.Length == 0 || string.Equals(family, product, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return ResolveProduct(family, gameVersion, engineVersion);
    }

    private static DecoderHook? ResolveProduct(string product, string gameVersion, string engineVersion)
    {
        string wantedGame = gameVersion ?? string.Empty;
        string wantedEngine = engineVersion ?? string.Empty;
        foreach (DecoderHook decoder in VersionsOf(product))
        {
            if (wantedEngine.Length > 0 && decoder.EngineVersion.Length > 0 &&
                !string.Equals(decoder.EngineVersion, wantedEngine, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (wantedGame.Length > 0 && decoder.Version.Length > 0 &&
                VersionKey.Compare(decoder.Version, wantedGame) > 0)
            {
                continue;
            }
            return decoder;
        }
        return null;
    }

    public static DecoderHook? DecoderById(string hookId)
    {
        Current().DecoderById.TryGetValue(hookId ?? string.Empty, out DecoderHook? found);
        return found;
    }

    public static FeatureHook? FeatureByName(string name)
    {
        Current().FeatureByName.TryGetValue(name ?? string.Empty, out FeatureHook? found);
        return found;
    }

    public static bool IsFeature(string hookId) => FeatureByName(hookId) is not null;

    /// <summary>
    /// Every class that can undo a game's own transform on the files it publishes -- asked in
    /// turn when the generic parse fails. See <see cref="InstallVersionReaderAttribute"/>.
    /// </summary>
    public static IReadOnlyList<Type> EngineFileReaders => Current().EngineFileReaders;

    /// <summary>
    /// Every class that can read the identity of an install built on another engine -- asked
    /// alongside the generic Unity probe. See <see cref="InstallProbeAttribute"/>.
    /// </summary>
    public static IReadOnlyList<Type> InstallProbes => Current().InstallProbes;

    /// <summary>The implementation behind a hook id, decoder or feature alike.</summary>
    public static Type? TypeOf(string hookId)
    {
        return (Type?)DecoderById(hookId)?.Type ?? FeatureByName(hookId)?.Type;
    }

    private static Snapshot Current()
    {
        Snapshot? snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (SyncRoot)
        {
            _snapshot ??= Build();
            return _snapshot;
        }
    }

    private static Snapshot Build()
    {
        Snapshot snapshot = new();

        foreach (Type type in ScannableTypes())
        {
            object[] attributes;
            try
            {
                attributes = type.GetCustomAttributes(inherit: false);
            }
            catch
            {
                continue;
            }

            foreach (object attribute in attributes)
            {
                if (attribute is GameHookAttribute game)
                {
                    if (hostFamily is null || hostFamily.IsInstanceOfType(game))
                    {
                        snapshot.Decoders.Add(new DecoderHook(type, game));
                    }
                    break;
                }
                if (attribute is FeatureHookAttribute feature)
                {
                    snapshot.Features.Add(new FeatureHook(type, feature));
                    break;
                }
                if (attribute is InstallVersionReaderAttribute)
                {
                    snapshot.EngineFileReaders.Add(type);
                    break;
                }
                if (attribute is InstallProbeAttribute)
                {
                    snapshot.InstallProbes.Add(type);
                    break;
                }
            }
        }

        snapshot.Decoders.Sort(static (left, right) =>
        {
            int product = string.Compare(left.Product, right.Product, StringComparison.OrdinalIgnoreCase);
            return product != 0 ? product : VersionKey.Compare(left.Version, right.Version);
        });
        snapshot.Features.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        foreach (DecoderHook decoder in snapshot.Decoders)
        {
            if (snapshot.DecoderById.TryGetValue(decoder.Id, out DecoderHook? clash) && clash.Type != decoder.Type)
            {
                throw new InvalidOperationException(
                    $"[HookCatalog] decoder id '{decoder.Id}' is claimed by {clash.Type.FullName} and {decoder.Type.FullName}. "
                    + "A hook id is a product plus the version it applies from, and it must resolve to exactly one class.");
            }
            snapshot.DecoderById[decoder.Id] = decoder;

            if (!snapshot.ByProduct.TryGetValue(decoder.Product, out List<DecoderHook>? versions))
            {
                versions = new List<DecoderHook>();
                snapshot.ByProduct[decoder.Product] = versions;
            }
            versions.Add(decoder);
        }
        foreach (List<DecoderHook> versions in snapshot.ByProduct.Values)
        {
            versions.Reverse();
        }

        foreach (FeatureHook feature in snapshot.Features)
        {
            if (snapshot.FeatureByName.TryGetValue(feature.Name, out FeatureHook? clash) && clash.Type != feature.Type)
            {
                throw new InvalidOperationException(
                    $"[HookCatalog] feature '{feature.Name}' is claimed by {clash.Type.FullName} and {feature.Type.FullName}.");
            }
            snapshot.FeatureByName[feature.Name] = feature;
        }

        return snapshot;
    }

    private static IEnumerable<Type> ScannableTypes()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name is null ||
                name.StartsWith("System.", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WindowsBase", StringComparison.Ordinal) ||
                name.Equals("PresentationCore", StringComparison.Ordinal) ||
                name.Equals("PresentationFramework", StringComparison.Ordinal))
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(static type => type is not null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (type is not null)
                {
                    yield return type;
                }
            }
        }
    }
}
