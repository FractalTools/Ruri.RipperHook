using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// A cached, executable read layout for one (class, engine version, AssetRipper type) triple.
///
/// This is what replaces the generated <c>Ruri.SourceGenerated</c> assembly. Where the old pipeline
/// ran the AssetRipper assembly dumper over the game's tpk to emit a parallel class hierarchy whose
/// <c>ReadRelease</c> methods were then deep-copied onto the stock classes, the plan walks the same
/// tpk tree at runtime and reads straight into the stock class -- one object graph built once per
/// type, then reused for every asset.
///
/// Field binding, node dispatch and align placement are ports of <c>Pass015_AddFields</c> and
/// <c>Pass100_FillReadMethods</c>; see <see cref="TypeTreeStep"/> for the per-node semantics.
/// </summary>
public sealed class TypeTreeReadPlan
{
    private static readonly ConcurrentDictionary<(Type Target, ClassIDType ClassID, UnityVersion Version, int Revision), TypeTreeReadPlan?> Cache = new();

    [ThreadStatic]
    private static TypeTreeReadContext? _context;

    private readonly TypeTreeStructStep root;
    private readonly IReadOnlyList<Action<TypeTreeReadContext>>? postReaders;

    public ClassIDType ClassID { get; }

    public UnityVersion Version { get; }

    public TypeTreeNode RootNode { get; }

    private TypeTreeReadPlan(ClassIDType classID, UnityVersion version, TypeTreeNode rootNode, TypeTreeStructStep root, IReadOnlyList<Action<TypeTreeReadContext>>? postReaders)
    {
        ClassID = classID;
        Version = version;
        RootNode = rootNode;
        this.root = root;
        this.postReaders = postReaders;
    }

    /// <summary>
    /// The plan for reading <paramref name="classID"/> at <paramref name="version"/> into
    /// <paramref name="targetType"/>, or <see langword="null"/> when the tpk has no tree for it.
    /// </summary>
    public static TypeTreeReadPlan? Get(ClassIDType classID, Type targetType, UnityVersion version)
    {
        return Cache.GetOrAdd(
            (targetType, classID, version, TypeTreeOverrides.Revision),
            static key => Build(key.ClassID, key.Target, key.Version));
    }

    public void Read(IUnityObjectBase asset, ref EndianSpanReader reader)
    {
        TypeTreeReadContext context = _context ??= new TypeTreeReadContext();
        context.Begin(asset, ClassID, Version);

        root.ReadInto(asset, ref reader, context);

        if (postReaders is not null)
        {
            for (int i = 0; i < postReaders.Count; i++)
            {
                postReaders[i](context);
            }
        }
    }

    private static TypeTreeReadPlan? Build(ClassIDType classID, Type targetType, UnityVersion version)
    {
        TypeTreeNode? rootNode = TypeTreeDatabase.GetReleaseRoot(classID, version);
        if (rootNode is null)
        {
            return null;
        }

        TypeTreeStructStep root = new(BuildFields(rootNode, targetType, classID, ""), align: false);
        return new TypeTreeReadPlan(classID, version, rootNode, root, TypeTreeOverrides.FindPostReaders(classID));
    }

    // -----------------------------------------------------------------
    // plan construction
    // -----------------------------------------------------------------

    private static TypeTreeFieldStep[] BuildFields(TypeTreeNode structNode, Type? ownerType, ClassIDType classID, string parentPath)
    {
        TypeTreeFieldStep[] steps = new TypeTreeFieldStep[structNode.SubNodes.Length];
        for (int i = 0; i < structNode.SubNodes.Length; i++)
        {
            TypeTreeNode node = structNode.SubNodes[i];
            string path = parentPath.Length == 0 ? node.Name : $"{parentPath}/{node.Name}";
            FieldInfo? field = ownerType is null ? null : TypeTreeFieldAccess.FindField(ownerType, node.Name);

            TypeTreeFieldStep step = BuildFieldStep(node, field, classID, path);
            step.Gate = TypeTreeOverrides.FindGate(classID, path);
            steps[i] = step;
        }
        return steps;
    }

    private static TypeTreeFieldStep BuildFieldStep(TypeTreeNode node, FieldInfo? field, ClassIDType classID, string path)
    {
        bool capture = TypeTreeOverrides.ShouldCapture(classID, path);

        if (field is null)
        {
            // Nothing on the AssetRipper side holds this node. Capture it when a hook asked for it,
            // otherwise consume its bytes so the stream stays aligned with the rest of the layout.
            return capture
                ? new TypeTreeCaptureFieldStep(node, path)
                : new TypeTreeFilledFieldStep(node, path, null, new TypeTreeDiscardStep(node));
        }

        Type fieldType = field.FieldType;

        if (IsAssignedInPlaceOfRead(fieldType))
        {
            if (TryBuildScalarReader(node, fieldType, out Delegate? read, out bool align))
            {
                return CreateScalarStep(node, path, fieldType, read!, align, field, classID, capture);
            }

            Warn($"{Describe(field)} cannot hold node '{node.OriginalName}' ({node.TypeName}); reading and discarding it.");
            return new TypeTreeFilledFieldStep(node, path, null, new TypeTreeDiscardStep(node));
        }

        if (capture)
        {
            throw new NotSupportedException(
                $"[TypeTree] '{path}' is bound to {Describe(field)}; capture is only supported for nodes the stock classes have no field for.");
        }

        TypeTreeStep? inner = TryBuildInPlaceStep(node, fieldType, classID, path);
        if (inner is null)
        {
            Warn($"{Describe(field)} cannot hold node '{node.OriginalName}' ({node.TypeName}); reading and discarding it.");
            return new TypeTreeFilledFieldStep(node, path, null, new TypeTreeDiscardStep(node));
        }

        return new TypeTreeFilledFieldStep(node, path, TypeTreeFieldAccess.CreateReferenceGetter(field), inner);
    }

    /// <summary>
    /// 1:1 port of <c>Pass100_FillReadMethods.IsArrayOrPrimitive</c>: these field types are assigned
    /// from a returned value, everything else is filled in place.
    /// </summary>
    private static bool IsAssignedInPlaceOfRead(Type type) => type.IsArray || type.IsPrimitive || type == typeof(Utf8String);

    private static bool TryBuildScalarReader(TypeTreeNode node, Type fieldType, out Delegate? read, out bool align)
    {
        read = null;
        align = false;

        switch (node.NodeType)
        {
            case TypeTreeNodeType.TypelessData:
                if (fieldType != typeof(byte[])) return false;
                read = TypeTreePrimitives.GetByteArrayReader();
                align = node.AlignBytes;
                return true;

            case TypeTreeNodeType.Vector:
            case TypeTreeNodeType.Array:
            {
                // Pass100 routes a byte-element sequence through the TypelessData reader whenever the
                // generated field is a byte[]; anything else was an outright NotSupportedException.
                if (fieldType != typeof(byte[])) return false;
                TypeTreeNode arrayNode = node.NodeType == TypeTreeNodeType.Vector ? node.SubNodes[0] : node;
                TypeTreeNode elementNode = arrayNode.SubNodes[1];
                if (elementNode.NodeType is not (TypeTreeNodeType.UInt8 or TypeTreeNodeType.Int8)) return false;
                read = TypeTreePrimitives.GetByteArrayReader();
                align = node.NodeType == TypeTreeNodeType.Vector ? node.AlignBytes || arrayNode.AlignBytes : node.AlignBytes;
                return true;
            }

            default:
                if (!TypeTreePrimitives.IsPrimitive(node.NodeType)) return false;
                if (TypeTreePrimitives.GetClrType(node.NodeType) != fieldType) return false;
                read = TypeTreePrimitives.GetReader(node.NodeType);
                align = node.AlignBytes;
                return true;
        }
    }

    private static TypeTreeFieldStep CreateScalarStep(
        TypeTreeNode node,
        string path,
        Type valueType,
        Delegate read,
        bool align,
        FieldInfo field,
        ClassIDType classID,
        bool capture)
    {
        object setter = typeof(TypeTreeFieldAccess)
            .GetMethod(nameof(TypeTreeFieldAccess.CreateSetter))!
            .MakeGenericMethod(valueType)
            .Invoke(null, [field])!;

        Delegate? valueFix = TypeTreeOverrides.FindValueFix(classID, path);
        if (valueFix is not null && valueFix.GetType() != typeof(Func<,>).MakeGenericType(valueType, valueType))
        {
            throw new InvalidOperationException(
                $"[TypeTree] The value fix for '{path}' must be Func<{valueType.Name}, {valueType.Name}>, not {valueFix.GetType().Name}.");
        }

        return (TypeTreeFieldStep)Activator.CreateInstance(
            typeof(TypeTreeScalarFieldStep<>).MakeGenericType(valueType),
            [node, path, read, align, setter, valueFix, capture])!;
    }

    /// <remarks>
    /// <paramref name="path"/> is the node's own path and becomes the parent path for anything nested
    /// inside it, so a gate or capture can address <c>m_Shapes/m_Vertices</c>. Elements of a sequence
    /// share their sequence's path -- overrides inside a repeated element are not addressable, which
    /// no known deviation needs.
    /// </remarks>
    private static TypeTreeStep? TryBuildInPlaceStep(TypeTreeNode node, Type fieldType, ClassIDType classID, string path)
    {
        switch (node.NodeType)
        {
            case TypeTreeNodeType.Type:
                if (fieldType.IsValueType) return null;
                return new TypeTreeStructStep(BuildFields(node, fieldType, classID, path), node.AlignBytes);

            case TypeTreeNodeType.Vector:
            {
                TypeTreeNode arrayNode = node.SubNodes[0];
                return BuildSequenceStep(arrayNode.SubNodes[1], fieldType, node.AlignBytes || arrayNode.AlignBytes, classID, path);
            }

            case TypeTreeNodeType.Array:
                return BuildSequenceStep(node.SubNodes[1], fieldType, node.AlignBytes, classID, path);

            case TypeTreeNodeType.Map:
            {
                TypeTreeNode arrayNode = node.SubNodes[0];
                TypeTreeNode pairNode = arrayNode.SubNodes[1];
                if (!TryGetGenericArguments(fieldType, typeof(AssetDictionary<,>), out Type[]? arguments)) return null;

                TypeTreeStep? pair = BuildPairStep(pairNode, arguments![0], arguments[1], classID, path);
                if (pair is null) return null;

                return (TypeTreeStep)Activator.CreateInstance(
                    typeof(TypeTreeDictionaryStep<,>).MakeGenericType(arguments[0], arguments[1]),
                    [pair, node.AlignBytes || arrayNode.AlignBytes])!;
            }

            case TypeTreeNodeType.Pair:
            {
                if (!TryGetGenericArguments(fieldType, typeof(AssetPair<,>), out Type[]? arguments)) return null;
                return BuildPairStep(node, arguments![0], arguments[1], classID, path);
            }

            default:
                return null;
        }
    }

    private static TypeTreeStep? BuildSequenceStep(TypeTreeNode elementNode, Type fieldType, bool align, ClassIDType classID, string path)
    {
        if (!TryGetGenericArguments(fieldType, typeof(AssetList<>), out Type[]? arguments))
        {
            return null;
        }

        Type elementType = arguments![0];

        if (IsAssignedInPlaceOfRead(elementType))
        {
            if (!TryBuildScalarReader(elementNode, elementType, out Delegate? read, out bool elementAlign))
            {
                return null;
            }
            return (TypeTreeStep)Activator.CreateInstance(
                typeof(TypeTreePrimitiveListStep<>).MakeGenericType(elementType),
                [read!, elementAlign, align])!;
        }

        TypeTreeStep? element = TryBuildInPlaceStep(elementNode, elementType, classID, path);
        if (element is null)
        {
            return null;
        }

        return (TypeTreeStep)Activator.CreateInstance(
            typeof(TypeTreeAssetListStep<>).MakeGenericType(elementType),
            [element, align])!;
    }

    private static TypeTreeStep? BuildPairStep(TypeTreeNode pairNode, Type keyType, Type valueType, ClassIDType classID, string path)
    {
        if (pairNode.SubNodes.Length != 2)
        {
            return null;
        }

        if (!TryBuildPairMember(pairNode.SubNodes[0], keyType, classID, path, out Delegate? readKey, out bool keyAlign, out TypeTreeStep? keyStep)) return null;
        if (!TryBuildPairMember(pairNode.SubNodes[1], valueType, classID, path, out Delegate? readValue, out bool valueAlign, out TypeTreeStep? valueStep)) return null;

        return (TypeTreeStep)Activator.CreateInstance(
            typeof(TypeTreePairStep<,>).MakeGenericType(keyType, valueType),
            [readKey, keyAlign, keyStep, readValue, valueAlign, valueStep, pairNode.AlignBytes])!;
    }

    private static bool TryBuildPairMember(TypeTreeNode node, Type memberType, ClassIDType classID, string path, out Delegate? read, out bool align, out TypeTreeStep? step)
    {
        step = null;
        if (IsAssignedInPlaceOfRead(memberType))
        {
            return TryBuildScalarReader(node, memberType, out read, out align);
        }

        read = null;
        align = false;
        step = TryBuildInPlaceStep(node, memberType, classID, path);
        return step is not null;
    }

    private static bool TryGetGenericArguments(Type type, Type openGeneric, out Type[]? arguments)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric)
        {
            arguments = type.GetGenericArguments();
            return true;
        }
        arguments = null;
        return false;
    }

    private static readonly HashSet<string> WarnedOnce = new(StringComparer.Ordinal);

    private static void Warn(string message)
    {
        lock (WarnedOnce)
        {
            if (!WarnedOnce.Add(message))
            {
                return;
            }
        }
        HookLogger.LogRaw($"    [TypeTree] {message}");
    }

    private static string Describe(FieldInfo field) => $"{field.DeclaringType?.Name}.{field.Name} ({field.FieldType.Name})";
}
