using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using System.Numerics;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// The Transform tree of a loaded prefab or scene: every Transform is a node with a parent, a
/// local TRS, its GameObject's name and active flag, a Unity-space world matrix and a stable
/// root-relative path -- the path an AnimationClip binds curves by. The animator's own node
/// is bound by the EMPTY path, which is a real binding and not a missing one.
/// </summary>
public sealed class UnityNode
{
    public const string AnimatorRootPath = "";

    public required ITransform Transform { get; init; }

    public IGameObject? GameObject { get; init; }

    public required string Name { get; init; }

    public required bool Active { get; init; }

    public required Vector3 LocalPosition { get; init; }

    public required Quaternion LocalRotation { get; init; }

    public required Vector3 LocalScale { get; init; }

    public required double[] Local { get; init; }

    public double[] World { get; set; } = Mat4.Identity();

    public UnityNode? Parent { get; set; }

    public List<UnityNode> Children { get; } = [];

    public string Path { get; set; } = AnimatorRootPath;

    public int Depth { get; set; }

    public bool ActiveInHierarchy
    {
        get
        {
            for (UnityNode? node = this; node is not null; node = node.Parent)
            {
                if (!node.Active)
                {
                    return false;
                }
            }
            return true;
        }
    }
}

public sealed class UnityHierarchy
{
    private readonly Dictionary<ITransform, UnityNode> _byTransform = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IGameObject, UnityNode> _byGameObject = new(ReferenceEqualityComparer.Instance);

    public List<UnityNode> Roots { get; } = [];

    public IReadOnlyDictionary<ITransform, UnityNode> ByTransform => _byTransform;

    public UnityNode? Of(ITransform? transform) =>
        transform is not null && _byTransform.TryGetValue(transform, out UnityNode? node) ? node : null;

    public UnityNode? Of(IGameObject? gameObject) =>
        gameObject is not null && _byGameObject.TryGetValue(gameObject, out UnityNode? node) ? node : null;

    /// <summary>Every node, parents before children, in the order the previous host created
    /// bones and objects: roots in file order, then a stack walk that takes the LAST child
    /// first -- kept so that a name Blender has to uniquify lands the same suffix.</summary>
    public IEnumerable<UnityNode> StackOrder()
    {
        Stack<UnityNode> stack = new();
        for (int index = Roots.Count - 1; index >= 0; index--)
        {
            stack.Push(Roots[index]);
        }
        while (stack.Count > 0)
        {
            UnityNode node = stack.Pop();
            yield return node;
            foreach (UnityNode child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>Every node in document order: a depth-first walk that takes children in the
    /// order their parent lists them -- the order a serialized prefab states them in.</summary>
    public IEnumerable<UnityNode> DocumentOrder()
    {
        foreach (UnityNode root in Roots)
        {
            foreach (UnityNode node in Descend(root))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<UnityNode> Descend(UnityNode node)
    {
        yield return node;
        foreach (UnityNode child in node.Children)
        {
            foreach (UnityNode descendant in Descend(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Build the tree under the given root transforms (those with no father inside
    /// the loaded set), walking children through the transforms' own child lists.</summary>
    public static UnityHierarchy Build(IEnumerable<ITransform> roots)
    {
        UnityHierarchy hierarchy = new();
        foreach (ITransform rootTransform in roots)
        {
            UnityNode root = hierarchy.Create(rootTransform, null);
            root.World = root.Local;
            root.Path = UnityNode.AnimatorRootPath;
            hierarchy.Roots.Add(root);
            hierarchy.Fill(root);
        }
        return hierarchy;
    }

    private void Fill(UnityNode parent)
    {
        foreach (ITransform? childTransform in parent.Transform.Children_C4P)
        {
            if (childTransform is null || _byTransform.ContainsKey(childTransform))
            {
                continue;
            }
            UnityNode child = Create(childTransform, parent);
            child.World = Mat4.Multiply(parent.World, child.Local);
            child.Path = parent.Path == UnityNode.AnimatorRootPath ? child.Name : parent.Path + "/" + child.Name;
            child.Depth = parent.Depth + 1;
            parent.Children.Add(child);
            Fill(child);
        }
    }

    private UnityNode Create(ITransform transform, UnityNode? parent)
    {
        IGameObject? gameObject = transform.GameObject_C4P;
        Vector3 position = new(transform.LocalPosition_C4.X, transform.LocalPosition_C4.Y, transform.LocalPosition_C4.Z);
        Quaternion rotation = new(transform.LocalRotation_C4.X, transform.LocalRotation_C4.Y,
            transform.LocalRotation_C4.Z, transform.LocalRotation_C4.W);
        Vector3 scale = new(transform.LocalScale_C4.X, transform.LocalScale_C4.Y, transform.LocalScale_C4.Z);
        UnityNode node = new()
        {
            Transform = transform,
            GameObject = gameObject,
            Name = gameObject is null ? "Node" : gameObject.Name,
            Active = gameObject is null || gameObject.GetIsActive(),
            LocalPosition = position,
            LocalRotation = rotation,
            LocalScale = scale,
            Local = Mat4.UnityTrs(position, rotation, scale),
            Parent = parent,
        };
        _byTransform[transform] = node;
        if (gameObject is not null)
        {
            _byGameObject[gameObject] = node;
        }
        return node;
    }
}
