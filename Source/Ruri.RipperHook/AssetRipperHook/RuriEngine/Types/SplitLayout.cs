using System.Collections.Concurrent;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.RuriEngine;

internal static class SplitLayout
{
    private static readonly ConcurrentDictionary<string, SerializableType> Cache = new(StringComparer.Ordinal);

    public static SerializableType RootOf(EngineTypes types, RuriEngineMapping mapping, EngineClass engineClass)
    {
        SplitRow? split = mapping.SplitOf(engineClass.FullName);
        if (split is null)
        {
            return engineClass.RootLayout;
        }
        return Cache.GetOrAdd(engineClass.FullName, _ => Merge(types, mapping, engineClass, split));
    }

    private static SerializableTreeType Merge(EngineTypes types, RuriEngineMapping mapping, EngineClass engineClass, SplitRow split)
    {
        List<TypeTreeNodeStruct> nodes = new();
        TypeTreeNodeStruct? registry = null;

        void Take(TypeTreeNodeStruct tree)
        {
            foreach (TypeTreeNodeStruct node in tree.SubNodes)
            {
                if (node.IsManagedReferencesRegistry)
                {
                    registry ??= node;
                    continue;
                }
                nodes.Add(node);
            }
        }

        Take(engineClass.Tree);
        foreach (SplitGroup group in split.Groups)
        {
            if (types.TryGet(group.Component, out EngineClass? groupClass))
            {
                Take(groupClass.Tree);
            }
        }
        if (split.Identity is not null && types.TryGet(mapping.IdentityComponent, out EngineClass? identityClass))
        {
            TypeTreeNodeStruct named = identityClass.Tree.SubNodes.First(node => node.Name == mapping.IdentityField);
            nodes.Add(new TypeTreeNodeStruct(named.TypeName, split.Identity, named.Version, named.MetaFlag, named.SubNodes.ToArray()));
        }
        if (registry is { } found)
        {
            nodes.Add(found);
        }
        TypeTreeNodeStruct root = engineClass.Tree;
        return SerializableTreeType.FromRootNode(new TypeTreeNodeStruct(root.TypeName, root.Name, root.Version, root.MetaFlag, nodes.ToArray()));
    }
}
