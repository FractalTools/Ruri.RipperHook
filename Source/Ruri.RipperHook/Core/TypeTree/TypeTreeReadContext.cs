using System;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

public sealed class TypeTreeReadContext
{
    private readonly Dictionary<string, TypeTreeValue> captured = new(StringComparer.Ordinal);

    public IUnityObjectBase Asset { get; private set; } = null!;

    public TypeTreeVersion Version { get; private set; }

    public ClassIDType ClassID { get; private set; }

    internal void Begin(IUnityObjectBase asset, ClassIDType classID, TypeTreeVersion version)
    {
        captured.Clear();
        Asset = asset;
        ClassID = classID;
        Version = version;
    }

    internal void Capture(string path, TypeTreeValue value) => captured[path] = value;

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

    public bool Has(string path) => captured.ContainsKey(path);
}
