using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.Tpk;
using AssetRipper.Tpk.TypeTrees;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// The runtime type tree source. Replaces the generated <c>Ruri.SourceGenerated</c> assembly: instead
/// of baking one C# class per (class, engine version) ahead of time, the dumped type trees ship in a
/// stock tpk and are interpreted here.
///
/// The tpk is a <c>TpkCollectionBlob</c> holding one <c>TpkTypeTreeBlob</c> per lineage plus a
/// <c>TpkJsonBlob</c> manifest (see <see cref="TypeTreeManifest"/>) that maps free-form version
/// strings to the ordinals used inside each lineage's blob. Both blob kinds are stock tpk types, so
/// the container format is untouched.
///
/// Resolution order for the file:
/// <list type="number">
/// <item>the path in <c>RURI_TYPE_TREE_TPK</c> (iteration escape hatch -- point it at a freshly built tpk without rebuilding),</item>
/// <item><c>RuriTypeTree.tpk</c> next to the running assembly,</item>
/// <item>the <c>RuriTypeTree.tpk</c> embedded resource in this assembly.</item>
/// </list>
/// </summary>
public static class TypeTreeDatabase
{
    public const string ResourceName = "RuriTypeTree.tpk";
    public const string PathEnvironmentVariable = "RURI_TYPE_TREE_TPK";

    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<(int ClassID, string Lineage, string Version), TypeTreeNode?> ReleaseRootCache = new();

    private static TypeTreeManifest? _manifest;
    private static Dictionary<string, Lineage>? _lineages;
    private static string _origin = "<unloaded>";

    private sealed class Lineage
    {
        public required TpkTypeTreeBlob Blob;
        public required Dictionary<int, TpkClassInformation> ClassesById;
    }

    public static string Origin
    {
        get
        {
            EnsureLoaded();
            return _origin;
        }
    }

    public static TypeTreeManifest Manifest
    {
        get
        {
            EnsureLoaded();
            return _manifest!;
        }
    }

    /// <summary>
    /// The Unity version a snapshot reports about itself. A fork keeps its own build here
    /// (EndField's dumps say <c>2021.3.34f5</c>), so the stock AssetRipper class the loader
    /// instantiates is chosen from what the dump states rather than from a hand-written guess at the
    /// engine it was forked from.
    /// </summary>
    public static UnityVersion GetEngineVersion(TypeTreeVersion version)
    {
        EnsureLoaded();

        string? engine = _manifest!.GetEngine(version.Lineage, version.Version);
        if (string.IsNullOrEmpty(engine))
        {
            throw new InvalidOperationException(
                $"[TypeTreeDatabase] {version} declares no engine version in {_origin}. Repack the tpk with Ruri.Tpk.");
        }

        return UnityVersion.Parse(engine);
    }

    /// <summary>
    /// Resolves the release (build) type tree for <paramref name="classID"/> at
    /// <paramref name="version"/>, or <see langword="null"/> when that lineage carries no definition.
    ///
    /// The lookup stays inside the requested lineage. A lineage's chain already begins with the
    /// engine snapshots it builds on, so a class the game never redefines still resolves -- without
    /// the lookup ever reaching into another game's definitions.
    /// </summary>
    public static TypeTreeNode? GetReleaseRoot(ClassIDType classID, TypeTreeVersion version)
    {
        if (version.IsEmpty)
        {
            return null;
        }

        return ReleaseRootCache.GetOrAdd(
            ((int)classID, version.Lineage, version.Version),
            static key => BuildReleaseRoot(key.ClassID, key.Lineage, key.Version));
    }

    private static TypeTreeNode? BuildReleaseRoot(int classID, string lineageKey, string versionKey)
    {
        EnsureLoaded();

        if (!_lineages!.TryGetValue(lineageKey, out Lineage? lineage))
        {
            throw new InvalidOperationException(
                $"[TypeTreeDatabase] No lineage '{lineageKey}' in {_origin}. Known: {string.Join(", ", _lineages.Keys)}.");
        }

        int ordinal = _manifest!.GetOrdinal(lineageKey, versionKey);
        if (ordinal < 0)
        {
            throw new InvalidOperationException(
                $"[TypeTreeDatabase] Lineage '{lineageKey}' does not declare version '{versionKey}' in {_origin}. " +
                "Dump that build's type tree and repack -- reading it with a neighbouring build's layout is not safe.");
        }

        if (!lineage.ClassesById.TryGetValue(classID, out TpkClassInformation? classInformation))
        {
            return null;
        }

        TpkUnityClass? unityClass = GetItemForOrdinal(classInformation.Classes, ordinal);
        if (unityClass is null)
        {
            return null;
        }

        return TypeTreeNode.FromTpk(lineage.Blob.NodeBuffer[unityClass.ReleaseRootNode], lineage.Blob.StringBuffer, lineage.Blob.NodeBuffer);
    }

    /// <summary>
    /// Walks the lineage's sparse chain back to the newest definition at or before
    /// <paramref name="ordinal"/>. A null entry marks "removed from here on" and resolves to nothing.
    /// </summary>
    private static TpkUnityClass? GetItemForOrdinal(List<KeyValuePair<UnityVersion, TpkUnityClass?>> list, int ordinal)
    {
        TpkUnityClass? result = null;
        foreach (KeyValuePair<UnityVersion, TpkUnityClass?> pair in list)
        {
            if (TypeTreeOrdinal.ToOrdinal(pair.Key) > ordinal)
            {
                break;
            }
            result = pair.Value;
        }
        return result;
    }

    private static void EnsureLoaded()
    {
        if (_manifest is not null)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_manifest is not null)
            {
                return;
            }

            using Stream stream = OpenTpkStream(out string origin);
            TpkDataBlob root = TpkFile.FromStream(stream).GetDataBlob();

            if (root is not TpkCollectionBlob collection)
            {
                throw new InvalidDataException(
                    $"[TypeTreeDatabase] {origin} holds a {root.DataType} blob; expected a Collection. Repack it with Ruri.Tpk.");
            }

            TypeTreeManifest? manifest = null;
            Dictionary<string, Lineage> lineages = new(StringComparer.Ordinal);

            foreach (KeyValuePair<string, TpkDataBlob> pair in collection.Blobs)
            {
                switch (pair.Value)
                {
                    case TpkJsonBlob json when pair.Key == TypeTreeManifest.BlobName:
                        manifest = TypeTreeManifest.FromJson(json.Text);
                        break;

                    case TpkTypeTreeBlob typeTree:
                    {
                        Dictionary<int, TpkClassInformation> classesById = new(typeTree.ClassInformation.Count);
                        foreach (TpkClassInformation classInformation in typeTree.ClassInformation)
                        {
                            classesById[classInformation.ID] = classInformation;
                        }
                        lineages[pair.Key] = new Lineage { Blob = typeTree, ClassesById = classesById };
                        break;
                    }
                }
            }

            _manifest = manifest ?? throw new InvalidDataException(
                $"[TypeTreeDatabase] {origin} has no '{TypeTreeManifest.BlobName}' manifest. Repack it with Ruri.Tpk.");
            _lineages = lineages;
            _origin = origin;
        }
    }

    private static Stream OpenTpkStream(out string origin)
    {
        string? overridePath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
            {
                throw new FileNotFoundException($"[TypeTreeDatabase] {PathEnvironmentVariable} points at a missing file.", overridePath);
            }
            origin = overridePath;
            return File.OpenRead(overridePath);
        }

        Assembly assembly = typeof(TypeTreeDatabase).Assembly;

        string adjacentPath = Path.Combine(Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory, ResourceName);
        if (File.Exists(adjacentPath))
        {
            origin = adjacentPath;
            return File.OpenRead(adjacentPath);
        }

        Stream? resource = assembly.GetManifestResourceStream($"{nameof(Ruri)}.{nameof(RipperHook)}.{ResourceName}")
            ?? assembly.GetManifestResourceStream(ResourceName);
        if (resource is not null)
        {
            origin = $"embedded:{ResourceName}";
            return resource;
        }

        throw new FileNotFoundException(
            $"[TypeTreeDatabase] {ResourceName} was not found next to {assembly.GetName().Name} nor embedded in it. " +
            $"Build it with Ruri.Tpk, or set {PathEnvironmentVariable}.", adjacentPath);
    }
}
