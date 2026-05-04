using System.Windows.Forms;
using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI.Components;

public sealed class AssetItem : ListViewItem
{
	public AssetItem(RipperAssetEntry asset)
	{
		Entry = asset;
		Text = asset.Name;
		SubItems.Add(asset.Container);
		SubItems.Add(asset.TypeString);
		SubItems.Add(asset.PathId.ToString());
		SubItems.Add(asset.Size.ToString());
	}

	public RipperAssetEntry Entry { get; }
	public GameObjectTreeNode? TreeNode { get; set; }
}
