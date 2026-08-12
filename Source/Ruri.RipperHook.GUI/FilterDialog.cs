using System.Drawing;

namespace Ruri.RipperHook.GUI;

internal sealed class FilterDialog : Form
{
	private readonly List<MainForm.FilterRule> _rules;
	private readonly Action _onChanged;

	private readonly ComboBox _field = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
	private readonly ComboBox _relation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
	private readonly ComboBox _value = new() { Dock = DockStyle.Fill };
	private readonly ComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
	private readonly ListView _rulesView = new();
	private string[] _types;
	private bool _suppressCheck;

	public FilterDialog(List<MainForm.FilterRule> rules, IEnumerable<string> types, Action onChanged)
	{
		_rules = rules;
		_onChanged = onChanged;
		_types = types.ToArray();

		Text = "Filter";
		FormBorderStyle = FormBorderStyle.SizableToolWindow;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(760, 380);
		MinimumSize = new Size(660, 300);

		Label intro = new() { Text = "Display rows matching ALL these conditions:", Dock = DockStyle.Top, Height = 20, Padding = new Padding(6, 8, 6, 0) };
		Label heading = new() { Text = "(no rules ⇒ show all; every enabled rule must hold)", Dock = DockStyle.Top, Height = 18, Padding = new Padding(6, 0, 6, 4), ForeColor = SystemColors.GrayText };

		TableLayoutPanel top = new()
		{
			Dock = DockStyle.Top,
			Height = 60,
			ColumnCount = 2,
			RowCount = 1,
			Padding = new Padding(6, 2, 6, 2),
		};
		top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));

		TableLayoutPanel builder = new()
		{
			Dock = DockStyle.Top,
			Height = 25,
			ColumnCount = 5,
			RowCount = 1,
		};
		builder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118f));		builder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128f));		builder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));		builder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42f));		builder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96f));		_field.Items.AddRange(MainForm.FilterColumns);
		_field.SelectedIndex = 0;
		_field.SelectedIndexChanged += (_, _) => UpdateValueItems();
		foreach ((string label, MainForm.FilterRelation _) in MainForm.FilterRelationTable) _relation.Items.Add(label);
		_relation.SelectedIndex = 2;		_action.Items.AddRange(["Include", "Exclude"]);
		_action.SelectedIndex = 0;
		Label then = new() { Text = "then", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
		builder.Controls.Add(_field, 0, 0);
		builder.Controls.Add(_relation, 1, 0);
		builder.Controls.Add(_value, 2, 0);
		builder.Controls.Add(then, 3, 0);
		builder.Controls.Add(_action, 4, 0);

		FlowLayoutPanel buttonStack = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 2, 0, 0) };
		Button add = new() { Text = "Add", Width = 78, Height = 24, Margin = new Padding(0, 0, 0, 4) };
		add.Click += (_, _) => AddFromBuilder();
		Button remove = new() { Text = "Remove", Width = 78, Height = 24, Margin = new Padding(0) };
		remove.Click += (_, _) => RemoveSelected();
		buttonStack.Controls.Add(add);
		buttonStack.Controls.Add(remove);

		top.Controls.Add(builder, 0, 0);
		top.Controls.Add(buttonStack, 1, 0);

		_rulesView.Dock = DockStyle.Fill;
		_rulesView.View = View.Details;
		_rulesView.CheckBoxes = true;
		_rulesView.FullRowSelect = true;
		_rulesView.HideSelection = false;
		_rulesView.Columns.Add("Column", 100);
		_rulesView.Columns.Add("Relation", 110);
		_rulesView.Columns.Add("Value", 320);
		_rulesView.Columns.Add("Action", 80);
		_rulesView.ItemChecked += RulesView_ItemChecked;
		_rulesView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { e.Handled = true; RemoveSelected(); } };

		FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
		Button close = new() { Text = "Close", AutoSize = true, Margin = new Padding(2) };
		close.Click += (_, _) => Hide();
		Button clear = new() { Text = "Clear all", AutoSize = true, Margin = new Padding(2) };
		clear.Click += (_, _) => { _rules.Clear(); RefreshFromRules(); _onChanged(); };
		actions.Controls.AddRange([close, clear]);

		Controls.Add(_rulesView);
		Controls.Add(actions);
		Controls.Add(top);
		Controls.Add(heading);
		Controls.Add(intro);

		UpdateValueItems();
		RefreshFromRules();
		FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
	}

	public void RefreshTypes(IEnumerable<string> types)
	{
		_types = types.ToArray();
		UpdateValueItems();
	}

	private void UpdateValueItems()
	{
		string column = _field.SelectedItem as string ?? "Name";
		_value.Items.Clear();
		if (column == "Type")
		{
			_value.Items.AddRange(_types);
		}
	}

	private void AddFromBuilder()
	{
		string column = _field.SelectedItem as string ?? "Name";
		MainForm.FilterRelation relation = MainForm.FilterRelationTable[_relation.SelectedIndex].Relation;
		string value = _value.Text;
		bool include = _action.SelectedIndex == 0;
		if (string.IsNullOrEmpty(value) && relation is not (MainForm.FilterRelation.Is or MainForm.FilterRelation.IsNot))
		{
			return;
		}
		_rules.Add(new MainForm.FilterRule(column, relation, value, include));
		RefreshFromRules();
		_onChanged();
	}

	private void RemoveSelected()
	{
		List<MainForm.FilterRule> toRemove = [];
		foreach (int index in _rulesView.SelectedIndices)
		{
			if ((uint)index < (uint)_rules.Count) toRemove.Add(_rules[index]);
		}
		if (toRemove.Count == 0) return;
		foreach (MainForm.FilterRule rule in toRemove) _rules.Remove(rule);
		RefreshFromRules();
		_onChanged();
	}

	public void RefreshFromRules()
	{
		_suppressCheck = true;
		_rulesView.BeginUpdate();
		_rulesView.Items.Clear();
		foreach (MainForm.FilterRule rule in _rules)
		{
			ListViewItem item = new(rule.Column) { Checked = rule.Enabled, Tag = rule, ForeColor = rule.Include ? Color.Green : Color.Firebrick };
			item.SubItems.Add(MainForm.RelationLabel(rule.Relation));
			item.SubItems.Add(rule.Value);
			item.SubItems.Add(rule.Include ? "Include" : "Exclude");
			_rulesView.Items.Add(item);
		}
		_rulesView.EndUpdate();
		_suppressCheck = false;
	}

	private void RulesView_ItemChecked(object? sender, ItemCheckedEventArgs e)
	{
		if (_suppressCheck) return;
		if (e.Item.Tag is MainForm.FilterRule rule)
		{
			rule.Enabled = e.Item.Checked;
			_onChanged();
		}
	}
}
