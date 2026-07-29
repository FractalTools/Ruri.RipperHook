using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
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

/// <summary>
/// Unity <c>[SerializeReference]</c> 用的 <c>ManagedReferencesRegistry</c> 的运行期形态。
/// 移植自 AssetRipper PR #2313 / #2238(作者以代码质量为由未合,功能本身是对的)。
/// </summary>
public sealed class ManagedReferencesRegistry : UnityAssetBase, IDeepCloneable
{
    public const long UnknownRid = -1;
    public const long NullRid = -2;
    public const string TerminusClass = "Terminus";
    public const string TerminusNamespace = "UnityEngine.DMAT";
    public const string TerminusAssembly = "FAKE_ASM";

    public int Version { get; set; } = 2;

    public List<ReferencedObject> RefIds { get; } = new();

    public override void WalkEditor(AssetWalker walker)
    {
        if (!walker.EnterAsset(this))
        {
            return;
        }
        if (walker.EnterField(this, "version"))
        {
            walker.VisitPrimitive(Version);
            walker.ExitField(this, "version");
        }
        walker.DivideAsset(this);
        if (walker.EnterField(this, "RefIds"))
        {
            if (walker.EnterList(RefIds))
            {
                for (int i = 0; i < RefIds.Count; i++)
                {
                    if (i > 0)
                    {
                        walker.DivideList(RefIds);
                    }
                    RefIds[i].WalkEditor(walker);
                }
                walker.ExitList(RefIds);
            }
            walker.ExitField(this, "RefIds");
        }
        walker.ExitAsset(this);
    }

    public override void WalkRelease(AssetWalker walker) => WalkEditor(walker);

    public override void WalkStandard(AssetWalker walker) => WalkEditor(walker);

    public override IEnumerable<(string, PPtr)> FetchDependencies()
    {
        foreach (ReferencedObject referencedObject in RefIds)
        {
            foreach ((string, PPtr) pair in referencedObject.FetchDependencies())
            {
                yield return pair;
            }
        }
    }

    public override void Reset()
    {
        Version = 2;
        RefIds.Clear();
    }

    public override void CopyValues(IUnityAssetBase? source, PPtrConverter converter)
    {
        if (source is not ManagedReferencesRegistry other)
        {
            Reset();
            return;
        }
        Version = other.Version;
        RefIds.Clear();
        foreach (ReferencedObject referencedObject in other.RefIds)
        {
            RefIds.Add(referencedObject.DeepClone(converter));
        }
    }

    public ManagedReferencesRegistry DeepClone(PPtrConverter converter)
    {
        ManagedReferencesRegistry clone = new();
        clone.CopyValues(this, converter);
        return clone;
    }

    IUnityAssetBase IDeepCloneable.DeepClone(PPtrConverter converter) => DeepClone(converter);

    /// <summary>v1 靠 Terminus 哨兵终止,v2 是 count 前缀 + 显式 rid。两种都要认。</summary>
    public static ManagedReferencesRegistry Read(
        ref EndianSpanReader reader,
        UnityVersion version,
        TransferInstructionFlags flags,
        int depth,
        IReadOnlyList<SerializedTypeReference> refTypes,
        IAssemblyManager? assemblyManager)
    {
        ManagedReferencesRegistry registry = new() { Version = reader.ReadInt32() };
        if (registry.Version == 1)
        {
            int index = 0;
            while (true)
            {
                ReferencedObject? referencedObject = ReferencedObject.Read(
                    ref reader, version, flags, depth, refTypes, assemblyManager,
                    explicitRid: null, fallbackRid: index++);
                if (referencedObject is null)
                {
                    break;
                }
                registry.RefIds.Add(referencedObject);
            }
        }
        else if (registry.Version == 2)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                long rid = reader.ReadInt64();
                ReferencedObject? referencedObject = ReferencedObject.Read(
                    ref reader, version, flags, depth, refTypes, assemblyManager,
                    explicitRid: rid, fallbackRid: rid);
                if (referencedObject is not null)
                {
                    registry.RefIds.Add(referencedObject);
                }
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported ManagedReferencesRegistry version {registry.Version}.");
        }
        return registry;
    }
}
