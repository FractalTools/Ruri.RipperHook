using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>Several plans as ONE statement: one closure per plan, every plan's rows appended
/// after the ones before it, node indices re-based as they land.</summary>
public static class StatementFlattener
{
    public static Statement Flatten(CabTable map, IReadOnlyList<StatementPlan> plans, StatementOptions options,
        CancellationToken cancellation)
    {
        if (plans.Count == 1)
        {
            return FlattenOne(map, plans[0], options);
        }
        Statement merged = new();
        foreach (StatementPlan plan in plans)
        {
            cancellation.ThrowIfCancellationRequested();
            Append(merged, FlattenOne(map, plan, options));
        }
        return merged;
    }

    /// <summary>Several statements as one, nodes re-based as they land -- for a source whose one
    /// seed is read as several parts.</summary>
    public static Statement Merge(IEnumerable<Statement> statements)
    {
        Statement merged = new();
        foreach (Statement statement in statements)
        {
            Append(merged, statement);
        }
        return merged;
    }

    private static Statement FlattenOne(CabTable map, StatementPlan plan, StatementOptions options)
    {
        Statement flattened = plan.Flatten is { } own ? own(options) : UnityStatement.Flatten(map, plan, options);
        NoteEmpty(flattened, plan);
        return flattened;
    }

    /// <summary>A plan that produced NOTHING says so, in the report, with what it was read out of.
    ///
    /// Handing a host zero rows and no error is the one answer it cannot act on: "this selection
    /// holds nothing" and "this selection was never joined to its archives" look identical at the
    /// far end, and the second is a bug that reads as content. The report is where the difference
    /// is stated, so every seed leaves a trace whether or not anything came out of it.</summary>
    private static void NoteEmpty(Statement flattened, StatementPlan plan)
    {
        if (flattened.Roots.Count != 0 || flattened.Nodes.Count != 0 || flattened.Meshes.Count != 0
            || flattened.Clips.Count != 0 || flattened.Morphs.Count != 0
            || flattened.Materials.Count != 0 || flattened.Textures.Count != 0)
        {
            return;
        }
        List<string> named = [];
        for (int index = 0; index < plan.Cabs.Count && index < 8; index++)
        {
            named.Add(plan.Cabs[index]);
        }
        flattened.Note(plan.Seed, "nothing to place: the archives this resolved to carry no "
            + "object the flattening keeps", plan.Cabs.Count,
            named.Count == 0 ? plan.Label : string.Join(", ", named));
    }

    private static void Append(Statement target, Statement source)
    {
        int nodeBase = target.Nodes.Count;
        foreach (StatementRoot root in source.Roots)
        {
            target.Roots.Add(root with { Node = root.Node < 0 ? -1 : root.Node + nodeBase });
        }
        foreach (StatementNode node in source.Nodes)
        {
            target.Nodes.Add(new StatementNode
            {
                Index = node.Index + nodeBase,
                Parent = node.Parent < 0 ? -1 : node.Parent + nodeBase,
                Name = node.Name,
                Path = node.Path,
                Kind = node.Kind,
                Active = node.Active,
                Mesh = node.Mesh,
                Skeleton = node.Skeleton,
                Materials = node.Materials,
                Anchor = node.Anchor,
                Position = node.Position,
                Rotation = node.Rotation,
                Scale = node.Scale,
                Light = node.Light,
                Camera = node.Camera,
            });
        }
        foreach (StatementMesh mesh in source.Meshes)
        {
            if (!target.HasMesh(mesh.Key))
            {
                target.Add(mesh);
            }
        }
        target.Skeletons.AddRange(source.Skeletons);
        foreach (StatementMaterial material in source.Materials)
        {
            if (!target.HasMaterial(material.Key))
            {
                target.Add(material);
            }
        }
        foreach (StatementTexture texture in source.Textures)
        {
            if (!target.HasTexture(texture.Key))
            {
                target.Add(texture);
            }
        }
        target.Clips.AddRange(source.Clips);
        target.Morphs.AddRange(source.Morphs);
        target.Report.AddRange(source.Report);
    }
}
