using System;
using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;

namespace Ruri.RipperHook.Core.TypeTree;

internal abstract class TypeTreeStep
{
    public abstract void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context);
}

internal abstract class TypeTreeFieldStep
{
    protected TypeTreeFieldStep(TypeTreeNode node, string path)
    {
        Node = node;
        Path = path;
    }

    public TypeTreeNode Node { get; }

    public string Path { get; }

    public Func<TypeTreeReadContext, bool>? Gate { get; set; }

    public void Read(object? owner, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        if (Gate is not null && !Gate(context))
        {
            return;
        }
        ReadCore(owner, ref reader, context);
    }

    protected abstract void ReadCore(object? owner, ref EndianSpanReader reader, TypeTreeReadContext context);
}

internal sealed class TypeTreeScalarFieldStep<T> : TypeTreeFieldStep
{
    private readonly TypeTreePrimitiveReader<T> read;
    private readonly bool align;
    private readonly Action<object, T>? setter;
    private readonly Func<T, T>? valueFix;
    private readonly bool capture;

    public TypeTreeScalarFieldStep(
        TypeTreeNode node,
        string path,
        TypeTreePrimitiveReader<T> read,
        bool align,
        Action<object, T>? setter,
        Func<T, T>? valueFix,
        bool capture)
        : base(node, path)
    {
        this.read = read;
        this.align = align;
        this.setter = setter;
        this.valueFix = valueFix;
        this.capture = capture;
    }

    protected override void ReadCore(object? owner, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        T value = read(ref reader);
        if (align)
        {
            reader.Align();
        }
        if (valueFix is not null)
        {
            value = valueFix(value);
        }
        if (capture)
        {
            context.Capture(Path, TypeTreeValue.Scalar(Node, value));
        }
        if (owner is not null)
        {
            setter?.Invoke(owner, value);
        }
    }
}

internal sealed class TypeTreeFilledFieldStep : TypeTreeFieldStep
{
    private readonly Func<object, object?>? getter;
    private readonly TypeTreeStep inner;

    public TypeTreeFilledFieldStep(TypeTreeNode node, string path, Func<object, object?>? getter, TypeTreeStep inner)
        : base(node, path)
    {
        this.getter = getter;
        this.inner = inner;
    }

    protected override void ReadCore(object? owner, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        object? instance = owner is not null && getter is not null ? getter(owner) : null;
        inner.ReadInto(instance, ref reader, context);
    }
}

internal sealed class TypeTreeCaptureFieldStep : TypeTreeFieldStep
{
    public TypeTreeCaptureFieldStep(TypeTreeNode node, string path) : base(node, path)
    {
    }

    protected override void ReadCore(object? owner, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        context.Capture(Path, TypeTreeCaptureReader.Read(Node, ref reader));
    }
}

internal sealed class TypeTreeStructStep : TypeTreeStep
{
    private readonly TypeTreeFieldStep[] fields;
    private readonly bool align;

    public TypeTreeStructStep(TypeTreeFieldStep[] fields, bool align)
    {
        this.fields = fields;
        this.align = align;
    }

    public TypeTreeFieldStep[] Fields => fields;

    public override void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i].Read(instance, ref reader, context);
        }
        if (align)
        {
            reader.Align();
        }
    }
}

internal sealed class TypeTreePrimitiveListStep<T> : TypeTreeStep where T : notnull, new()
{
    private readonly TypeTreePrimitiveReader<T> read;
    private readonly bool elementAlign;
    private readonly bool align;

    public TypeTreePrimitiveListStep(TypeTreePrimitiveReader<T> read, bool elementAlign, bool align)
    {
        this.read = read;
        this.elementAlign = elementAlign;
        this.align = align;
    }

    public override void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        AssetList<T>? list = instance as AssetList<T>;
        list?.Clear();

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            T value = read(ref reader);
            if (elementAlign)
            {
                reader.Align();
            }
            list?.Add(value);
        }

        if (list is not null)
        {
            list.Capacity = count;
        }
        if (align)
        {
            reader.Align();
        }
    }
}

internal sealed class TypeTreeAssetListStep<T> : TypeTreeStep where T : notnull, new()
{
    private readonly TypeTreeStep element;
    private readonly bool align;

    public TypeTreeAssetListStep(TypeTreeStep element, bool align)
    {
        this.element = element;
        this.align = align;
    }

    public override void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        AssetList<T>? list = instance as AssetList<T>;
        list?.Clear();

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            element.ReadInto(list is null ? null : list.AddNew(), ref reader, context);
        }

        if (list is not null)
        {
            list.Capacity = count;
        }
        if (align)
        {
            reader.Align();
        }
    }
}

internal sealed class TypeTreeDictionaryStep<TKey, TValue> : TypeTreeStep
    where TKey : notnull, new()
    where TValue : notnull, new()
{
    private readonly TypeTreeStep pair;
    private readonly bool align;

    public TypeTreeDictionaryStep(TypeTreeStep pair, bool align)
    {
        this.pair = pair;
        this.align = align;
    }

    public override void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        AssetDictionary<TKey, TValue>? dictionary = instance as AssetDictionary<TKey, TValue>;
        dictionary?.Clear();

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            pair.ReadInto(dictionary is null ? null : dictionary.AddNew(), ref reader, context);
        }

        if (dictionary is not null)
        {
            dictionary.Capacity = count;
        }
        if (align)
        {
            reader.Align();
        }
    }
}

internal sealed class TypeTreePairStep<TKey, TValue> : TypeTreeStep
    where TKey : notnull, new()
    where TValue : notnull, new()
{
    private readonly TypeTreePrimitiveReader<TKey>? readKey;
    private readonly bool keyAlign;
    private readonly TypeTreeStep? keyStep;
    private readonly TypeTreePrimitiveReader<TValue>? readValue;
    private readonly bool valueAlign;
    private readonly TypeTreeStep? valueStep;
    private readonly bool align;

    public TypeTreePairStep(
        TypeTreePrimitiveReader<TKey>? readKey,
        bool keyAlign,
        TypeTreeStep? keyStep,
        TypeTreePrimitiveReader<TValue>? readValue,
        bool valueAlign,
        TypeTreeStep? valueStep,
        bool align)
    {
        this.readKey = readKey;
        this.keyAlign = keyAlign;
        this.keyStep = keyStep;
        this.readValue = readValue;
        this.valueAlign = valueAlign;
        this.valueStep = valueStep;
        this.align = align;
    }

    public override void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        AssetPair<TKey, TValue>? pair = instance as AssetPair<TKey, TValue>;

        if (readKey is not null)
        {
            TKey key = readKey(ref reader);
            if (keyAlign)
            {
                reader.Align();
            }
            if (pair is not null)
            {
                pair.Key = key;
            }
        }
        else
        {
            object? keyInstance = pair is null ? null : pair.Key;
            keyStep!.ReadInto(keyInstance, ref reader, context);
        }

        if (readValue is not null)
        {
            TValue value = readValue(ref reader);
            if (valueAlign)
            {
                reader.Align();
            }
            if (pair is not null)
            {
                pair.Value = value;
            }
        }
        else
        {
            object? valueInstance = pair is null ? null : pair.Value;
            valueStep!.ReadInto(valueInstance, ref reader, context);
        }

        if (align)
        {
            reader.Align();
        }
    }
}

internal sealed class TypeTreeDiscardStep : TypeTreeStep
{
    private readonly TypeTreeNode node;

    public TypeTreeDiscardStep(TypeTreeNode node)
    {
        this.node = node;
    }

    public override void ReadInto(object? instance, ref EndianSpanReader reader, TypeTreeReadContext context)
    {
        TypeTreeCaptureReader.Skip(node, ref reader);
    }
}
