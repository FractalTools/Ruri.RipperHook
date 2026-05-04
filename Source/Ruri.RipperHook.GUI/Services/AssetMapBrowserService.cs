using MessagePack;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.GUI.Services;

internal sealed class AssetMapBrowserService
{
	private AssetMapFile _assetMap = new();

	public BrowserCabMapService CabMap { get; } = new();
	public IReadOnlyList<AssetMapEntry> Entries => _assetMap.AssetEntries;
	public Ruri.RipperHook.GameType GameType => _assetMap.GameType;
	public bool IsAssetMapLoaded => _assetMap.AssetEntries.Count > 0;

	public void Clear()
	{
		_assetMap = new AssetMapFile();
		CabMap.Clear();
	}

	public void LoadAssetMap(string path)
	{
		using FileStream stream = File.OpenRead(path);
		_assetMap = MessagePackSerializer.Deserialize<AssetMapFile>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
	}

	public IReadOnlyList<AssetMapEntry> Filter(IReadOnlyDictionary<string, string> rawFilters)
	{
		Dictionary<string, Regex> activeFilters = [];
		foreach ((string key, string value) in rawFilters)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				continue;
			}

			activeFilters[key] = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.Compiled);
		}

		if (activeFilters.Count == 0)
		{
			return _assetMap.AssetEntries.ToList();
		}

		return _assetMap.AssetEntries.Where(entry => entry.Matches(activeFilters)).ToList();
	}

	public void ConvertAssetMapToJson(string mapPath)
	{
		using FileStream stream = File.OpenRead(mapPath);
		AssetMapFile map = MessagePackSerializer.Deserialize<AssetMapFile>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
		string jsonPath = Path.ChangeExtension(mapPath, ".json");
		string json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(jsonPath, json);
	}

	public IReadOnlyList<string> ResolveSelectedSourceFiles(IEnumerable<AssetMapEntry> entries)
	{
		HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
		foreach (AssetMapEntry entry in entries)
		{
			string? sourceFile = CabMap.ResolveSourceFromAssetEntry(entry);
			if (!string.IsNullOrWhiteSpace(sourceFile) && File.Exists(sourceFile))
			{
				files.Add(sourceFile);
			}
		}
		return files.ToList();
	}

	public IReadOnlyList<string> ResolveSelectedCabNames(IEnumerable<AssetMapEntry> entries)
	{
		HashSet<string> cabs = new(StringComparer.OrdinalIgnoreCase);
		foreach (AssetMapEntry entry in entries)
		{
			if (!string.IsNullOrWhiteSpace(entry.CAB))
			{
				cabs.Add(entry.CAB);
				continue;
			}

			string? sourceFile = CabMap.ResolveSourceFromAssetEntry(entry);
			if (string.IsNullOrWhiteSpace(sourceFile))
			{
				continue;
			}

			foreach (string cab in CabMap.GetCabNamesForSource(sourceFile))
			{
				cabs.Add(cab);
			}
		}
		return cabs.ToList();
	}
}
