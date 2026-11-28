using System.Globalization;
using System.Numerics;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class NodeEntityWriter(UnityYamlAssetCollection collection, NodeFile nodes, RuriEngineMapping mapping, RecordTable records, GapReport gaps)
{
    private const string ComponentsKey = "components";
    private const string NameKey = "m_Name";
    private const string EnabledKey = "m_Enabled";
    private const string ActiveKey = "m_IsActive";
    private const string TagKey = "m_TagString";
    private const string UntaggedName = "Untagged";
    private const string NameComponent = "Ruri.ComponentSpace.NameComponent";
    private const string ChildOfComponent = "Ruri.ComponentSpace.ChildOfComponent";
    private const string DisabledComponent = "Ruri.ComponentSpace.DisabledComponent";
    private const string LocalTransformComponent = "Ruri.Transform.LocalTransformComponent";
    private const string PostTransformComponent = "Ruri.Transform.PostTransformMatrixComponent";

    private readonly Dictionary<long, IReadOnlyList<IReadOnlyList<ChildRecord>>> childRecords = new();

    public ObjectValue Write()
    {
        string file = collection.Entry.RelativePath;
        YamlValues values = new(collection, mapping, records, gaps, file);
        RecordConverter converter = new(records, gaps, file);
        ObjectValue entities = new();
        foreach (NodeObject node in nodes.Objects)
        {
            ObjectValue components = Structure(node);
            List<NodeComponent> separate = new();
            foreach (NodeComponent component in node.Components)
            {
                if (component.Row is null)
                {
                    continue;
                }
                if (component.HostKey != node.FileId)
                {
                    separate.Add(component);
                    continue;
                }
                Merge(components, Convert(node, component, values, converter, file), file);
            }
            entities.Add(Key(node.FileId), Entity(components));
            foreach (NodeComponent component in separate)
            {
                ObjectValue hosted = new();
                hosted.Add(ChildOfComponent, ParentOf(node.FileId));
                Merge(hosted, Convert(node, component, values, converter, file), file);
                entities.Add(Key(component.FileId), Entity(hosted));
            }
        }
        return entities;
    }

    private static string Key(long fileId) => fileId.ToString(CultureInfo.InvariantCulture);

    private static ObjectValue Entity(ObjectValue components)
    {
        ObjectValue entity = new();
        entity.Add(ComponentsKey, components);
        return entity;
    }

    private ObjectValue ParentOf(long parentKey)
    {
        ObjectValue parent = new();
        parent.Add("parent", nodes.Reference(collection.Entry.Guid, parentKey));
        return parent;
    }

    private ObjectValue Structure(NodeObject node)
    {
        ObjectValue components = new();
        string name = node.Body.TryGetText(NameKey) ?? string.Empty;
        if (name.Length > 0)
        {
            ObjectValue identity = new();
            identity.Add("hash", ScalarValue.FromUnsigned(NodeNameHash.Of(name)));
            if (node.ParentKey != 0)
            {
                identity.Add("scope", nodes.Reference(collection.Entry.Guid, node.ParentKey));
            }
            components.Add(NameComponent, identity);
        }
        if (node.ParentKey != 0)
        {
            components.Add(ChildOfComponent, ParentOf(node.ParentKey));
        }
        AddPose(components, NodeTransform.Of(node.Transform, collection.Entry.RelativePath));
        if (node.Body.TryGetText(ActiveKey) == "0")
        {
            components.Add(DisabledComponent, new ObjectValue());
        }
        AddTag(components, node.Body.TryGetText(TagKey) ?? UntaggedName);
        return components;
    }

    private static void AddPose(ObjectValue components, NodePose pose)
    {
        ObjectValue transform = new();
        transform.Add("position", Fields(("X", pose.Position.X), ("Y", pose.Position.Y), ("Z", pose.Position.Z)));
        transform.Add("scale", ScalarValue.FromSingle(pose.Scale));
        transform.Add("rotation", Fields(("X", pose.Rotation.X), ("Y", pose.Rotation.Y), ("Z", pose.Rotation.Z), ("W", pose.Rotation.W)));
        components.Add(LocalTransformComponent, transform);
        if (pose.PostTransform is { } matrix)
        {
            ObjectValue post = new();
            post.Add("value", Fields(
                ("M11", matrix.M11), ("M12", matrix.M12), ("M13", matrix.M13), ("M14", matrix.M14),
                ("M21", matrix.M21), ("M22", matrix.M22), ("M23", matrix.M23), ("M24", matrix.M24),
                ("M31", matrix.M31), ("M32", matrix.M32), ("M33", matrix.M33), ("M34", matrix.M34),
                ("M41", matrix.M41), ("M42", matrix.M42), ("M43", matrix.M43), ("M44", matrix.M44)));
            components.Add(PostTransformComponent, post);
        }
    }

    private static ObjectValue Fields(params (string Name, float Value)[] items)
    {
        ObjectValue result = new();
        foreach ((string name, float value) in items)
        {
            result.Add(name, ScalarValue.FromSingle(value));
        }
        return result;
    }

    private void AddTag(ObjectValue components, string tag)
    {
        if (tag == UntaggedName || Array.IndexOf(records.IgnoredTags, tag) >= 0)
        {
            return;
        }
        if (records.Tags.TryGetValue(tag, out string? marker))
        {
            components.Add(marker, new ObjectValue());
            return;
        }
        gaps.Add(GapCategories.UnmappedClass, $"tag {tag}", collection.Entry.RelativePath);
    }

    private IReadOnlyList<IReadOnlyList<ChildRecord>> ChildrenOf(NodeObject node, YamlValues values)
    {
        if (!childRecords.TryGetValue(node.FileId, out IReadOnlyList<IReadOnlyList<ChildRecord>>? found))
        {
            List<IReadOnlyList<ChildRecord>> children = new();
            foreach (NodeObject child in node.Children)
            {
                children.Add(child.Components
                    .Where(static component => component.Row is not null)
                    .Select(component => new ChildRecord(component.Row!, values.Read(component.Body), nodes.Reference(collection.Entry.Guid, component.FileId)))
                    .ToList());
            }
            childRecords[node.FileId] = found = children;
        }
        return found;
    }

    private ObjectValue Convert(NodeObject node, NodeComponent component, YamlValues values, RecordConverter converter, string file)
    {
        try
        {
            UnityYamlMapping body = component.Body;
            string hostName = node.Body.TryGetText(NameKey) ?? string.Empty;
            Func<IReadOnlyList<IReadOnlyList<ChildRecord>>> children = () => ChildrenOf(node, values);
            bool enabled = body.TryGetText(EnabledKey) is not { } text || !YamlScalars.TryParseInteger(text, out long flag) || flag != 0;
            RecordEntry entry = new(component.ClassName, enabled, body.TryGetText(NameKey) ?? string.Empty, hostName, values.Read(body), nodes.Reference(collection.Entry.Guid, component.FileId).Target, children);
            return converter.Convert(entry, component.Row!);
        }
        catch (Exception exception)
        {
            gaps.Add(GapCategories.UnreadableFile, $"{component.ClassName} could not be converted", $"{file}: {exception.GetType().Name}: {exception.Message}");
            return new ObjectValue();
        }
    }

    private void Merge(ObjectValue target, ObjectValue source, string file)
    {
        foreach (KeyValuePair<string, Value> entry in source.Fields)
        {
            if (!target.TryGet(entry.Key, out Value? existing))
            {
                target.Add(entry.Key, entry.Value);
            }
            else if (existing is ArrayValue list && entry.Value is ArrayValue more)
            {
                list.Items.AddRange(more.Items);
            }
            else
            {
                gaps.Add(GapCategories.DuplicateComponent, entry.Key, file);
            }
        }
    }
}
