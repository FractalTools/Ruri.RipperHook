using System.Windows.Forms;

namespace Ruri.RipperHook.GUI.Components;

internal sealed class GOHierarchy : TreeView
{
	protected override void WndProc(ref Message m)
	{
		if (m.Msg != 0x203)
		{
			base.WndProc(ref m);
		}
	}
}
