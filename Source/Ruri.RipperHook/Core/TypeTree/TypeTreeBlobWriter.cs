using System;
using System.Collections.Generic;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.Tpk.TypeTrees;

namespace Ruri.RipperHook.Core.TypeTree;

public sealed class TypeTreeBlobWriter
{
    public static readonly UnityVersion LayoutVersion = new(2022, 3, 62, UnityVersionType.Final, 1);

    private const int NodeBudget = 8192;

    private readonly UnityVersion ordinalVersion = TypeTreeOrdinal.ToUnityVersion(0);
    private readonly List<TpkTypeTreeBlob> blobs = new();
    private readonly Dictionary<string, ushort> stringIndices = new(StringComparer.Ordinal);
    private readonly Dictionary<NodeKey, ushort> nodeIndices = new();
    private TpkTypeTreeBlob current;

    public TypeTreeBlobWriter()
    {
        current = NewBlob();
    }

    public IReadOnlyList<TpkTypeTreeBlob> Blobs => blobs;

    public bool BeginClass()
    {
        if (current.NodeBuffer.Count <= NodeBudget)
        {
            return false;
        }
        Seal();
        current = NewBlob();
        return true;
    }

    public ushort Intern(string value)
    {
        if (!stringIndices.TryGetValue(value, out ushort index))
        {
            index = current.StringBuffer.AddString(value);
            stringIndices[value] = index;
        }
        return index;
    }

    public ushort Node(string typeName, string nodeName, int version, TransferMetaFlags flags, IReadOnlyList<ushort> children)
    {
        TpkUnityNode node = new()
        {
            TypeName = Intern(typeName),
            Name = Intern(nodeName),
            Version = (short)version,
            MetaFlag = (uint)flags,
            SubNodes = children.Count == 0 ? Array.Empty<ushort>() : ToArray(children),
        };
        NodeKey key = new(node.TypeName, node.Name, node.Version, node.MetaFlag, node.SubNodes);
        if (!nodeIndices.TryGetValue(key, out ushort index))
        {
            index = current.NodeBuffer.AddNode(node);
            nodeIndices[key] = index;
        }
        return index;
    }

    public void AddClass(int id, string name, string baseName, ushort root)
    {
        TpkUnityClass unityClass = new()
        {
            Name = Intern(name),
            Base = Intern(baseName),
            Flags = TpkUnityClassFlags.HasReleaseRootNode | TpkUnityClassFlags.HasEditorRootNode,
            ReleaseRootNode = root,
            EditorRootNode = root,
        };
        TpkClassInformation information = new(id);
        information.Classes.Add(new KeyValuePair<UnityVersion, TpkUnityClass?>(ordinalVersion, unityClass));
        current.ClassInformation.Add(information);
    }

    public IReadOnlyList<TpkTypeTreeBlob> Finish()
    {
        Seal();
        return blobs;
    }

    private static ushort[] ToArray(IReadOnlyList<ushort> children)
    {
        ushort[] result = new ushort[children.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = children[index];
        }
        return result;
    }

    private TpkTypeTreeBlob NewBlob()
    {
        stringIndices.Clear();
        nodeIndices.Clear();
        TpkTypeTreeBlob blob = new();
        blob.Versions.Add(ordinalVersion);
        blob.CommonString.Add(UnityVersion.MinVersion, []);
        blob.CreationTime = DateTime.UtcNow;
        return blob;
    }

    private void Seal()
    {
        if (current.ClassInformation.Count > 0 && !blobs.Contains(current))
        {
            blobs.Add(current);
        }
    }

    private readonly struct NodeKey : IEquatable<NodeKey>
    {
        private readonly ushort typeName;
        private readonly ushort name;
        private readonly int version;
        private readonly uint metaFlag;
        private readonly ushort[] children;
        private readonly int hash;

        public NodeKey(ushort typeName, ushort name, int version, uint metaFlag, ushort[] children)
        {
            this.typeName = typeName;
            this.name = name;
            this.version = version;
            this.metaFlag = metaFlag;
            this.children = children;
            HashCode code = new();
            code.Add(typeName);
            code.Add(name);
            code.Add(version);
            code.Add(metaFlag);
            code.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(children.AsSpan()));
            hash = code.ToHashCode();
        }

        public bool Equals(NodeKey other) =>
            hash == other.hash && typeName == other.typeName && name == other.name && version == other.version
            && metaFlag == other.metaFlag && children.AsSpan().SequenceEqual(other.children);

        public override bool Equals(object? obj) => obj is NodeKey other && Equals(other);

        public override int GetHashCode() => hash;
    }
}
