using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Extensions;
using System.Drawing;
using System.Windows.Forms;

namespace Ruri.RipperHook.GUI.Components;

public sealed class GameObjectTreeNode : TreeNode
{
	public GameObjectTreeNode(IGameObject gameObject)
	{
		GameObject = gameObject;
		Text = string.IsNullOrWhiteSpace(gameObject.Name) ? "Unnamed" : gameObject.Name;
		try
		{
			if (gameObject.GetComponentAccessList().Any(static c => c is not null && c.ClassName.Contains("Renderer", StringComparison.OrdinalIgnoreCase)))
			{
				BackColor = Color.LightBlue;
			}
		}
		catch (Exception ex)
		{
			Logger.Verbose(LogCategory.General, $"GameObjectTreeNode renderer probe failed for '{gameObject.Name}': {ex.GetType().Name}: {ex.Message}");
		}
	}

	public IGameObject GameObject { get; }
}
