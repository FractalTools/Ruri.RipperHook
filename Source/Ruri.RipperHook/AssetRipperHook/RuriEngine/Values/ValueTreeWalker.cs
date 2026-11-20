using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Subclasses.GUID;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class ValueTreeWalker(AssetCollection collection, ReferenceResolver references, string where) : AssetWalker
{
    private const string FirstName = "first";
    private const string SecondName = "second";

    private readonly Stack<Frame> frames = new();

    public static ObjectValue Walk(IUnityAssetBase asset, AssetCollection collection, ReferenceResolver references, string where)
    {
        ValueTreeWalker walker = new(collection, references, where);
        walker.frames.Push(new Frame(new ArrayValue()));
        asset.WalkEditor(walker);
        ArrayValue root = (ArrayValue)walker.frames.Pop().Container;
        return root.Items.Count == 1 && root.Items[0] is ObjectValue value ? value : new ObjectValue();
    }

    public override bool EnterAsset(IUnityAssetBase asset)
    {
        if (asset is GUID guid)
        {
            Add(ScalarValue.FromText(guid.ToString()));
            return false;
        }
        frames.Push(new Frame(new ObjectValue()));
        return true;
    }

    public override void ExitAsset(IUnityAssetBase asset) => Complete();

    public override bool EnterField(IUnityAssetBase asset, string name)
    {
        if (SparseFields.IsAbsent(asset, name))
        {
            return false;
        }
        frames.Peek().Name = name;
        return true;
    }

    public override void ExitField(IUnityAssetBase asset, string name)
    {
        frames.Peek().Name = null;
    }

    public override bool EnterList<T>(IReadOnlyList<T> list)
    {
        frames.Push(new Frame(new ArrayValue()));
        return true;
    }

    public override void ExitList<T>(IReadOnlyList<T> list) => Complete();

    public override bool EnterDictionary<TKey, TValue>(IReadOnlyCollection<KeyValuePair<TKey, TValue>> dictionary)
    {
        frames.Push(new Frame(new ArrayValue()));
        return true;
    }

    public override void ExitDictionary<TKey, TValue>(IReadOnlyCollection<KeyValuePair<TKey, TValue>> dictionary) => Complete();

    public override bool EnterPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
    {
        frames.Push(new Frame(new ObjectValue()) { Name = FirstName });
        return true;
    }

    public override void DividePair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
    {
        frames.Peek().Name = SecondName;
    }

    public override void ExitPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair) => Complete();

    public override void VisitPrimitive<T>(T value)
    {
        Add(ScalarOf(value));
    }

    public override void VisitPPtr<TAsset>(PPtr<TAsset> pptr)
    {
        Add(references.Resolve(collection, pptr.FileID, pptr.PathID, typeof(TAsset), where));
    }

    private static ScalarValue ScalarOf<T>(T value) where T : notnull
    {
        return value switch
        {
            bool flag => ScalarValue.FromBoolean(flag),
            char character => ScalarValue.FromInteger(character),
            sbyte signedByte => ScalarValue.FromInteger(signedByte),
            byte unsignedByte => ScalarValue.FromInteger(unsignedByte),
            short signedShort => ScalarValue.FromInteger(signedShort),
            ushort unsignedShort => ScalarValue.FromInteger(unsignedShort),
            int signedInt => ScalarValue.FromInteger(signedInt),
            uint unsignedInt => ScalarValue.FromInteger(unsignedInt),
            long signedLong => ScalarValue.FromInteger(signedLong),
            ulong unsignedLong => ScalarValue.FromUnsigned(unsignedLong),
            float single => ScalarValue.FromSingle(single),
            double floating => ScalarValue.FromDouble(floating),
            string text => ScalarValue.FromText(text),
            Utf8String utf8 => ScalarValue.FromText(utf8.String),
            byte[] bytes => ScalarValue.FromBytes(bytes),
            _ => ScalarValue.FromText(value.ToString() ?? string.Empty),
        };
    }

    private void Add(Value value)
    {
        Frame frame = frames.Peek();
        switch (frame.Container)
        {
            case ObjectValue entity:
                entity.Add(frame.Name ?? string.Empty, value);
                break;
            case ArrayValue array:
                array.Items.Add(value);
                break;
        }
    }

    private void Complete()
    {
        Frame frame = frames.Pop();
        Add(frame.Container);
    }

    private sealed class Frame(Value container)
    {
        public Value Container { get; } = container;

        public string? Name { get; set; }
    }
}
