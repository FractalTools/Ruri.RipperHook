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

public static class TypeTreeDatabase
{
    public const string ResourceName = "RuriTypeTree.tpk";
    public const string PathEnvironmentVariable = "RURI_TYPE_TREE_TPK";

    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<(int ClassID, string Lineage, string Version), TypeTreeNode?> ReleaseRootCache = new();
    private static readonly ConcurrentDictionary<(int ClassID, string Lineage, string Version), TypeTreeNode?> EditorRootCache = new();

    public static TypeTreeVersion ActiveVersion { get; set; }

    private static TypeTreeManifest? _manifest;
    private static Dictionary<string, Lineage>? _lineages;
    private static string _origin = "<unloaded>";

    private sealed class Lineage
    {
        public required IReadOnlyList<TpkTypeTreeBlob> Blobs;
        public required Dictionary<int, (TpkClassInformation Class, TpkTypeTreeBlob Blob)> ClassesById;
        public Dictionary<string, int>? IdsByName;

        public static Lineage From(IReadOnlyList<TpkTypeTreeBlob> blobs)
        {
            Dictionary<int, (TpkClassInformation, TpkTypeTreeBlob)> classesById = new();
            foreach (TpkTypeTreeBlob blob in blobs)
            {
                foreach (TpkClassInformation classInformation in blob.ClassInformation)
                {
                    classesById[classInformation.ID] = (classInformation, blob);
                }
            }
            return new Lineage { Blobs = blobs, ClassesById = classesById };
        }
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
    /// Add (or replace) a lineage built at run time -- a schema read from the install itself,
    /// such as another engine's reflection dump restated as type trees -- so its classes resolve
    /// exactly like the packed ones. <paramref name="versions"/> lists the lineage's snapshots in
    /// ordinal order with the Unity layout version each was emitted against.
    /// </summary>
    public static void RegisterLineage(string key, IReadOnlyList<TpkTypeTreeBlob> blobs, IReadOnlyList<(string Version, string Engine)> versions)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(blobs);
        EnsureLoaded();
        lock (SyncRoot)
        {
            _lineages![key] = Lineage.From(blobs);

            TypeTreeManifest.LineageEntry entry = new() { Key = key };
            foreach ((string version, string engine) in versions)
            {
                entry.Versions.Add(new TypeTreeManifest.VersionEntry { Key = version, Engine = engine });
            }
            _manifest!.Lineages.RemoveAll(existing => existing.Key == key);
            _manifest.Lineages.Add(entry);
            _manifest.Invalidate();
        }
        foreach (var cached in ReleaseRootCache.Keys.Where(cacheKey => cacheKey.Lineage == key).ToArray())
        {
            ReleaseRootCache.TryRemove(cached, out _);
        }
        foreach (var cached in EditorRootCache.Keys.Where(cacheKey => cacheKey.Lineage == key).ToArray())
        {
            EditorRootCache.TryRemove(cached, out _);
        }
    }

    /// <summary>
    /// The class id a lineage files <paramref name="className"/> under, or -1. Class ids of a
    /// lineage restated from a named schema are ordinals assigned at pack time, so a name is
    /// the only identity the source can ask by.
    /// </summary>
    public static int ClassIdByName(string lineageKey, string className)
    {
        EnsureLoaded();
        if (!_lineages!.TryGetValue(lineageKey, out Lineage? lineage))
        {
            return -1;
        }
        Dictionary<string, int>? index = lineage.IdsByName;
        if (index is null)
        {
            index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach ((TpkClassInformation classInformation, TpkTypeTreeBlob blob) in lineage.ClassesById.Values)
            {
                foreach (KeyValuePair<UnityVersion, TpkUnityClass?> pair in classInformation.Classes)
                {
                    if (pair.Value is not null)
                    {
                        index[blob.StringBuffer[pair.Value.Name]] = classInformation.ID;
                        break;
                    }
                }
            }
            lineage.IdsByName = index;
        }
        return index.TryGetValue(className, out int id) ? id : -1;
    }

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

    public static TypeTreeNode? GetReleaseRoot(ClassIDType classID, TypeTreeVersion version)
    {
        if (version.IsEmpty)
        {
            return null;
        }

        return ReleaseRootCache.GetOrAdd(
            ((int)classID, version.Lineage, version.Version),
            static key => BuildRoot(key.ClassID, key.Lineage, key.Version, editor: false));
    }

    public static TypeTreeNode? GetEditorRoot(ClassIDType classID, TypeTreeVersion version)
    {
        if (version.IsEmpty)
        {
            return null;
        }

        return EditorRootCache.GetOrAdd(
            ((int)classID, version.Lineage, version.Version),
            static key => BuildRoot(key.ClassID, key.Lineage, key.Version, editor: true));
    }

    private static TypeTreeNode? BuildRoot(int classID, string lineageKey, string versionKey, bool editor)
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

        if (!lineage.ClassesById.TryGetValue(classID, out (TpkClassInformation Class, TpkTypeTreeBlob Blob) entry))
        {
            return null;
        }

        TpkUnityClass? unityClass = GetItemForOrdinal(entry.Class.Classes, ordinal);
        if (unityClass is null)
        {
            return null;
        }

        TpkUnityClassFlags required = editor ? TpkUnityClassFlags.HasEditorRootNode : TpkUnityClassFlags.HasReleaseRootNode;
        if ((unityClass.Flags & required) == 0)
        {
            return null;
        }

        ushort root = editor ? unityClass.EditorRootNode : unityClass.ReleaseRootNode;
        return TypeTreeNode.FromTpk(entry.Blob.NodeBuffer[root], entry.Blob.StringBuffer, entry.Blob.NodeBuffer);
    }

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
                        lineages[pair.Key] = Lineage.From([typeTree]);
                        break;
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
