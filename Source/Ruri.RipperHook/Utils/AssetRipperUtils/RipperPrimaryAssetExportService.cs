using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ruri.RipperHook.HookUtils;

public static class RipperPrimaryAssetExportService
{
	public delegate IEnumerable<(Type AssetType, IContentExtractor Extractor)> ContentExtractorDelegate(FullConfiguration settings);

	public static readonly List<ContentExtractorDelegate> CustomContentExtractors = new();

	private static readonly PropertyInfo GameDataProperty = typeof(GameFileLoader).GetProperty("GameData", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(GameFileLoader).FullName, "GameData");

	private static readonly MethodInfo CreateCollectionMethod = typeof(PrimaryContentExporter).GetMethod("CreateCollection", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(PrimaryContentExporter).FullName, "CreateCollection");

	public static int ExportAssets(IEnumerable<IUnityObjectBase> assets, string outputPath)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

		if (!GameFileLoader.IsLoaded)
		{
			throw new InvalidOperationException("No assets are currently loaded.");
		}

		List<IUnityObjectBase> requestedAssets = assets
			.Where(static asset => asset is not null)
			.Distinct(AssetReferenceComparer.Instance)
			.ToList();
		if (requestedAssets.Count == 0)
		{
			return 0;
		}

		Directory.CreateDirectory(outputPath);

		GameData gameData = GetLoadedGameData();
		FullConfiguration settings = GameFileLoader.Settings;
		settings.ExportRootPath = outputPath;

		PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(gameData, settings);
		foreach (ContentExtractorDelegate customContentExtractor in CustomContentExtractors)
		{
			foreach ((Type assetType, IContentExtractor extractor) in customContentExtractor(settings))
			{
				exporter.RegisterHandler(assetType, extractor);
			}
		}

		List<ExportCollectionBase> collections = CreateCollections(exporter, requestedAssets);
		int exportableCount = collections.Count(static collection => collection.Exportable);
		int exportedCount = 0;
		int currentExportable = 0;

		foreach (ExportCollectionBase collection in collections)
		{
			if (!collection.Exportable)
			{
				continue;
			}

			currentExportable++;
			Logger.Info(LogCategory.ExportProgress, $"({currentExportable}/{exportableCount}) Exporting '{collection.Name}'");
			bool exportedSuccessfully = collection.Export(outputPath, LocalFileSystem.Instance);
			if (exportedSuccessfully)
			{
				exportedCount++;
			}
			else
			{
				Logger.Warning(LogCategory.ExportProgress, $"Failed to export '{collection.Name}'");
			}
		}

		return exportedCount;
	}

	private static GameData GetLoadedGameData()
	{
		if (GameDataProperty.GetValue(null) is GameData gameData)
		{
			return gameData;
		}

		throw new InvalidOperationException("Game data is not available.");
	}

	private static List<ExportCollectionBase> CreateCollections(PrimaryContentExporter exporter, IEnumerable<IUnityObjectBase> selectedAssets)
	{
		List<ExportCollectionBase> collections = [];
		HashSet<IUnityObjectBase> queuedAssets = new(AssetReferenceComparer.Instance);

		foreach (IUnityObjectBase asset in selectedAssets)
		{
			if (!queuedAssets.Add(asset))
			{
				continue;
			}

			ExportCollectionBase collection = (ExportCollectionBase)CreateCollectionMethod.Invoke(exporter, [asset])!;
			if (collection is AssetRipper.Export.PrimaryContent.EmptyExportCollection)
			{
				continue;
			}

			foreach (IUnityObjectBase element in collection.Assets)
			{
				queuedAssets.Add(element);
			}
			collections.Add(collection);
		}

		return collections;
	}

	private sealed class AssetReferenceComparer : IEqualityComparer<IUnityObjectBase>
	{
		public static AssetReferenceComparer Instance { get; } = new();

		public bool Equals(IUnityObjectBase? x, IUnityObjectBase? y) => ReferenceEquals(x, y);

		public int GetHashCode(IUnityObjectBase obj) => RuntimeHelpers.GetHashCode(obj);
	}
}
