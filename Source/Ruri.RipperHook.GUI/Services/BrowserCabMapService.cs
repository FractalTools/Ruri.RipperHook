using System.Text;

namespace Ruri.RipperHook.GUI.Services;

internal sealed class BrowserCabMapService
{
	internal sealed record Entry(string Path, long Offset, List<string> Dependencies);

	private readonly Dictionary<string, Entry> _cabMap = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, List<string>> _pathToCabs = new(StringComparer.OrdinalIgnoreCase);
	private string _baseFolder = string.Empty;

	public bool HasCabMap => _cabMap.Count > 0;

	public void Clear()
	{
		_cabMap.Clear();
		_pathToCabs.Clear();
		_baseFolder = string.Empty;
	}

	public void Load(string path)
	{
		Clear();
		string mapDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
		using FileStream stream = File.OpenRead(path);
		using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
		string storedBase = reader.ReadString();
		_baseFolder = Path.GetFullPath(Path.Combine(mapDir, storedBase));
		int count = reader.ReadInt32();
		for (int i = 0; i < count; i++)
		{
			string cab = reader.ReadString();
			string relativePath = reader.ReadString();
			long offset = reader.ReadInt64();
			int depCount = reader.ReadInt32();
			List<string> dependencies = [];
			for (int j = 0; j < depCount; j++)
			{
				dependencies.Add(reader.ReadString());
			}

			_cabMap[cab] = new Entry(relativePath, offset, dependencies);
		}

		BuildReverseIndex();
	}

	public string? ResolveSourceFromAssetEntry(AssetMapEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(entry.Source))
		{
			string direct = ResolveSource(entry.Source);
			if (File.Exists(direct))
			{
				return direct;
			}
		}

		if (!string.IsNullOrWhiteSpace(entry.CAB) && _cabMap.TryGetValue(entry.CAB, out Entry? cabEntry))
		{
			string fullPath = Path.Combine(_baseFolder, cabEntry.Path);
			if (File.Exists(fullPath))
			{
				return fullPath;
			}
		}

		return null;
	}

	public string ResolveSource(string? relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			return string.Empty;
		}

		return Path.IsPathRooted(relativePath)
			? relativePath
			: Path.GetFullPath(Path.Combine(_baseFolder, relativePath));
	}

	public IReadOnlyList<string> GetCabNamesForSource(string fullPath)
	{
		if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(_baseFolder))
		{
			return [];
		}

		string relativePath = Path.GetRelativePath(_baseFolder, fullPath);
		return _pathToCabs.TryGetValue(relativePath, out List<string>? cabs) ? cabs : [];
	}

	public ExportCabResult ExportCabs(string startCab, string displayName, string savePath, bool overwrite)
	{
		if (!HasCabMap)
		{
			throw new InvalidOperationException("CABMap not loaded.");
		}

		Dictionary<string, string?> parentMap = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<string>> childrenMap = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> missingInMap = new(StringComparer.OrdinalIgnoreCase);
		Queue<string> queue = new();

		parentMap[startCab] = null;
		childrenMap[startCab] = [];
		queue.Enqueue(startCab);

		while (queue.Count > 0)
		{
			string cab = queue.Dequeue();
			if (!_cabMap.TryGetValue(cab, out Entry? entry))
			{
				missingInMap.Add(cab);
				continue;
			}

			if (!childrenMap.ContainsKey(cab))
			{
				childrenMap[cab] = [];
			}

			foreach (string dependency in entry.Dependencies)
			{
				if (parentMap.ContainsKey(dependency))
				{
					continue;
				}

				parentMap[dependency] = cab;
				childrenMap[cab].Add(dependency);
				childrenMap[dependency] = [];
				queue.Enqueue(dependency);
			}
		}

		string outputDir = Path.Combine(savePath, SanitizeName(displayName));
		Directory.CreateDirectory(outputDir);

		int exportedCount = 0;
		int skippedCount = 0;
		HashSet<string> exportedCabNames = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> missingSource = new(StringComparer.OrdinalIgnoreCase);

		foreach (string cab in parentMap.Keys)
		{
			if (!_cabMap.TryGetValue(cab, out Entry? entry))
			{
				continue;
			}

			string sourcePath = Path.Combine(_baseFolder, entry.Path);
			if (!File.Exists(sourcePath))
			{
				missingSource.Add(cab);
				continue;
			}

			string fileName = Path.GetFileName(sourcePath);
			string destinationPath = Path.Combine(outputDir, fileName);
			if (File.Exists(destinationPath) && !overwrite)
			{
				skippedCount++;
				exportedCabNames.Add(cab);
				continue;
			}

			File.Copy(sourcePath, destinationPath, overwrite);
			exportedCount++;
			exportedCabNames.Add(cab);
		}

		WriteSummary(Path.Combine(outputDir, "_summary.txt"), startCab, displayName, parentMap, childrenMap, missingInMap, missingSource);

		return new ExportCabResult(outputDir, exportedCount, skippedCount, parentMap.Count, missingInMap.Count, missingSource.Count);
	}

	private void BuildReverseIndex()
	{
		_pathToCabs.Clear();
		foreach ((string cab, Entry entry) in _cabMap)
		{
			if (!_pathToCabs.TryGetValue(entry.Path, out List<string>? cabs))
			{
				cabs = [];
				_pathToCabs[entry.Path] = cabs;
			}
			cabs.Add(cab);
		}
	}

	private static string SanitizeName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "CABExport";
		}

		char[] invalidChars = Path.GetInvalidFileNameChars();
		StringBuilder builder = new(name.Length);
		foreach (char c in name)
		{
			builder.Append(invalidChars.Contains(c) ? '_' : c);
		}
		return builder.ToString().TrimEnd('.', ' ');
	}

	private static void WriteSummary(string path, string startCab, string displayName, IReadOnlyDictionary<string, string?> parentMap, IReadOnlyDictionary<string, List<string>> childrenMap, IReadOnlyCollection<string> missingInMap, IReadOnlyCollection<string> missingSource)
	{
		List<string> lines =
		[
			$"Root CAB: {startCab}",
			$"Display Name: {displayName}",
			$"Total CABs in tree: {parentMap.Count}",
			$"Missing in CABMap: {missingInMap.Count}",
			$"Missing source files: {missingSource.Count}",
			string.Empty,
			"Dependency Tree:"
		];

		WriteTree(lines, startCab, childrenMap, 0);

		if (missingInMap.Count > 0)
		{
			lines.Add(string.Empty);
			lines.Add("Missing In CABMap:");
			lines.AddRange(missingInMap.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase));
		}

		if (missingSource.Count > 0)
		{
			lines.Add(string.Empty);
			lines.Add("Missing Source Files:");
			lines.AddRange(missingSource.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase));
		}

		File.WriteAllLines(path, lines);
	}

	private static void WriteTree(List<string> lines, string cab, IReadOnlyDictionary<string, List<string>> childrenMap, int depth)
	{
		lines.Add($"{new string(' ', depth * 2)}- {cab}");
		if (!childrenMap.TryGetValue(cab, out List<string>? children))
		{
			return;
		}

		foreach (string child in children.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
		{
			WriteTree(lines, child, childrenMap, depth + 1);
		}
	}
}

internal sealed record ExportCabResult(string OutputDirectory, int ExportedCount, int SkippedCount, int TotalCabCount, int MissingInMapCount, int MissingSourceCount);
