namespace Ruri.RipperHook.GUI.Services;

internal static class BrowserSearchHistory
{
	private static readonly Dictionary<string, List<string>> Entries = new(StringComparer.OrdinalIgnoreCase);

	public static IReadOnlyList<string> GetEntries(string? key)
	{
		if (string.IsNullOrWhiteSpace(key) || !Entries.TryGetValue(key, out List<string>? values))
		{
			return [];
		}

		return values;
	}

	public static void AddEntry(string key, string value)
	{
		if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
		{
			return;
		}

		if (!Entries.TryGetValue(key, out List<string>? values))
		{
			values = [];
			Entries[key] = values;
		}

		values.RemoveAll(static existing => string.IsNullOrWhiteSpace(existing));
		values.RemoveAll(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase));
		values.Insert(0, value);
		const int maxEntries = 24;
		if (values.Count > maxEntries)
		{
			values.RemoveRange(maxEntries, values.Count - maxEntries);
		}
	}

	public static void ClearKeys(IEnumerable<string> keys)
	{
		foreach (string key in keys)
		{
			Entries.Remove(key);
		}
	}
}
