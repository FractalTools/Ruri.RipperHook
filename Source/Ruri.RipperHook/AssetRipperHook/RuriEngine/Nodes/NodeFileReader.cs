using AssetRipper.Primitives;

namespace Ruri.RipperHook.RuriEngine;

internal static class NodeFileReader
{
    private const int GameObjectClassId = 1;
    private const int TransformClassId = 4;
    private const int RectTransformClassId = 224;
    private const int MonoBehaviourClassId = 114;
    private const string SceneExtension = ".unity";
    private const string PrefabExtension = ".prefab";
    private const string ComponentsKey = "m_Component";
    private const string ComponentKey = "component";
    private const string OwnerKey = "m_GameObject";
    private const string FatherKey = "m_Father";
    private const string ChildrenKey = "m_Children";
    private const string RootOrderKey = "m_RootOrder";
    private const string ScriptKey = "m_Script";
    private const string GuidKey = "guid";

    public static bool IsNodeFile(ProjectEntry entry)
    {
        return entry.RelativePath.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.EndsWith(PrefabExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static void Read(UnityYamlAssetCollection collection, List<UnityYamlDocument> documents, ProjectIndex index, UnityVersion version, GapReport gaps)
    {
        string file = collection.Entry.RelativePath;
        RecordTable records = RuriEngineSession.Records;
        List<long> gameObjectOrder = new();
        Dictionary<long, UnityYamlMapping> gameObjects = new();
        Dictionary<long, (int ClassId, UnityYamlMapping Body)> components = new();
        foreach (UnityYamlDocument document in documents)
        {
            if (document.Stripped)
            {
                continue;
            }
            if (document.Root is not UnityYamlMapping { Entries.Count: 1 } wrapper || wrapper.Entries[0].Value is not UnityYamlMapping body)
            {
                gaps.Add(GapCategories.UnreadableFile, "document without a body", file);
                continue;
            }
            if (document.ClassId == GameObjectClassId)
            {
                gameObjects.Add(document.FileId, body);
                gameObjectOrder.Add(document.FileId);
            }
            else if (YamlNodes.FileId(YamlNodes.Optional(body, OwnerKey)) != 0)
            {
                components.Add(document.FileId, (document.ClassId, body));
            }
            else
            {
                gaps.Add(GapCategories.UnmappedClass, UnityProjectReader.ClassNameOf(document.ClassId), file);
            }
        }

        Dictionary<long, NodeObject> objects = new();
        Dictionary<long, long> ownerOfTransform = new();
        foreach (long fileId in gameObjectOrder)
        {
            NodeObject? node = Assemble(fileId, gameObjects[fileId], components, index, records, gaps, file, ownerOfTransform);
            if (node is not null)
            {
                objects.Add(fileId, node);
            }
        }

        List<NodeObject> ordered = new();
        foreach (NodeObject root in Link(gameObjectOrder, objects, ownerOfTransform))
        {
            Flatten(root, ordered);
        }
        if (ordered.Count != objects.Count)
        {
            gaps.Add(GapCategories.UnreadableFile, "game objects not reachable from a root", $"{file}: {objects.Count - ordered.Count}");
        }
        if (ordered.Count == 0)
        {
            return;
        }

        Dictionary<long, long> hosts = new();
        Dictionary<long, string> componentTypes = new();
        foreach (NodeObject node in ordered)
        {
            Place(node, hosts, componentTypes);
        }
        collection.Nodes = new NodeFile(ordered[0].FileId, ordered, hosts, componentTypes);
        UnityProjectReader.AddTypeTreeAsset(collection, ordered[0].FileId, GameObjectClassId, version);
    }

    private static NodeObject? Assemble(
        long fileId,
        UnityYamlMapping body,
        Dictionary<long, (int ClassId, UnityYamlMapping Body)> components,
        ProjectIndex index,
        RecordTable records,
        GapReport gaps,
        string file,
        Dictionary<long, long> ownerOfTransform)
    {
        long transformId = 0;
        UnityYamlMapping? transform = null;
        List<NodeComponent> list = new();
        foreach (UnityYamlNode item in YamlNodes.Items(YamlNodes.Optional(body, ComponentsKey), ComponentsKey))
        {
            long componentId = YamlNodes.FileId(item is UnityYamlMapping reference ? YamlNodes.Optional(reference, ComponentKey) : null);
            if (!components.TryGetValue(componentId, out (int ClassId, UnityYamlMapping Body) document))
            {
                gaps.Add(GapCategories.UnreadableFile, "component missing from the file", file);
                continue;
            }
            if (document.ClassId is TransformClassId or RectTransformClassId)
            {
                transformId = componentId;
                transform = document.Body;
                continue;
            }
            string? className = ClassNameOf(document.ClassId, document.Body, index, gaps, file);
            if (className is null)
            {
                continue;
            }
            RecordClass? row = null;
            string? componentType = null;
            if (!records.TryFind(className, string.Empty, string.Empty, out RecordClass? found))
            {
                gaps.Add(GapCategories.UnmappedClass, $"{className} (component of a game object)", file);
            }
            else
            {
                componentType = found.Component;
                if (found.TargetOnly)
                {
                    gaps.Add(GapCategories.UnmappedClass, $"{className} (the record table names its component type only)", file);
                }
                else if (document.ClassId != MonoBehaviourClassId)
                {
                    gaps.Add(GapCategories.UnmappedClass, $"{className} (component of a game object)", file);
                }
                else
                {
                    row = found;
                }
            }
            list.Add(new NodeComponent { FileId = componentId, ClassName = className, Body = document.Body, Row = row, ComponentType = componentType });
        }
        if (transform is null)
        {
            gaps.Add(GapCategories.UnreadableFile, "game object without a transform", file);
            return null;
        }
        ownerOfTransform.Add(transformId, fileId);
        NodeObject node = new() { FileId = fileId, Body = body, TransformId = transformId, Transform = transform };
        node.Components.AddRange(list);
        return node;
    }

    private static string? ClassNameOf(int classId, UnityYamlMapping body, ProjectIndex index, GapReport gaps, string file)
    {
        if (classId != MonoBehaviourClassId)
        {
            return UnityProjectReader.ClassNameOf(classId);
        }
        string? guid = body.TryGet(ScriptKey, out UnityYamlNode? script) && script is UnityYamlMapping reference ? reference.TryGetText(GuidKey) : null;
        if (guid is null || !index.TryGetCollection(guid, out UnityYamlAssetCollection? scriptCollection) || scriptCollection.Entry.Kind != ProjectFileKind.Script)
        {
            gaps.Add(GapCategories.UnresolvedScript, guid ?? "(no script reference)", file);
            return null;
        }
        return System.IO.Path.GetFileNameWithoutExtension(scriptCollection.Entry.RelativePath);
    }

    private static List<NodeObject> Link(List<long> gameObjectOrder, Dictionary<long, NodeObject> objects, Dictionary<long, long> ownerOfTransform)
    {
        Dictionary<long, int> childOrder = new();
        foreach (NodeObject node in objects.Values)
        {
            int position = 0;
            foreach (UnityYamlNode child in YamlNodes.Items(YamlNodes.Optional(node.Transform, ChildrenKey), ChildrenKey))
            {
                childOrder[YamlNodes.FileId(child)] = position++;
            }
        }
        List<NodeObject> roots = new();
        foreach (long fileId in gameObjectOrder)
        {
            if (!objects.TryGetValue(fileId, out NodeObject? node))
            {
                continue;
            }
            long father = YamlNodes.FileId(YamlNodes.Optional(node.Transform, FatherKey));
            if (father != 0 && ownerOfTransform.TryGetValue(father, out long parentKey) && objects.TryGetValue(parentKey, out NodeObject? parent))
            {
                node.ParentKey = parentKey;
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }
        foreach (NodeObject node in objects.Values)
        {
            node.Children.Sort((left, right) => childOrder.GetValueOrDefault(left.TransformId, int.MaxValue).CompareTo(childOrder.GetValueOrDefault(right.TransformId, int.MaxValue)));
        }
        roots.Sort((left, right) => RootOrder(left).CompareTo(RootOrder(right)));
        return roots;
    }

    private static long RootOrder(NodeObject node)
    {
        return node.Transform.TryGetText(RootOrderKey) is { } text && YamlScalars.TryParseInteger(text, out long order) ? order : long.MaxValue;
    }

    private static void Flatten(NodeObject node, List<NodeObject> ordered)
    {
        ordered.Add(node);
        foreach (NodeObject child in node.Children)
        {
            Flatten(child, ordered);
        }
    }

    private static void Place(NodeObject node, Dictionary<long, long> hosts, Dictionary<long, string> componentTypes)
    {
        hosts[node.FileId] = node.FileId;
        hosts[node.TransformId] = node.FileId;
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (NodeComponent component in node.Components)
        {
            List<string> types = component.Row?.OutputTypes() ?? [];
            component.HostKey = types.Exists(used.Contains) ? component.FileId : node.FileId;
            if (component.HostKey == node.FileId)
            {
                used.UnionWith(types);
            }
            hosts[component.FileId] = component.HostKey;
            if (component.ComponentType is { } type)
            {
                componentTypes[component.FileId] = type;
            }
        }
    }
}
