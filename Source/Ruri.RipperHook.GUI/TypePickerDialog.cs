using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.GUI;

/// <summary>
/// "Export by Type" picker — a checkbox list of the asset ClassIDs present in the loaded CABMap.
/// The user ticks the types to batch-export; <see cref="SelectedClassIds"/> holds the result.
/// </summary>
internal sealed class TypePickerDialog : Form
{
	private sealed record Row(int ClassId, string Label)
	{
		public override string ToString() => Label;
	}

	private readonly CheckedListBox _list;

	public HashSet<int> SelectedClassIds { get; } = new();

	public TypePickerDialog(IReadOnlySet<int> availableClassIds)
	{
		Text = RuriLocalization.ByTypeExportCaption;
		StartPosition = FormStartPosition.CenterParent;
		FormBorderStyle = FormBorderStyle.Sizable;
		MinimizeBox = false;
		MaximizeBox = false;
		ShowInTaskbar = false;
		MinimumSize = new Size(360, 420);
		Size = new Size(420, 520);

		_list = new CheckedListBox
		{
			Dock = DockStyle.Fill,
			CheckOnClick = true,
			IntegralHeight = false,
		};
		foreach (Row row in availableClassIds
			.Select(id => new Row(id, $"{ClassName(id)} ({id})"))
			.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase))
		{
			_list.Items.Add(row);
		}

		Label hint = new()
		{
			Dock = DockStyle.Top,
			AutoSize = false,
			Height = 28,
			Padding = new Padding(8, 6, 8, 0),
			Text = RuriLocalization.ByTypePickHint,
		};

		FlowLayoutPanel buttons = new()
		{
			Dock = DockStyle.Bottom,
			FlowDirection = FlowDirection.RightToLeft,
			Height = 44,
			Padding = new Padding(8),
		};
		Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
		Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
		ok.Click += (_, _) =>
		{
			SelectedClassIds.Clear();
			foreach (object item in _list.CheckedItems)
			{
				SelectedClassIds.Add(((Row)item).ClassId);
			}
		};
		buttons.Controls.Add(ok);
		buttons.Controls.Add(cancel);

		Controls.Add(_list);
		Controls.Add(hint);
		Controls.Add(buttons);
		AcceptButton = ok;
		CancelButton = cancel;
	}

	private static string ClassName(int id)
		=> Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : $"ClassID {id}";
}
