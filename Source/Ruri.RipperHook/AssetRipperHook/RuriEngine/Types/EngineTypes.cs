using System.Reflection;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;
using AssetRipper.Tpk;
using AssetRipper.Tpk.TypeTrees;
using TypeTreeFallbackHook = Ruri.RipperHook.HookUtils.TypeTreeFallbackHook.TypeTreeFallbackHook;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class EngineClass
{
    private readonly TpkTypeTreeBlob blob;
    private readonly TpkUnityNode root;
    private readonly Lazy<TypeTreeNodeStruct> tree;
    private readonly Lazy<StructShape> shape;
    private readonly Lazy<SerializableType> rootLayout;
    private readonly Lazy<SerializableType> referencedLayout;

    public EngineClass(string fullName, string baseName, TpkTypeTreeBlob blob, TpkUnityNode root)
    {
        FullName = fullName;
        BaseName = baseName;
        this.blob = blob;
        this.root = root;
        tree = new Lazy<TypeTreeNodeStruct>(() => TypeTreeFallbackHook.Convert(this.root, this.blob));
        shape = new Lazy<StructShape>(() => EngineShapes.OfClass(Tree));
        rootLayout = new Lazy<SerializableType>(() => SerializableTreeType.FromRootNode(Tree));
        referencedLayout = new Lazy<SerializableType>(() => SerializableTreeType.FromReferencedObjectNode(Tree));
    }

    public string FullName { get; }

    public string BaseName { get; }

    public TypeTreeNodeStruct Tree => tree.Value;

    public StructShape Shape => shape.Value;

    public SerializableType RootLayout => rootLayout.Value;

    public SerializableType ReferencedLayout => referencedLayout.Value;
}

internal sealed class EngineTypeResolver(EngineTypes types) : ITypeResolver
{
    public bool TryGetSerializableType(
        ScriptIdentifier scriptID,
        UnityVersion version,
        [NotNullWhen(true)] out SerializableType? scriptType,
        [NotNullWhen(false)] out string? failureReason)
    {
        if (ScriptIdentity.TryResolve(types, scriptID.Namespace, scriptID.Name, out EngineClass? engineClass, out string reason))
        {
            scriptType = engineClass.ReferencedLayout;
            failureReason = null;
            return true;
        }
        scriptType = null;
        failureReason = reason;
        return false;
    }
}

internal sealed class EngineTypes
{
    public const string ResourceName = "RuriEngine.tpk";

    private readonly Dictionary<string, EngineClass> classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> bySimpleName = new(StringComparer.Ordinal);

    private EngineTypes()
    {
    }

    public int Count => classes.Count;

    public static EngineTypes Load(string? overridePath)
    {
        using Stream stream = OpenTpk(overridePath, out string origin);
        TpkDataBlob root = TpkFile.FromStream(stream).GetDataBlob();
        if (root is not TpkCollectionBlob collection)
        {
            throw new InvalidDataException($"[RuriEngine] {origin} holds a {root.DataType} blob; expected a Collection. Repack it with Ruri.Tpk.");
        }

        EngineTypes types = new();
        foreach (KeyValuePair<string, TpkDataBlob> pair in collection.Blobs)
        {
            if (pair.Value is TpkTypeTreeBlob blob)
            {
                types.Add(blob);
            }
        }
        return types;
    }

    public bool TryGet(string fullName, [NotNullWhen(true)] out EngineClass? engineClass) => classes.TryGetValue(fullName, out engineClass);

    public IReadOnlyList<string> FullNamesOfSimpleName(string simpleName)
    {
        return bySimpleName.TryGetValue(simpleName, out List<string>? found) ? found : [];
    }

    private void Add(TpkTypeTreeBlob blob)
    {
        foreach (TpkClassInformation information in blob.ClassInformation)
        {
            foreach (KeyValuePair<UnityVersion, TpkUnityClass?> entry in information.Classes)
            {
                if (entry.Value is not { } unityClass)
                {
                    continue;
                }
                string fullName = blob.StringBuffer[unityClass.Name];
                string baseName = blob.StringBuffer[unityClass.Base];
                classes[fullName] = new EngineClass(fullName, baseName, blob, blob.NodeBuffer[unityClass.ReleaseRootNode]);
                string simpleName = SimpleNameOf(fullName);
                if (!bySimpleName.TryGetValue(simpleName, out List<string>? names))
                {
                    names = new List<string>();
                    bySimpleName[simpleName] = names;
                }
                names.Add(fullName);
                break;
            }
        }
    }

    public static string SimpleNameOf(string fullName)
    {
        int separator = fullName.LastIndexOf('.');
        return separator < 0 ? fullName : fullName[(separator + 1)..];
    }

    private static Stream OpenTpk(string? overridePath, out string origin)
    {
        if (overridePath is { Length: > 0 })
        {
            origin = overridePath;
            return File.OpenRead(overridePath);
        }
        Assembly assembly = typeof(EngineTypes).Assembly;
        origin = $"embedded:{ResourceName}";
        return assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"[RuriEngine] {ResourceName} is not embedded in {assembly.GetName().Name}. Build it with Ruri.Tpk --ruriengine.");
    }
}
