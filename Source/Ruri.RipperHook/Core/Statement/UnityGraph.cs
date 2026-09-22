using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// A loaded closure as what it is: objects that point at objects. Reaching one kind of asset
/// from another THROUGH a stated set of kinds is engine topology -- true of every Unity build
/// -- so a game states which kinds it means and reads the answer here instead of walking the
/// references itself. Restricting what may be expanded is what keeps a walk scoped: a
/// controller reaches its states through its state machines without leaking across the
/// transitions that also point at states.
/// </summary>
public static class UnityGraph
{
    public static List<IUnityObjectBase> Named(GameData gameData, ClassIDType classId, string name)
    {
        List<IUnityObjectBase> found = [];
        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            if (asset.ClassID == (int)classId && string.Equals(asset.GetBestName(), name, StringComparison.Ordinal))
            {
                found.Add(asset);
            }
        }
        return found;
    }

    public static List<string> NamesOfClass(GameData gameData, ClassIDType classId)
    {
        List<string> names = [];
        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            if (asset.ClassID == (int)classId)
            {
                names.Add(asset.GetBestName());
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public static List<IUnityObjectBase> Reachable(IUnityObjectBase start, IReadOnlySet<int> expand,
        ClassIDType collect)
    {
        List<IUnityObjectBase> found = [];
        HashSet<IUnityObjectBase> seen = new(ReferenceEqualityComparer.Instance) { start };
        Stack<IUnityObjectBase> frontier = new();
        frontier.Push(start);
        while (frontier.Count > 0)
        {
            IUnityObjectBase current = frontier.Pop();
            foreach ((string _, PPtr pointer) in current.FetchDependencies())
            {
                if (!current.Collection.TryGetAsset(pointer, out IUnityObjectBase? target) || !seen.Add(target))
                {
                    continue;
                }
                if (target.ClassID == (int)collect)
                {
                    found.Add(target);
                }
                if (expand.Contains(target.ClassID))
                {
                    frontier.Push(target);
                }
            }
        }
        return found;
    }

    public static HashSet<int> Kinds(params ClassIDType[] classIds)
    {
        HashSet<int> set = [];
        foreach (ClassIDType classId in classIds)
        {
            set.Add((int)classId);
        }
        return set;
    }
}
