using System;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// State for one asset read: the asset being filled plus the game-only nodes a hook asked to capture.
///
/// Instances are pooled per read and are not thread safe; a read is always confined to the thread
/// that owns the <c>EndianSpanReader</c>.
/// </summary>
public sealed class TypeTreeReadContext
{
    private readonly Dictionary<string, TypeTreeValue> captured = new(StringComparer.Ordinal);

    public IUnityObjectBase Asset { get; private set; } = null!;

    public UnityVersion Version { get; private set; }

    public ClassIDType ClassID { get; private set; }

    internal void Begin(IUnityObjectBase asset, ClassIDType classID, UnityVersion version)
    {
        captured.Clear();
        Asset = asset;
        ClassID = classID;
        Version = version;
    }

    internal void Capture(string path, TypeTreeValue value) => captured[path] = value;

    /// <summary>Captured node by path, e.g. <c>m_CollisionMeshBaked</c> or <c>m_Shapes/m_Vertices</c>.</summary>
    public TypeTreeValue? Find(string path) => captured.TryGetValue(path, out TypeTreeValue? value) ? value : null;

    public TypeTreeValue Require(string path) => Find(path)
        ?? throw new InvalidOperationException(
            $"[TypeTree] '{path}' was not captured while reading {ClassID}. " +
            "Declare it in the hook's Captures list so the read plan retains it.");

    public bool GetBoolean(string path) => Require(path).AsBoolean();

    public int GetInt32(string path) => Require(path).AsInt32();

    public uint GetUInt32(string path) => Require(path).AsUInt32();

    public float GetSingle(string path) => Require(path).AsSingle();

    public byte[] GetByteArray(string path) => Require(path).AsByteArray();

    public Utf8String GetUtf8String(string path) => Require(path).AsUtf8String();

    /// <summary>True when the node exists in this game's tree and was captured.</summary>
    public bool Has(string path) => captured.ContainsKey(path);
}
