using System.Globalization;
using System.Text.RegularExpressions;
using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI;

// Process-Monitor-equivalent filtering shared by both asset lists (loaded "Asset List" + "Virtual Asset List").
// Rules — [Field] [Relation] [Value] then Include/Exclude — live in one shared list edited from a small dialog
// opened via the top "Filter" menu; right-click a row for one-click Include/Exclude of its values. Every enabled
// rule is a required constraint: Include(X) means the row MUST match X, Exclude(X) means the row must NOT match
// X. A row shows only when it matches the tab's quick search AND satisfies every enabled rule. No rules ⇒
// everything shows. Folding Type into the rules (Type contains Animation → Include, Type contains Mesh →
// Exclude) replaces the old single/multi-select Type dropdown.
public partial class MainForm
{
	internal enum FilterRelation { Is, IsNot, Contains, Excludes, BeginsWith, EndsWith, LessThan, MoreThan, Matches, NotMatches }

	internal sealed record FilterRule(string Column, FilterRelation Relation, string Value, bool Include)
	{
		public bool Enabled { get; set; } = true;
	}

	internal static readonly string[] FilterColumns = ["Name", "Container", "Type", "PathID", "Source", "Deps"];

	internal static readonly (string Label, FilterRelation Relation)[] FilterRelationTable =
	[
		("is", FilterRelation.Is),
		("is not", FilterRelation.IsNot),
		("contains", FilterRelation.Contains),
		("excludes", FilterRelation.Excludes),
		("begins with", FilterRelation.BeginsWith),
		("ends with", FilterRelation.EndsWith),
		("less than", FilterRelation.LessThan),
		("more than", FilterRelation.MoreThan),
		("matches regex", FilterRelation.Matches),
		("not matches", FilterRelation.NotMatches),
	];

	private readonly List<FilterRule> _filterRules = [];
	private readonly List<string> _allTypes = [];
	private FilterDialog? _filterDialog;
	private System.Windows.Forms.Timer _assetSearchTimer = null!;

	private ToolStripMenuItem _assetQuickInclude = null!;
	private ToolStripMenuItem _assetQuickExclude = null!;

	// ── top "Filter" menu + asset-list quick search wiring ──────────────────────────────────────────
	private void BuildFilterMenu()
	{
		// The old single-select Type combo + inline filter panel are gone: quick search stays inline per tab and
		// the full rule editor lives in a dialog opened from this menu item.
		typeFilterComboBox.Visible = false;
		tabPage2.Controls.Remove(typeFilterComboBox);

		_assetSearchTimer = new System.Windows.Forms.Timer(components) { Interval = 250 };
		_assetSearchTimer.Tick += (_, _) => { _assetSearchTimer.Stop(); ApplyAssetFilter(); };
		listSearch.PlaceholderText = "Search loaded assets (Name / Container / Source / Type)…";

		ToolStripMenuItem filterMenu = new() { Text = "Filter" };
		filterMenu.Click += (_, _) => OpenFilterDialog();
		menuStrip1.Items.Add(filterMenu);

		// Right-click Include/Exclude for the loaded-asset list (the virtual list builds its own pair).
		_assetQuickInclude = new ToolStripMenuItem("Include");
		_assetQuickExclude = new ToolStripMenuItem("Exclude");
		assetListContextMenuStrip.Items.Insert(0, new ToolStripSeparator());
		assetListContextMenuStrip.Items.Insert(0, _assetQuickExclude);
		assetListContextMenuStrip.Items.Insert(0, _assetQuickInclude);
	}

	internal void QuickSearchChanged() // listSearch.TextChanged (asset list) — debounced
	{
		_assetSearchTimer.Stop();
		_assetSearchTimer.Start();
	}

	private void OpenFilterDialog()
	{
		if (_filterDialog is null || _filterDialog.IsDisposed)
		{
			_filterDialog = new FilterDialog(_filterRules, _allTypes, ReapplyAllFilters);
		}
		_filterDialog.RefreshTypes(_allTypes);
		_filterDialog.RefreshFromRules();
		_filterDialog.Show(this);
		_filterDialog.BringToFront();
	}

	/// <summary>Re-run the shared rules against both lists (called whenever a rule is added/removed/toggled).</summary>
	internal void ReapplyAllFilters()
	{
		ApplyAssetFilter();
		ApplyVirtualFilter();
	}

	private void RebuildFilters()
	{
		RebuildTypeList();
	}

	private void RebuildTypeList()
	{
		SortedSet<string> types = new(StringComparer.OrdinalIgnoreCase);
		foreach (string type in _adapter.GetTypes()) types.Add(type);
		foreach (string type in CabMapTypeNames()) types.Add(type);
		_allTypes.Clear();
		_allTypes.AddRange(types);
		_filterDialog?.RefreshTypes(_allTypes);
	}

	internal void AddRule(string column, FilterRelation relation, string value, bool include)
	{
		_filterRules.Add(new FilterRule(column, relation, value, include));
		_filterDialog?.RefreshFromRules();
		ReapplyAllFilters();
	}

	// ── evaluation (shared by both lists; each passes its own quick-search text + column getter) ──────
	private bool RowPasses(string quick, Func<string, string> value)
	{
		if (quick.Length > 0)
		{
			bool any = value("Name").Contains(quick, StringComparison.OrdinalIgnoreCase)
				|| value("Container").Contains(quick, StringComparison.OrdinalIgnoreCase)
				|| value("Source").Contains(quick, StringComparison.OrdinalIgnoreCase)
				|| value("Type").Contains(quick, StringComparison.OrdinalIgnoreCase);
			if (!any) return false;
		}

		foreach (FilterRule rule in _filterRules)
		{
			if (!rule.Enabled) continue;
			bool match = RelationMatches(value(rule.Column), rule.Relation, rule.Value);
			if (rule.Include)
			{
				if (!match) return false;
			}
			else if (match)
			{
				return false;
			}
		}
		return true;
	}

	private static bool RelationMatches(string value, FilterRelation relation, string filter)
	{
		const StringComparison ic = StringComparison.OrdinalIgnoreCase;
		return relation switch
		{
			FilterRelation.Is => value.Equals(filter, ic),
			FilterRelation.IsNot => !value.Equals(filter, ic),
			FilterRelation.Contains => value.Contains(filter, ic),
			FilterRelation.Excludes => !value.Contains(filter, ic),
			FilterRelation.BeginsWith => value.StartsWith(filter, ic),
			FilterRelation.EndsWith => value.EndsWith(filter, ic),
			FilterRelation.LessThan => CompareValues(value, filter) < 0,
			FilterRelation.MoreThan => CompareValues(value, filter) > 0,
			FilterRelation.Matches => TryRegex(value, filter, onError: true),
			FilterRelation.NotMatches => !TryRegex(value, filter, onError: false),
			_ => true,
		};
	}

	private static int CompareValues(string a, string b)
	{
		return long.TryParse(a, out long la) && long.TryParse(b, out long lb)
			? la.CompareTo(lb)
			: string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryRegex(string value, string pattern, bool onError)
	{
		try { return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase); }
		catch (ArgumentException) { return onError; }
	}

	private static string AssetColumnValue(RipperAssetEntry entry, string column) => column switch
	{
		"Name" => entry.Name,
		"Container" => entry.Container,
		"Type" => entry.TypeString,
		"PathID" => entry.PathId.ToString(CultureInfo.InvariantCulture),
		"Source" => entry.SourceFile,
		_ => string.Empty,
	};

	private static string CabColumnValue(ExportCabMap.CabRow row, string column) => column switch
	{
		"Name" => CabDisplayName(row),
		"Container" => string.Join(" | ", row.ContainerPaths),
		"Type" => CabTypeNames(row),
		"Source" => row.RelativePath,
		"Deps" => row.DependencyCount.ToString(CultureInfo.InvariantCulture),
		_ => string.Empty,
	};

	internal static string RelationLabel(FilterRelation relation)
	{
		foreach ((string label, FilterRelation value) in FilterRelationTable)
		{
			if (value == relation) return label;
		}
		return relation.ToString();
	}

	// ── right-click quick filters (Process Monitor style — Include/Exclude the clicked row's values) ──
	private void PopulateQuickFilterMenu(ToolStripMenuItem include, ToolStripMenuItem exclude, Func<string, string> value)
	{
		include.DropDownItems.Clear();
		exclude.DropDownItems.Clear();
		bool any = false;
		foreach (string column in FilterColumns)
		{
			string columnValue = value(column);
			if (string.IsNullOrEmpty(columnValue)) continue;
			any = true;
			FilterRelation relation = column is "Container" or "Type" ? FilterRelation.Contains : FilterRelation.Is;
			string label = $"{column} {RelationLabel(relation)} \"{Shorten(columnValue)}\"";
			include.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => AddRule(column, relation, columnValue, include: true)));
			exclude.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => AddRule(column, relation, columnValue, include: false)));
		}
		include.Enabled = any;
		exclude.Enabled = any;
	}

	private static string Shorten(string value) => value.Length <= 60 ? value : value[..57] + "…";
}
