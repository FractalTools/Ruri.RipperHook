using System.Drawing;

namespace Ruri.RipperHook.GUI;

// Process-Monitor-style filter editor opened from the top menu. Build a rule from
// [Field] [Relation] [Value] then [Include/Exclude] and Add it; stack any number of rules freely
// (Type contains Animation → Include, Type contains Mesh → Exclude, Container is pelica, …). Edits the
// shared rule list in place and calls back to re-apply both asset lists live (non-modal).
internal sealed class FilterDialog : Form
{
	private readonly List<MainForm.FilterRule> _rules;
	private readonly Action _onChanged;

	private readonly ComboBox _field = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
	private readonly ComboBox _relation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
	private readonly ComboBox _value = new() { Width = 240 };
	private readonly ComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
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
		ClientSize = new Size(640, 320);
		MinimumSize = new Size(520, 240);

		Label heading = new() { Text = "Display rows matching these rules (no Include rule ⇒ show all; Exclude wins):", Dock = DockStyle.Top, Height = 22, Padding = new Padding(4, 4, 4, 0) };

		FlowLayoutPanel builder = new() { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(2) };
		_field.Items.AddRange(MainForm.FilterColumns);
		_field.SelectedIndex = 0;
		_field.SelectedIndexChanged += (_, _) => UpdateValueItems();
		foreach ((string label, MainForm.FilterRelation _) in MainForm.FilterRelationTable) _relation.Items.Add(label);
		_relation.SelectedIndex = 2; // contains
		_action.Items.AddRange(["Include", "Exclude"]);
		_action.SelectedIndex = 0;
		Label then = new() { Text = "then", AutoSize = true, Margin = new Padding(6, 8, 2, 1) };
		Button add = new() { Text = "Add", AutoSize = true, Margin = new Padding(4, 1, 1, 1) };
		add.Click += (_, _) => AddFromBuilder();
		builder.Controls.AddRange([_field, _relation, _value, then, _action, add]);

		_rulesView.Dock = DockStyle.Fill;
		_rulesView.View = View.Details;
		_rulesView.CheckBoxes = true;
		_rulesView.FullRowSelect = true;
		_rulesView.HideSelection = false;
		_rulesView.Columns.Add("Column", 90);
		_rulesView.Columns.Add("Relation", 100);
		_rulesView.Columns.Add("Value", 300);
		_rulesView.Columns.Add("Action", 80);
		_rulesView.ItemChecked += RulesView_ItemChecked;
		_rulesView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { e.Handled = true; RemoveSelected(); } };

		FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(2) };
		Button close = new() { Text = "Close", AutoSize = true, Margin = new Padding(2) };
		close.Click += (_, _) => Hide();
		Button clear = new() { Text = "Clear all", AutoSize = true, Margin = new Padding(2) };
		clear.Click += (_, _) => { _rules.Clear(); RefreshFromRules(); _onChanged(); };
		Button remove = new() { Text = "Remove", AutoSize = true, Margin = new Padding(2) };
		remove.Click += (_, _) => RemoveSelected();
		actions.Controls.AddRange([close, clear, remove]);

		Controls.Add(_rulesView);
		Controls.Add(actions);
		Controls.Add(builder);
		Controls.Add(heading);

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
		// Picking Field = Type offers the available type names so a rule needs no typing.
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
