namespace Ruri.RipperHook.GUI.Services;

internal sealed class ClickAwayFilter(ListBox listBox, Func<Control?> getActiveTextBox, Action hide) : IMessageFilter
{
	private const int WmLeftButtonDown = 0x0201;

	public bool PreFilterMessage(ref Message m)
	{
		if (m.Msg != WmLeftButtonDown || !listBox.Visible)
		{
			return false;
		}

		Control? clicked = Control.FromHandle(m.HWnd);
		if (clicked == listBox || clicked == getActiveTextBox())
		{
			return false;
		}

		hide();
		return false;
	}
}
