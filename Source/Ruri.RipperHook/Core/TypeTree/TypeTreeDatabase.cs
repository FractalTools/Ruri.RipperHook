using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
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
    private static FieldNames? _fieldNames;
    private static string _origin = "<unloaded>";

    private sealed class Lineage
    {
        public required Dictionary<int, (TpkClassInformation Class, TpkTypeTreeBlob Blob)> ClassesById;
        public required Dictionary<int, (TpkClassInformation Class, TpkTypeTreeBlob Blob)> UnityClassesById;
        public Dictionary<string, int>? IdsByName;

        public static Lineage From(IReadOnlyList<TpkTypeTreeBlob> blobs, IReadOnlyList<TpkTypeTreeBlob> unityNames) =>
            new() { ClassesById = Index(blobs), UnityClassesById = Index(unityNames) };

        private static Dictionary<int, (TpkClassInformation, TpkTypeTreeBlob)> Index(IReadOnlyList<TpkTypeTreeBlob> blobs)
        {
            Dictionary<int, (TpkClassInformation, TpkTypeTreeBlob)> classesById = new();
            foreach (TpkTypeTreeBlob blob in blobs)
            {
                foreach (TpkClassInformation classInformation in blob.ClassInformation)
                {
                    classesById[classInformation.ID] = (classInformation, blob);
                }
            }
            return classesById;
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
    /// ordinal order with the Unity layout version each was emitted against. Such a schema has
    /// one vocabulary, its own.
    /// </summary>
    public static void RegisterLineage(string key, IReadOnlyList<TpkTypeTreeBlob> blobs, IReadOnlyList<(string Version, string Engine)> versions)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(blobs);
        EnsureLoaded();
        lock (SyncRoot)
        {
            _lineages![key] = Lineage.From(blobs, blobs);

            TypeTreeManifest.LineageEntry entry = new() { Key = key, UnityNames = key };
            foreach ((string version, string engine) in versions)
            {
                entry.Versions.Add(new TypeTreeManifest.VersionEntry { Key = version, Engine = engine });
            }
            _manifest!.Lineages.RemoveAll(existing => existing.Key == key);
            _manifest.Lineages.Add(entry);
            _manifest.Invalidate();
            _fieldNames = null;
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

    /// <summary>
    /// A class's tree as the engine spells it (see <see cref="TypeTreeManifest.LineageEntry.UnityNames"/>),
    /// for a consumer that hands it to AssetRipper's own reader rather than to this hook's interpreter:
    /// the root node and the blob whose buffers it indexes, or null wherever <see cref="GetReleaseRoot"/>
    /// and <see cref="GetEditorRoot"/> answer null.
    /// </summary>
    public static (TpkUnityNode Root, TpkTypeTreeBlob Blob)? GetUnityRoot(ClassIDType classID, TypeTreeVersion version, bool editor)
    {
        if (version.IsEmpty)
        {
            return null;
        }

        return FindRoot((int)classID, version.Lineage, version.Version, editor, unityNames: true);
    }

    /// <summary>
    /// The field name AssetRipper's renaming pass gives a node that a serialized tree of class <paramref name="classID"/>
    /// spells <paramref name="name"/>, of type <paramref name="type"/>, at <paramref name="path"/> (the spelled names from
    /// the root, joined by '/') under a node of type <paramref name="parentType"/>. The packed lineages hold every tree both
    /// as the engine spells it and as that pass rewrote it, node for node, so the name is read off them: by the node's
    /// place in its class first, else by its parent's type. Null where no packed tree holds such a node; a node the packed
    /// trees name two ways both at its place and under its parent's type throws.
    /// </summary>
    public static string? GetFieldName(ClassIDType classID, bool editor, string path, string parentType, string name, string type)
    {
        FieldNames names = Volatile.Read(ref _fieldNames) ?? BuildFieldNames();
        if (names.ByPath.TryGetValue((editor, (int)classID, path, type), out string? placed) && placed is not null)
        {
            return placed;
        }
        if (!names.ByParent.TryGetValue((editor, parentType, name, type), out string? fieldName))
        {
            return null;
        }
        return fieldName ?? throw new NotSupportedException(
            $"[TypeTreeDatabase] {type} {path} of {classID} is renamed two ways across the packed trees of {_origin}.");
    }

    private sealed class FieldNames
    {
        public readonly Dictionary<(bool Editor, int ClassID, string Path, string Type), string?> ByPath = new();
        public readonly Dictionary<(bool Editor, string Parent, string Name, string Type), string?> ByParent = new();

        public static void Record<TKey>(Dictionary<TKey, string?> names, TKey key, string fieldName) where TKey : notnull
        {
            if (!names.TryAdd(key, fieldName) && names[key] != fieldName)
            {
                names[key] = null;
            }
        }
    }

    private static FieldNames BuildFieldNames()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_fieldNames is not null)
            {
                return _fieldNames;
            }
            FieldNames names = new();
            HashSet<(TpkTypeTreeBlob Blob, int ClassID, string Path, ushort Unity, ushort Renamed, bool Editor)> walked = new();
            foreach (Lineage lineage in _lineages!.Values)
            {
                foreach ((int id, (TpkClassInformation Class, TpkTypeTreeBlob Blob) renamed) in lineage.ClassesById)
                {
                    if (!lineage.UnityClassesById.TryGetValue(id, out (TpkClassInformation Class, TpkTypeTreeBlob Blob) unity)
                        || ReferenceEquals(unity.Blob, renamed.Blob))
                    {
                        continue;
                    }
                    List<KeyValuePair<UnityVersion, TpkUnityClass?>> unityClasses = unity.Class.Classes;
                    List<KeyValuePair<UnityVersion, TpkUnityClass?>> renamedClasses = renamed.Class.Classes;
                    if (unityClasses.Count != renamedClasses.Count)
                    {
                        throw new InvalidDataException(
                            $"[TypeTreeDatabase] Class {id} has {unityClasses.Count} spelled and {renamedClasses.Count} renamed versions in {_origin}. Repack it with Ruri.Tpk.");
                    }
                    for (int i = 0; i < unityClasses.Count; i++)
                    {
                        if (unityClasses[i].Value is not { } unityClass || renamedClasses[i].Value is not { } renamedClass)
                        {
                            continue;
                        }
                        if ((unityClass.Flags & TpkUnityClassFlags.HasEditorRootNode) != 0)
                        {
                            WalkFieldNames(names, walked, id, "", unity.Blob, unityClass.EditorRootNode, renamed.Blob, renamedClass.EditorRootNode, true);
                        }
                        if ((unityClass.Flags & TpkUnityClassFlags.HasReleaseRootNode) != 0)
                        {
                            WalkFieldNames(names, walked, id, "", unity.Blob, unityClass.ReleaseRootNode, renamed.Blob, renamedClass.ReleaseRootNode, false);
                        }
                    }
                }
            }
            Volatile.Write(ref _fieldNames, names);
            return names;
        }
    }

    private static void WalkFieldNames(FieldNames names,
        HashSet<(TpkTypeTreeBlob Blob, int ClassID, string Path, ushort Unity, ushort Renamed, bool Editor)> walked, int classID,
        string path, TpkTypeTreeBlob unityBlob, ushort unityIndex, TpkTypeTreeBlob renamedBlob, ushort renamedIndex, bool editor)
    {
        if (!walked.Add((unityBlob, classID, path, unityIndex, renamedIndex, editor)))
        {
            return;
        }
        TpkUnityNode unity = unityBlob.NodeBuffer[unityIndex];
        TpkUnityNode renamed = renamedBlob.NodeBuffer[renamedIndex];
        if (unity.SubNodes.Length != renamed.SubNodes.Length)
        {
            throw new InvalidDataException(
                $"[TypeTreeDatabase] {unityBlob.StringBuffer[unity.TypeName]} has {unity.SubNodes.Length} spelled and {renamed.SubNodes.Length} renamed children in {_origin}. Repack it with Ruri.Tpk.");
        }
        string parent = unityBlob.StringBuffer[unity.TypeName];
        for (int i = 0; i < unity.SubNodes.Length; i++)
        {
            TpkUnityNode child = unityBlob.NodeBuffer[unity.SubNodes[i]];
            string name = unityBlob.StringBuffer[child.Name];
            string type = unityBlob.StringBuffer[child.TypeName];
            string childPath = path.Length == 0 ? name : path + "/" + name;
            string fieldName = renamedBlob.StringBuffer[renamedBlob.NodeBuffer[renamed.SubNodes[i]].Name];
            FieldNames.Record(names.ByPath, (editor, classID, childPath, type), fieldName);
            FieldNames.Record(names.ByParent, (editor, parent, name, type), fieldName);
            WalkFieldNames(names, walked, classID, childPath, unityBlob, unity.SubNodes[i], renamedBlob, renamed.SubNodes[i], editor);
        }
    }

    private static TypeTreeNode? BuildRoot(int classID, string lineageKey, string versionKey, bool editor) =>
        FindRoot(classID, lineageKey, versionKey, editor, unityNames: false) is { } found
            ? TypeTreeNode.FromTpk(found.Root, found.Blob.StringBuffer, found.Blob.NodeBuffer)
            : null;

    private static (TpkUnityNode Root, TpkTypeTreeBlob Blob)? FindRoot(int classID, string lineageKey, string versionKey,
        bool editor, bool unityNames)
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

        Dictionary<int, (TpkClassInformation Class, TpkTypeTreeBlob Blob)> classes =
            unityNames ? lineage.UnityClassesById : lineage.ClassesById;
        if (!classes.TryGetValue(classID, out (TpkClassInformation Class, TpkTypeTreeBlob Blob) entry))
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
        return (entry.Blob.NodeBuffer[root], entry.Blob);
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
        if (Volatile.Read(ref _manifest) is not null)
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
            Dictionary<string, TpkTypeTreeBlob> blobs = new(StringComparer.Ordinal);

            foreach (KeyValuePair<string, TpkDataBlob> pair in collection.Blobs)
            {
                switch (pair.Value)
                {
                    case TpkJsonBlob json when pair.Key == TypeTreeManifest.BlobName:
                        manifest = TypeTreeManifest.FromJson(json.Text);
                        break;

                    case TpkTypeTreeBlob typeTree:
                        blobs[pair.Key] = typeTree;
                        break;
                }
            }

            TypeTreeManifest loaded = manifest ?? throw new InvalidDataException(
                $"[TypeTreeDatabase] {origin} has no '{TypeTreeManifest.BlobName}' manifest. Repack it with Ruri.Tpk.");

            Dictionary<string, Lineage> lineages = new(StringComparer.Ordinal);
            foreach (TypeTreeManifest.LineageEntry entry in loaded.Lineages)
            {
                lineages[entry.Key] = Lineage.From([Blob(entry.Key)], [Blob(entry.UnityNames)]);
            }
            _lineages = lineages;
            _origin = origin;
            Volatile.Write(ref _manifest, loaded);

            TpkTypeTreeBlob Blob(string key) => blobs.TryGetValue(key, out TpkTypeTreeBlob? blob)
                ? blob
                : throw new InvalidDataException(
                    $"[TypeTreeDatabase] {origin}'s manifest names blob '{key}', which it does not hold. Repack it with Ruri.Tpk.");
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
