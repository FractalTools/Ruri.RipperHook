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

        if (!lineage.ClassesById.TryGetValue(classID, out TpkClassInformation? classInformation))
        {
            return null;
        }

        TpkUnityClass? unityClass = GetItemForOrdinal(classInformation.Classes, ordinal);
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
        return TypeTreeNode.FromTpk(lineage.Blob.NodeBuffer[root], lineage.Blob.StringBuffer, lineage.Blob.NodeBuffer);
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
