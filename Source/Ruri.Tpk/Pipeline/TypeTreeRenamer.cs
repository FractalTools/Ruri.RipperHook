using AssetRipper.AssemblyDumper;
using AssetRipper.AssemblyDumper.Passes;
using AssetRipper.AssemblyDumper.Utils;
using AssetRipper.Primitives;
using AssetRipper.Tpk;
using AssetRipper.Tpk.Shared;
using AssetRipper.Tpk.TypeTrees;

namespace Ruri.Tpk.Pipeline;

/// <summary>
/// Rewrites a freshly packed tpk into AssetRipper's own naming vocabulary by running AR's real
/// <c>Pass002_RenameSubnodes</c> over it.
///
/// This is not optional polish. AssetRipper names every generated field after its type tree node --
/// but only after Pass002 has rewritten a long list of them: <c>m_TextureFormat</c> becomes
/// <c>m_Format</c>, <c>image data</c> becomes <c>m_ImageData</c>, <c>VertexData.m_DataSize</c>
/// becomes <c>m_Data</c>, and every PPtr's <c>m_FileID</c>/<c>m_PathID</c> gain a trailing
/// underscore. A runtime interpreter that binds raw dump names straight onto those fields silently
/// drops every renamed node -- textures lose their format, and every asset reference reads as null.
///
/// Transcribing those ~500 lines into our own node model would just relocate the bug, so the real
/// pass runs here instead and the shipped tpk carries the post-rename names. That also keeps us in
/// step with upstream automatically: a rename added to AR is picked up by rebuilding the tpk.
///
/// <c>Pass000_ProcessTpk</c> is deliberately NOT used to load the blob -- it rewrites version keys
/// (<c>StripType</c>/<c>StripBuild</c>/...), which would destroy the custom-engine discriminator
/// that identifies a game's overlay (<c>2021.3.1404x5</c> -> EndField build 1404).
/// </summary>
internal static class TypeTreeRenamer
{
    public static TpkTypeTreeBlob ApplyAssetRipperRenaming(TpkTypeTreeBlob blob)
    {
        Dictionary<int, VersionedList<UniversalClass>> classes = ToUniversalClasses(blob);

        PrepareSharedStateWorkingDirectory();

        SharedState.Initialize(
            blob.Versions.ToArray(),
            classes,
            UniversalCommonString.FromBlob(blob),
            TpkFile.FromBlob(blob, TpkCompressionType.Brotli).WriteToMemory());

        Pass002_RenameSubnodes.DoPass();

        return ToBlob(blob, classes);
    }

    /// <summary>
    /// <see cref="SharedState"/>'s constructor resolves its reference modules and a documentation
    /// history file relative to the working directory. Only the renaming pass is being run, and it
    /// reads neither, so point the working directory at our own output (where the AssetRipper
    /// assemblies already sit) and stub the history file rather than dragging the whole codegen
    /// artifact set back in.
    /// </summary>
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

    /// <summary>
    /// Repacks the renamed trees. The version list and the per-class version keys are carried over
    /// untouched; only names change, so the runtime lookup keys stay exactly as the packer produced.
    /// </summary>
    private static TpkTypeTreeBlob ToBlob(TpkTypeTreeBlob source, Dictionary<int, VersionedList<UniversalClass>> classes)
    {
        TpkTypeTreeBlob result = new();
        result.Versions.AddRange(source.Versions);
        result.CreationTime = source.CreationTime;

        foreach (KeyValuePair<UnityVersion, byte> pair in source.CommonString.VersionInformation)
        {
            result.CommonString.Add(pair.Key, pair.Value);
        }
        result.CommonString.SetIndices(result.StringBuffer, source.CommonString.GetStrings(source.StringBuffer).ToList());

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

    /// <summary>Exact inverse of <c>UniversalNode.FromTpkUnityNode</c>.</summary>
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
