using AssetRipper.AssemblyDumper;
using AssetRipper.AssemblyDumper.Passes;
using AssetRipper.AssemblyDumper.Utils;
using AssetRipper.Primitives;
using AssetRipper.Tpk;
using AssetRipper.Tpk.Shared;
using AssetRipper.Tpk.TypeTrees;

namespace Ruri.Tpk.Pipeline;

internal static class TypeTreeRenamer
{
    public static TpkTypeTreeBlob ApplyAssetRipperRenaming(TpkTypeTreeBlob blob)
    {
        Dictionary<int, VersionedList<UniversalClass>> classes = ToUniversalClasses(blob);

        PrepareSharedStateWorkingDirectory();

        SharedState.Initialize(
            blob.Versions.ToArray(),
            classes,
            TpkFile.FromBlob(blob, TpkCompressionType.Brotli).WriteToMemory());

        Pass002_RenameSubnodes.DoPass();

        return ToBlob(blob, classes);
    }

    private static void PrepareSharedStateWorkingDirectory()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        const string historyFileName = "consolidated.json";
        if (!File.Exists(historyFileName))
        {
            File.WriteAllText(historyFileName, "{}");
        }
    }

    private static Dictionary<int, VersionedList<UniversalClass>> ToUniversalClasses(TpkTypeTreeBlob blob)
    {
        Dictionary<int, VersionedList<UniversalClass>> classes = new();
        foreach (TpkClassInformation classInformation in blob.ClassInformation)
        {
            VersionedList<UniversalClass> classList = new();
            classes.Add(classInformation.ID, classList);
            foreach (KeyValuePair<UnityVersion, TpkUnityClass?> pair in classInformation.Classes)
            {
                classList.Add(
                    pair.Key,
                    pair.Value is null
                        ? null
                        : UniversalClass.FromTpkUnityClass(pair.Value, classInformation.ID, blob.StringBuffer, blob.NodeBuffer));
            }
        }
        return classes;
    }

    private static TpkTypeTreeBlob ToBlob(TpkTypeTreeBlob source, Dictionary<int, VersionedList<UniversalClass>> classes)
    {
        TpkTypeTreeBlob result = new();
        result.Versions.AddRange(source.Versions);
        result.CreationTime = source.CreationTime;

        foreach (KeyValuePair<UnityVersion, TpkCommonString.Entry[]> pair in source.CommonString.VersionInformation)
        {
            TpkCommonString.Entry[] entries = new TpkCommonString.Entry[pair.Value.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new TpkCommonString.Entry(pair.Value[i].Offset, source.StringBuffer[pair.Value[i].String], result.StringBuffer);
            }
            result.CommonString.Add(pair.Key, entries);
        }

        foreach (TpkClassInformation sourceClassInformation in source.ClassInformation)
        {
            VersionedList<UniversalClass> classList = classes[sourceClassInformation.ID];
            TpkClassInformation classInformation = new(sourceClassInformation.ID);
            for (int i = 0; i < classList.Count; i++)
            {
                KeyValuePair<UnityVersion, UniversalClass?> pair = classList[i];
                classInformation.Classes.Add(new KeyValuePair<UnityVersion, TpkUnityClass?>(
                    pair.Key,
                    pair.Value is null ? null : Convert(pair.Value, result.StringBuffer, result.NodeBuffer)));
            }
            result.ClassInformation.Add(classInformation);
        }

        return result;
    }

    private static TpkUnityClass Convert(UniversalClass source, TpkStringBuffer stringBuffer, TpkUnityNodeBuffer nodeBuffer)
    {
        TpkUnityClass result = new()
        {
            Name = stringBuffer.AddString(source.Name),
            Base = stringBuffer.AddString(source.BaseString ?? ""),
            Flags = GetFlags(source),
        };
        if (source.EditorRootNode is not null)
        {
            result.EditorRootNode = Convert(source.EditorRootNode, stringBuffer, nodeBuffer);
        }
        if (source.ReleaseRootNode is not null)
        {
            result.ReleaseRootNode = Convert(source.ReleaseRootNode, stringBuffer, nodeBuffer);
        }
        return result;
    }

    private static ushort Convert(UniversalNode node, TpkStringBuffer stringBuffer, TpkUnityNodeBuffer nodeBuffer)
    {
        TpkUnityNode result = new()
        {
            TypeName = stringBuffer.AddString(node.TypeName),
            Name = stringBuffer.AddString(node.Name),
            Version = node.Version,
            MetaFlag = (uint)node.MetaFlag,
            SubNodes = node.SubNodes.Count == 0 ? Array.Empty<ushort>() : new ushort[node.SubNodes.Count],
        };
        for (int i = 0; i < node.SubNodes.Count; i++)
        {
            result.SubNodes[i] = Convert(node.SubNodes[i], stringBuffer, nodeBuffer);
        }
        return nodeBuffer.AddNode(result);
    }

    private static TpkUnityClassFlags GetFlags(UniversalClass source)
    {
        TpkUnityClassFlags result = TpkUnityClassFlags.None;
        if (source.IsAbstract) result |= TpkUnityClassFlags.IsAbstract;
        if (source.IsSealed) result |= TpkUnityClassFlags.IsSealed;
        if (source.IsEditorOnly) result |= TpkUnityClassFlags.IsEditorOnly;
        if (source.IsReleaseOnly) result |= TpkUnityClassFlags.IsReleaseOnly;
        if (source.IsStripped) result |= TpkUnityClassFlags.IsStripped;
        if (source.EditorRootNode is not null) result |= TpkUnityClassFlags.HasEditorRootNode;
        if (source.ReleaseRootNode is not null) result |= TpkUnityClassFlags.HasReleaseRootNode;
        return result;
    }
}
