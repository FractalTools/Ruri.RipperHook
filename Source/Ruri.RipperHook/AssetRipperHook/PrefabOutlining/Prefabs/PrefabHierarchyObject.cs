using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;

namespace AssetRipper.Processing;

public sealed class PrefabHierarchyObject : GameObjectHierarchyObject, INamed
{
	public IGameObject Root { get; }

	public IPrefabInstance Prefab { get; }

	public override IEnumerable<IUnityObjectBase> Assets => base.Assets.Append(Prefab);

	public Utf8String Name { get => Root.Name; set => throw new NotSupportedException(); }

	public PrefabHierarchyObject(AssetInfo assetInfo, IGameObject root, IPrefabInstance prefab) : base(assetInfo)
	{
		Root = root;
		Prefab = prefab;
	}

	public override IEnumerable<(string, PPtr)> FetchDependencies()
	{
		return base.FetchDependencies().Append((nameof(Prefab), AssetToPPtr(Prefab)));
	}
}
