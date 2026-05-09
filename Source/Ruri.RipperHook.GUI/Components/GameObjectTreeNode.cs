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
			// PPtr 解析、缺组件等异常不应阻断树构建 — 但完全静默会让 "为什么这个节点没高亮" 类问题无法排查.
			Logger.Verbose(LogCategory.General, $"GameObjectTreeNode renderer probe failed for '{gameObject.Name}': {ex.GetType().Name}: {ex.Message}");
		}
	}

	public IGameObject GameObject { get; }
}
