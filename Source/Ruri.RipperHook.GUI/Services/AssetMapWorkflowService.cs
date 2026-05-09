using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Extensions;
using MessagePack;

namespace Ruri.RipperHook.GUI.Services;

internal sealed class AssetMapWorkflowService
{
	private readonly Dictionary<string, CabMapEntry> _cabMap = new(StringComparer.OrdinalIgnoreCase);

	public bool HasCabMap => _cabMap.Count > 0;
	public int CabMapCount => _cabMap.Count;
	public string? LoadedCabMapPath { get; private set; }
	public string? BaseFolder { get; private set; }

	public void Clear()
	{
		_cabMap.Clear();
		LoadedCabMapPath = null;
		BaseFolder = null;
	}

	public CabMapLoadResult LoadCabMap(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		using FileStream stream = File.OpenRead(path);
		using BinaryReader reader = new(stream);

		string mapDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
		string storedBaseFolder = reader.ReadString();
		string resolvedBaseFolder = Path.GetFullPath(Path.Combine(mapDirectory, storedBaseFolder));

		Dictionary<string, CabMapEntry> entries = new(StringComparer.OrdinalIgnoreCase);
		int count = reader.ReadInt32();
		for (int i = 0; i < count; i++)
		{
			string cabName = reader.ReadString();
			string relativePath = reader.ReadString();
			long offset = reader.ReadInt64();
			int dependencyCount = reader.ReadInt32();
			List<string> dependencies = new(dependencyCount);
			for (int j = 0; j < dependencyCount; j++)
			{
				dependencies.Add(reader.ReadString());
			}

			entries[cabName] = new CabMapEntry(relativePath, offset, dependencies);
		}

		_cabMap.Clear();
		foreach ((string cabName, CabMapEntry entry) in entries)
		{
			_cabMap[cabName] = entry;
		}

		LoadedCabMapPath = Path.GetFullPath(path);
		BaseFolder = resolvedBaseFolder;
		return new CabMapLoadResult(LoadedCabMapPath, BaseFolder, _cabMap.Count);
	}

	public MapBuildResult BuildCabAndAssetMap(string rootFolder, string assetMapPath, GameType gameType)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetMapPath);

		string fullRootFolder = Path.GetFullPath(rootFolder);
		string fullAssetMapPath = Path.GetFullPath(assetMapPath);
		string cabMapPath = Path.ChangeExtension(fullAssetMapPath, ".bin");
		string[] files = Directory.GetFiles(fullRootFolder, "*.*", SearchOption.AllDirectories);
		if (files.Length == 0)
		{
			throw new InvalidOperationException("The selected folder does not contain any files.");
		}

		Dictionary<string, CabMapEntry> cabEntries = new(StringComparer.OrdinalIgnoreCase);
		List<AssetMapEntry> assetEntries = [];

		foreach (string file in files)
		{
			try
			{
				GameFileLoader.LoadAndProcess([file]);
				if (!GameFileLoader.IsLoaded)
				{
					continue;
				}

				string relativeFilePath = Path.GetRelativePath(fullRootFolder, file);
				foreach (var collection in GameFileLoader.GameBundle.FetchAssetCollections())
				{
					string cabName = string.IsNullOrWhiteSpace(collection.Name) ? Path.GetFileName(file) : collection.Name;
					List<string> dependencies = collection.Dependencies
						.Where(static dependency => dependency is not null && !string.IsNullOrWhiteSpace(dependency.Name))
						.Select(static dependency => dependency.Name)
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.ToList();

					cabEntries[cabName] = new CabMapEntry(relativeFilePath, 0, dependencies);

					foreach (var asset in collection)
					{
						assetEntries.Add(new AssetMapEntry
						{
							Name = asset.GetBestName(),
							Container = asset.OriginalPath ?? asset.AssetBundleName ?? string.Empty,
							Source = relativeFilePath,
							PathID = asset.PathID,
							Type = (int)(Enum.TryParse<ClassIDType>(asset.ClassName, ignoreCase: true, out ClassIDType classId) ? classId : 0),
							CAB = cabName,
						});
					}
				}
			}
			catch (Exception ex)
			{
				// 扫描混合目录时,某些文件读不动是预期的 (头文件已经被 Hash 过, 或者别的游戏的格式).
				// 但完全静默的话遇到调试问题没法定位 — 用 Verbose 级把文件名和异常类型记下来.
				Logger.Verbose(LogCategory.Import, $"Skipping unreadable file '{file}': {ex.GetType().Name}: {ex.Message}");
			}
			finally
			{
				GameFileLoader.Reset();
			}
		}

		Directory.CreateDirectory(Path.GetDirectoryName(fullAssetMapPath)!);
		WriteCabMap(cabMapPath, fullRootFolder, cabEntries);
		WriteAssetMap(fullAssetMapPath, gameType, assetEntries);

		_cabMap.Clear();
		foreach ((string cabName, CabMapEntry entry) in cabEntries)
		{
			_cabMap[cabName] = entry;
		}
		LoadedCabMapPath = cabMapPath;
		BaseFolder = fullRootFolder;

		return new MapBuildResult(fullRootFolder, fullAssetMapPath, cabMapPath, files.Length, cabEntries.Count, assetEntries.Count);
	}

	public CabResolutionResult ResolveCabFiles(IEnumerable<string> cabNames)
	{
		HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
		Queue<string> queue = new(cabNames.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));

		while (queue.Count > 0)
		{
			string cabName = queue.Dequeue();
			if (!requested.Add(cabName))
			{
				continue;
			}

			if (!_cabMap.TryGetValue(cabName, out CabMapEntry? entry))
			{
				missing.Add(cabName);
				continue;
			}

			if (!string.IsNullOrWhiteSpace(BaseFolder))
			{
				string fullPath = Path.GetFullPath(Path.Combine(BaseFolder, entry.RelativePath));
				if (File.Exists(fullPath))
				{
					files.Add(fullPath);
				}
			}

			foreach (string dependency in entry.Dependencies)
			{
				queue.Enqueue(dependency);
			}
		}

		return new CabResolutionResult(files.Order(StringComparer.OrdinalIgnoreCase).ToArray(), missing.Order(StringComparer.OrdinalIgnoreCase).ToArray());
	}

	private static void WriteCabMap(string cabMapPath, string baseFolder, IReadOnlyDictionary<string, CabMapEntry> cabEntries)
	{
		string outputDirectory = Path.GetDirectoryName(cabMapPath)!;
		Directory.CreateDirectory(outputDirectory);
		string relativeBaseFolder = Path.GetRelativePath(outputDirectory, baseFolder);

		using FileStream stream = File.Create(cabMapPath);
		using BinaryWriter writer = new(stream);
		writer.Write(relativeBaseFolder);
		writer.Write(cabEntries.Count);
		foreach ((string cabName, CabMapEntry entry) in cabEntries.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
		{
			writer.Write(cabName);
			writer.Write(entry.RelativePath);
			writer.Write(entry.Offset);
			writer.Write(entry.Dependencies.Count);
			foreach (string dependency in entry.Dependencies)
			{
				writer.Write(dependency);
			}
		}
	}

	private static void WriteAssetMap(string assetMapPath, GameType gameType, IReadOnlyList<AssetMapEntry> assetEntries)
	{
		AssetMapFile document = new()
		{
			GameType = gameType,
			AssetEntries = assetEntries.OrderBy(static entry => entry.Source, StringComparer.OrdinalIgnoreCase).ThenBy(static entry => entry.CAB, StringComparer.OrdinalIgnoreCase).ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList()
		};
		byte[] bytes = MessagePackSerializer.Serialize(document, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
		File.WriteAllBytes(assetMapPath, bytes);
	}

	private sealed record CabMapEntry(string RelativePath, long Offset, List<string> Dependencies);
}

internal sealed record MapBuildResult(string RootFolder, string AssetMapPath, string CabMapPath, int FilesScanned, int CabCount, int AssetCount);

internal sealed record CabMapLoadResult(string Path, string BaseFolder, int CabCount);

internal sealed record CabResolutionResult(string[] Files, string[] MissingCabs);
