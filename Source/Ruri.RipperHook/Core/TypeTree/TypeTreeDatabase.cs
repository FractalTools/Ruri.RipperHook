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
/// of baking one C# class per (class, engine version) ahead of time, the same tpk that used to drive
/// that codegen is shipped as-is and interpreted here.
///
/// Resolution order for the blob:
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
    private static readonly ConcurrentDictionary<(int ClassID, UnityVersion Version), TypeTreeNode?> ReleaseRootCache = new();

    private static TpkTypeTreeBlob? _blob;
    private static Dictionary<int, TpkClassInformation>? _classesById;
    private static string _blobOrigin = "<unloaded>";

    public static string BlobOrigin
    {
        get
        {
            EnsureLoaded();
            return _blobOrigin;
        }
    }

    /// <summary>
    /// Resolves the release (build) type tree a game uses for <paramref name="classID"/>, or
    /// <see langword="null"/> when the tpk carries no definition for it at that version.
    /// </summary>
    public static TypeTreeNode? GetReleaseRoot(ClassIDType classID, UnityVersion version)
    {
        return ReleaseRootCache.GetOrAdd(((int)classID, version), static key => BuildReleaseRoot(key.ClassID, key.Version));
    }

    private static TypeTreeNode? BuildReleaseRoot(int classID, UnityVersion version)
    {
        EnsureLoaded();

        if (!_classesById!.TryGetValue(classID, out TpkClassInformation? classInformation))
        {
            return null;
        }

        TpkUnityClass? unityClass = GetItemForVersion(classInformation.Classes, version);
        if (unityClass is null)
        {
            return null;
        }

        return TypeTreeNode.FromTpk(_blob!.NodeBuffer[unityClass.ReleaseRootNode], _blob.StringBuffer, _blob.NodeBuffer);
    }

    private static void EnsureLoaded()
    {
        if (_blob is not null)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_blob is not null)
            {
                return;
            }

            using Stream stream = OpenBlobStream(out string origin);
            TpkTypeTreeBlob blob = (TpkTypeTreeBlob)TpkFile.FromStream(stream).GetDataBlob();

            Dictionary<int, TpkClassInformation> classesById = new(blob.ClassInformation.Count);
            foreach (TpkClassInformation classInformation in blob.ClassInformation)
            {
                classesById[classInformation.ID] = classInformation;
            }

            _classesById = classesById;
            _blobOrigin = origin;
            _blob = blob;
        }
    }

    private static Stream OpenBlobStream(out string origin)
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
            $"Build it with Ruri.AssemblyDumper, or set {PathEnvironmentVariable}.", adjacentPath);
    }

    /// <summary>
    /// 1:1 port of <c>TypeTreeNodeStruct.GetItemForVersion</c> -- the version list is sparse and uses
    /// null entries to mark "the class does not exist from here on", so a plain nearest-match lookup
    /// would resurrect a removed class.
    /// </summary>
    private static TpkUnityClass? GetItemForVersion(List<KeyValuePair<UnityVersion, TpkUnityClass?>> list, UnityVersion version)
    {
        if (list.Count == 0)
        {
            return null;
        }

        if (list[0].Key > version)
        {
            return list[0].Value;
        }

        for (int i = 0; i < list.Count - 1; i++)
        {
            if (list[i].Key <= version && version < list[i + 1].Key)
            {
                return list[i].Value ?? (i > 0 ? list[i - 1].Value : null);
            }
        }

        return list[^1].Value ?? (list.Count > 1 ? list[^2].Value : null);
    }
}
