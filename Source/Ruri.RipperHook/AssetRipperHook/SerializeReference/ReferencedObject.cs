using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

public sealed class ReferencedObject : UnityAssetBase, IDeepCloneable
{
    public long Rid { get; set; }

    public ReferencedManagedType Type { get; set; } = new();

    public IUnityAssetBase? Data { get; set; }

    public override void WalkEditor(AssetWalker walker)
    {
        if (!walker.EnterAsset(this))
        {
            return;
        }
        if (walker.EnterField(this, "rid"))
        {
            walker.VisitPrimitive(Rid);
            walker.ExitField(this, "rid");
        }
        walker.DivideAsset(this);
        if (walker.EnterField(this, "type"))
        {
            Type.WalkEditor(walker);
            walker.ExitField(this, "type");
        }
        walker.DivideAsset(this);
        if (walker.EnterField(this, "data"))
        {
            (Data ?? EmptyReferencedObjectData.Instance).WalkEditor(walker);
            walker.ExitField(this, "data");
        }
        walker.ExitAsset(this);
    }

    public override void WalkRelease(AssetWalker walker) => WalkEditor(walker);

    public override void WalkStandard(AssetWalker walker) => WalkEditor(walker);

    public override IEnumerable<(string, PPtr)> FetchDependencies()
    {
        if (Data is null)
        {
            yield break;
        }
        foreach ((string, PPtr) pair in Data.FetchDependencies())
        {
            yield return pair;
        }
    }

    public override void Reset()
    {
        Rid = 0;
        Type.Reset();
        Data = null;
    }

    public override void CopyValues(IUnityAssetBase? source, PPtrConverter converter)
    {
        if (source is not ReferencedObject other)
        {
            Reset();
            return;
        }
        Rid = other.Rid;
        Type.CopyValues(other.Type, converter);
        Data = other.Data is IDeepCloneable cloneable ? cloneable.DeepClone(converter) : null;
    }

    public ReferencedObject DeepClone(PPtrConverter converter)
    {
        ReferencedObject clone = new();
        clone.CopyValues(this, converter);
        return clone;
    }

    IUnityAssetBase IDeepCloneable.DeepClone(PPtrConverter converter) => DeepClone(converter);

    public static ReferencedObject? Read(
        ref EndianSpanReader reader,
        UnityVersion version,
        TransferInstructionFlags flags,
        int depth,
        IReadOnlyList<SerializedTypeReference> refTypes,
        IAssemblyManager? assemblyManager,
        long? explicitRid,
        long fallbackRid)
    {
        ReferencedManagedType type = ReferencedManagedType.Read(ref reader);
        if (type.IsTerminus)
        {
            return null;
        }

        ReferencedObject referencedObject = new()
        {
            Rid = explicitRid ?? fallbackRid,
            Type = type,
        };
        if (referencedObject.Rid is ManagedReferencesRegistry.UnknownRid or ManagedReferencesRegistry.NullRid
            || type.IsEmpty)
        {
            referencedObject.Data = null;
            return referencedObject;
        }

        referencedObject.Data = ReadData(ref reader, version, flags, depth, type, refTypes, assemblyManager);
        return referencedObject;
    }

    private static IUnityAssetBase ReadData(
        ref EndianSpanReader reader,
        UnityVersion version,
        TransferInstructionFlags flags,
        int depth,
        ReferencedManagedType type,
        IReadOnlyList<SerializedTypeReference> refTypes,
        IAssemblyManager? assemblyManager)
    {
        SerializedTypeReference? refType = FindRefType(refTypes, type);
        if (refType is not null && TypeTreeNodeStruct.TryMakeFromTypeTree(refType.OldType, out TypeTreeNodeStruct rootNode))
        {
            SerializableStructure structure = SerializableTreeType.FromRootNode(rootNode).CreateSerializableStructure();
            SerializeReferenceContext.ReadNested(structure, ref reader, version, flags);
            return structure;
        }

        if (assemblyManager is not null && !type.IsEmpty)
        {
            string assemblyName = SpecialFileNames.FixAssemblyName(type.AsmName);
            ScriptIdentifier scriptID = assemblyManager.GetScriptID(assemblyName, type.Namespace, type.ClassName);
            if (assemblyManager.TryGetSerializableType(scriptID, version, out SerializableType? serializableType, out _))
            {
                SerializableStructure structure = serializableType.CreateSerializableStructure();
                SerializeReferenceContext.ReadNested(structure, ref reader, version, flags);
                return structure;
            }
        }

        throw new InvalidDataException(
            $"Unable to resolve SerializeReference type [{type.AsmName}]{type.Namespace}.{type.ClassName}.");
    }

    private static SerializedTypeReference? FindRefType(
        IReadOnlyList<SerializedTypeReference> refTypes, ReferencedManagedType type)
    {
        foreach (SerializedTypeReference refType in refTypes)
        {
            if (refType.ClassName == type.ClassName
                && refType.Namespace == type.Namespace
                && refType.AsmName == type.AsmName)
            {
                return refType;
            }
        }
        return null;
    }
}

public sealed class ReferencedManagedType : UnityAssetBase, IDeepCloneable
{
    public string ClassName { get; set; } = "";

    public string Namespace { get; set; } = "";

    public string AsmName { get; set; } = "";

    public bool IsEmpty =>
        string.IsNullOrEmpty(ClassName) && string.IsNullOrEmpty(Namespace) && string.IsNullOrEmpty(AsmName);

    public bool IsTerminus =>
        ClassName == ManagedReferencesRegistry.TerminusClass
        && Namespace == ManagedReferencesRegistry.TerminusNamespace
        && AsmName == ManagedReferencesRegistry.TerminusAssembly;

    public override bool FlowMappedInYaml => true;

    public override void WalkEditor(AssetWalker walker)
    {
        if (!walker.EnterAsset(this))
        {
            return;
        }
        if (walker.EnterField(this, "class"))
        {
            walker.VisitPrimitive(ClassName);
            walker.ExitField(this, "class");
        }
        walker.DivideAsset(this);
        if (walker.EnterField(this, "ns"))
        {
            walker.VisitPrimitive(Namespace);
            walker.ExitField(this, "ns");
        }
        walker.DivideAsset(this);
        if (walker.EnterField(this, "asm"))
        {
            walker.VisitPrimitive(AsmName);
            walker.ExitField(this, "asm");
        }
        walker.ExitAsset(this);
    }

    public override void WalkRelease(AssetWalker walker) => WalkEditor(walker);

    public override void WalkStandard(AssetWalker walker) => WalkEditor(walker);

    public override void Reset()
    {
        ClassName = "";
        Namespace = "";
        AsmName = "";
    }

    public override void CopyValues(IUnityAssetBase? source, PPtrConverter converter)
    {
        if (source is not ReferencedManagedType other)
        {
            Reset();
            return;
        }
        ClassName = other.ClassName;
        Namespace = other.Namespace;
        AsmName = other.AsmName;
    }

    public ReferencedManagedType DeepClone(PPtrConverter converter)
    {
        ReferencedManagedType clone = new();
        clone.CopyValues(this, converter);
        return clone;
    }

    IUnityAssetBase IDeepCloneable.DeepClone(PPtrConverter converter) => DeepClone(converter);

    public static ReferencedManagedType Read(ref EndianSpanReader reader)
    {
        return new ReferencedManagedType
        {
            ClassName = ReadAlignedString(ref reader),
            Namespace = ReadAlignedString(ref reader),
            AsmName = ReadAlignedString(ref reader),
        };
    }

    private static string ReadAlignedString(ref EndianSpanReader reader)
    {
        Utf8String value = reader.ReadUtf8String();
        reader.Align();
        return value.String;
    }
}

internal sealed class EmptyReferencedObjectData : UnityAssetBase
{
    public static EmptyReferencedObjectData Instance { get; } = new();

    public override void WalkEditor(AssetWalker walker)
    {
        if (walker.EnterAsset(this))
        {
            walker.ExitAsset(this);
        }
    }

    public override void WalkRelease(AssetWalker walker) => WalkEditor(walker);

    public override void WalkStandard(AssetWalker walker) => WalkEditor(walker);

    public override void Reset()
    {
    }
}
