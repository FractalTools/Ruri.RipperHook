using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Shaders;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;

namespace Ruri.RipperHook.AR;

public sealed class ShaderContentExtractor : IContentExtractor
{
	public static ShaderContentExtractor Instance { get; } = new();

	private static readonly SimpleShaderExporter SimpleExporter = new();
	private static readonly ShaderRuriDecompileExporter DecompiledExporter = new();

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out ExportCollectionBase? exportCollection)
	{
		if (asset is IShader shader)
		{
			exportCollection = new ShaderExportCollection(this, shader);
			return true;
		}

		exportCollection = null;
		return false;
	}

	public bool Export(IUnityObjectBase asset, string filePath, FileSystem fileSystem)
	{
		MinimalExportContainer container = new(asset.Collection);
		if (SimpleExporter.TryCreateCollection(asset, out _))
		{
			return SimpleExporter.Export(container, asset, filePath, fileSystem);
		}

		return DecompiledExporter.Export(container, asset, filePath, fileSystem);
	}

	private sealed class ShaderExportCollection(ShaderContentExtractor extractor, IShader shader) : SingleExportCollection<IShader>(extractor, shader)
	{
		protected override string ExportExtension => "shader";
	}

	private sealed class MinimalExportContainer(AssetRipper.Assets.Collections.AssetCollection file) : IExportContainer
	{
		public long GetExportID(IUnityObjectBase asset) => ExportIdHandler.GetMainExportID(asset);

		public AssetType ToExportType(Type type) => AssetType.Meta;

		public MetaPtr CreateExportPointer(IUnityObjectBase asset) => new(GetExportID(asset));

		public UnityGuid ScenePathToGUID(string name) => default;

		public bool IsSceneDuplicate(int sceneID) => false;

		public AssetRipper.Assets.Collections.AssetCollection File => file;

		public UnityVersion ExportVersion => file.Version;
	}
}
