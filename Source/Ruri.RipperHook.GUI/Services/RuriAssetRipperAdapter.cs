using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Modules.Audio;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.GUI.Web;
using AssetRipper.GUI.Web.Paths;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Processing.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_142;
using AssetRipper.SourceGenerated.Classes.ClassID_156;
using AssetRipper.SourceGenerated.Classes.ClassID_189;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Subclasses.GLTextureSettings;
using AssetRipper.Yaml;
using Ruri.RipperHook.AssetRipperHook.Exporting;
using Ruri.RipperHook.GUI.Components;
using System.Text;

namespace Ruri.RipperHook.GUI.Services;

internal sealed class RuriAssetRipperAdapter
{
	private List<RipperAssetEntry> _assets = [];

	public bool IsLoaded => GameFileLoader.IsLoaded;
	public IReadOnlyList<RipperAssetEntry> Assets => _assets;

	public void Reset()
	{
		_assets = [];
		GameFileLoader.Reset();
	}

	public void LoadPaths(IReadOnlyList<string> paths)
	{
		GameFileLoader.LoadAndProcess(paths);
		_assets = BuildAssetList(GameFileLoader.GameBundle);
	}

	public IReadOnlyList<RipperAssetEntry> Filter(string search, string typeFilter)
	{
		IEnumerable<RipperAssetEntry> query = _assets;

		if (!string.IsNullOrWhiteSpace(typeFilter) && !string.Equals(typeFilter, "All", StringComparison.OrdinalIgnoreCase))
		{
			query = query.Where(a => string.Equals(a.TypeString, typeFilter, StringComparison.OrdinalIgnoreCase));
		}

		if (!string.IsNullOrWhiteSpace(search))
		{
			query = query.Where(a =>
				a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| a.TypeString.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| a.Container.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| a.PathId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
		}

		return query.ToList();
	}

	public IReadOnlyList<string> GetTypes()
	{
		return _assets
			.Select(a => a.TypeString)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public PreviewData GetPreview(RipperAssetEntry entry)
	{
		IUnityObjectBase asset = entry.Asset;
		switch ((ClassIDType)asset.ClassID)
		{
			case ClassIDType.Texture2D:
			case ClassIDType.Texture2DArray:
			case ClassIDType.Texture3D:
			case ClassIDType.Sprite:
			case ClassIDType.TerrainData:
				if (TryGetImage(asset, out byte[]? pngBytes))
				{
					return PreviewData.Image(pngBytes!, GetPreviewInfoText(entry, asset));
				}
				return PreviewData.Info(GetPreviewInfoText(entry, asset) + Environment.NewLine + "Preview type: image" + Environment.NewLine + "Image preview unavailable for this asset instance.");

			case ClassIDType.Mesh:
			case ClassIDType.MeshFilter:
			case ClassIDType.MeshRenderer:
			case ClassIDType.SkinnedMeshRenderer:
			case ClassIDType.GameObject:
			case ClassIDType.Animator:
				if (TryGetMeshPreview(asset, out MeshPreviewPayload? meshPreview))
				{
					return PreviewData.Mesh(meshPreview!, GetMeshInfoText(entry, meshPreview!));
				}
				return PreviewData.Info(GetInfoText(entry) + Environment.NewLine + "Preview type: mesh" + Environment.NewLine + "Mesh preview unavailable for this asset instance.");
		}

		if (TryGetImage(asset, out byte[]? fallbackImageBytes))
		{
			return PreviewData.Image(fallbackImageBytes!, GetPreviewInfoText(entry, asset));
		}

		if (TryGetMeshPreview(asset, out MeshPreviewPayload? fallbackMeshPreview))
		{
			return PreviewData.Mesh(fallbackMeshPreview!, GetMeshInfoText(entry, fallbackMeshPreview!));
		}

		if (TryGetAudio(asset, out byte[]? audioBytes, out string? audioExtension))
		{
			return PreviewData.Audio(audioBytes!, audioExtension, GetPreviewInfoText(entry, asset));
		}

		string? text = TryGetText(asset);
		if (!string.IsNullOrEmpty(text))
		{
			return PreviewData.Text(text, GetPreviewInfoText(entry, asset));
		}

		string json = SerializeJson(asset);
		if (!string.IsNullOrWhiteSpace(json))
		{
			return PreviewData.Json(json, GetPreviewInfoText(entry, asset));
		}

		return PreviewData.Info(GetPreviewInfoText(entry, asset));
	}

	public string GetJson(RipperAssetEntry entry) => SerializeJson(entry.Asset);

	public string GetYaml(RipperAssetEntry entry) => NormalizeForTextBox(SerializeYaml(entry.Asset));

	public byte[] GetRawBytes(RipperAssetEntry entry)
	{
		if (entry.Asset is RawDataObject raw)
		{
			return raw.RawData;
		}

		using MemoryStream stream = new();
		using AssetWriter writer = new(stream, entry.Asset.Collection);
		entry.Asset.Write(writer, entry.Asset.Collection.Flags);
		return stream.ToArray();
	}

	public IReadOnlyList<TreeNode> BuildSceneTree(Dictionary<string, AssetItem> assetItemsByObjectKey)
	{
		List<TreeNode> roots = [];
		if (!IsLoaded)
		{
			return roots;
		}

		Dictionary<string, GameObjectTreeNode> nodes = [];
		foreach (IGameObject gameObject in GameFileLoader.GameBundle.FetchAssets().OfType<IGameObject>())
		{
			GameObjectTreeNode node = GetOrCreateNode(gameObject, nodes, assetItemsByObjectKey);
			ITransform? transform = gameObject.TryGetComponent<ITransform>();
			ITransform? parentTransform = transform?.Father_C4P;
			IGameObject? parentGameObject = parentTransform?.GameObject_C4P;
			if (parentGameObject is null)
			{
				if (!roots.Contains(node))
				{
					roots.Add(node);
				}
				continue;
			}

			GameObjectTreeNode parentNode = GetOrCreateNode(parentGameObject, nodes, assetItemsByObjectKey);
			if (ReferenceEquals(parentNode, node) || WouldCreateCycle(parentNode, node))
			{
				if (!roots.Contains(node))
				{
					roots.Add(node);
				}
				continue;
			}

			if (!parentNode.Nodes.Contains(node))
			{
				parentNode.Nodes.Add(node);
			}
		}

		roots.Sort(static (a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));
		return roots;
	}

	public int ExportAssets(IEnumerable<RipperAssetEntry> entries, string directory)
	{
		return RipperPrimaryAssetExportService.ExportAssets(entries.Select(static entry => entry.Asset), directory);
	}

	private static List<RipperAssetEntry> BuildAssetList(GameBundle bundle)
	{
		List<RipperAssetEntry> result = [];
		foreach (AssetCollection collection in bundle.FetchAssetCollections())
		{
			foreach (IUnityObjectBase asset in collection)
			{
				string name = asset.GetBestName();
				string container = asset.OriginalPath ?? asset.AssetBundleName ?? string.Empty;
				result.Add(new RipperAssetEntry(
					asset,
					asset.GetPath(),
					name,
					container,
					asset.ClassName,
					asset.PathID,
					EstimateSize(asset),
					collection.Name));
			}
		}

		return result;
	}

	private static long EstimateSize(IUnityObjectBase asset)
	{
		return asset switch
		{
			ITexture2D texture => texture.GetImageData().Length,
			ITextAsset textAsset => textAsset.Script_C49.Data.Length,
			IFont font => font.FontData.LongLength,
			_ => 0,
		};
	}

	private static string GetInfoText(RipperAssetEntry entry)
	{
		return string.Join(Environment.NewLine,
		[
			$"Name: {entry.Name}",
			$"Type: {entry.TypeString}",
			$"PathID: {entry.PathId}",
			$"Source: {entry.SourceFile}",
			$"Container: {entry.Container}",
			$"Size: {entry.Size}"
		]);
	}

	private static string GetMeshInfoText(RipperAssetEntry entry, MeshPreviewPayload payload)
	{
		return string.Join(Environment.NewLine,
		[
			GetInfoText(entry),
			$"Vertices: {payload.Vertices.Length}",
			$"Indices: {payload.Indices.Length}",
			$"SubMeshes: {payload.SubMeshes.Length}",
			$"Has Normals: {payload.Normals.Length == payload.Vertices.Length}",
			$"Has UV0: {payload.Uv0.Length == payload.Vertices.Length}"
		]);
	}

	private static string GetPreviewInfoText(RipperAssetEntry entry, IUnityObjectBase asset)
	{
		return asset switch
		{
			ITexture2D texture => string.Join(Environment.NewLine, [GetInfoText(entry), GetTextureDetailsText(texture)]),
			ISprite sprite => string.Join(Environment.NewLine, [GetInfoText(entry), GetSpriteDetailsText(sprite)]),
			_ => GetInfoText(entry),
		};
	}

	private static bool TryGetImage(IUnityObjectBase asset, out byte[]? pngBytes)
	{
		pngBytes = null;
		DirectBitmap bitmap = asset switch
		{
			IImageTexture texture when TextureConverter.TryConvertToBitmap(texture, out DirectBitmap textureBitmap) => textureBitmap,
			SpriteInformationObject info when TextureConverter.TryConvertToBitmap(info.Texture, out DirectBitmap infoBitmap) => infoBitmap,
			ISprite sprite when SpriteConverter.TryConvertToBitmap(sprite, out DirectBitmap spriteBitmap) => spriteBitmap,
			ITerrainData terrain => TerrainHeatmap.GetBitmap(terrain),
			_ => DirectBitmap.Empty,
		};

		if (bitmap.IsEmpty)
		{
			return false;
		}

		using MemoryStream stream = new();
		bitmap.Save(stream, ImageExportFormat.Png);
		pngBytes = stream.ToArray();
		return true;
	}

	private static bool TryGetMeshPreview(IUnityObjectBase asset, out MeshPreviewPayload? payload)
	{
		payload = null;
		IRenderer? renderer = asset switch
		{
			IRenderer directRenderer => directRenderer,
			IComponent component => component.GameObject_C2P?.TryGetComponent<IRenderer>(),
			IGameObject gameObject => gameObject.TryGetComponent<IRenderer>(),
			_ => null,
		};

		IMesh? mesh = asset switch
		{
			IMesh directMesh => directMesh,
			IMeshFilter meshFilter when meshFilter.MeshP is { } filterMesh => filterMesh,
			ISkinnedMeshRenderer skinnedMeshRenderer when skinnedMeshRenderer.MeshP is { } skinMesh => skinMesh,
			IRenderer meshRenderer when meshRenderer.GameObject_C25P?.TryGetComponent<IMeshFilter>()?.MeshP is { } rendererMesh => rendererMesh,
			IComponent component when component.GameObject_C2P?.TryGetComponent<IMeshFilter>()?.MeshP is { } componentMesh => componentMesh,
			IGameObject gameObject when gameObject.TryGetComponent<IMeshFilter>()?.MeshP is { } goMesh => goMesh,
			_ => null,
		};

		if (mesh is null || !mesh.IsSet() || !MeshData.TryMakeFromMesh(mesh, out MeshData meshData) || meshData.Vertices.Length == 0 || meshData.ProcessedIndexBuffer.Length == 0)
		{
			return false;
		}

		payload = MeshPreviewPayload.FromMeshData(meshData);
		payload.Textures = BuildMeshTextures(renderer, payload.SubMeshes);
		return true;
	}

	private static MeshTexturePreview[] BuildMeshTextures(IRenderer? renderer, IReadOnlyList<SubMeshPreview> subMeshes)
	{
		if (renderer is null || subMeshes.Count == 0)
		{
			return [];
		}

		int[] subsetIndices = GetSubsetIndices(renderer, subMeshes.Count);
		List<MeshTexturePreview> textures = [];
		for (int materialIndex = 0; materialIndex < renderer.Materials_C25P.Count; materialIndex++)
		{
			int subMeshIndex = materialIndex < subsetIndices.Length ? subsetIndices[materialIndex] : materialIndex;
			if ((uint)subMeshIndex >= (uint)subMeshes.Count)
			{
				continue;
			}

			IMaterial? material = renderer.Materials_C25P[materialIndex];
			if (material is null || !TryGetMainTexture(material, out ITexture2D? texture) || texture is null || !TryConvertTextureToPng(texture, out byte[]? pngData))
			{
				continue;
			}

			SubMeshPreview subMesh = subMeshes[subMeshIndex];
			textures.Add(new MeshTexturePreview(subMesh.StartIndex, subMesh.Count, pngData!));
		}

		return textures.ToArray();
	}

	private static int[] GetSubsetIndices(IRenderer renderer, int subMeshCount)
	{
		if (renderer.Has_SubsetIndices_C25())
		{
			return renderer.SubsetIndices_C25.Select(static i => (int)i).ToArray();
		}
		if (renderer.Has_StaticBatchInfo_C25())
		{
			return Enumerable.Range(renderer.StaticBatchInfo_C25.FirstSubMesh, renderer.StaticBatchInfo_C25.SubMeshCount).ToArray();
		}
		return Enumerable.Range(0, subMeshCount).ToArray();
	}

	private static bool TryGetMainTexture(IMaterial material, out ITexture2D? texture)
	{
		texture = null;
		ITexture2D? fallback = null;
		foreach (var pair in material.GetTextureProperties())
		{
			ITexture2D? candidate = pair.Value.Texture.TryGetAsset(material.Collection) as ITexture2D;
			if (candidate is null)
			{
				continue;
			}

			string name = pair.Key.String;
			if (name is "_MainTex" or "texture" or "Texture" or "_Texture")
			{
				texture = candidate;
				return true;
			}

			fallback ??= candidate;
		}

		texture = fallback;
		return texture is not null;
	}

	private static bool TryConvertTextureToPng(ITexture2D texture, out byte[]? pngData)
	{
		pngData = null;
		if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
		{
			return false;
		}

		using MemoryStream stream = new();
		bitmap.Save(stream, ImageExportFormat.Png);
		pngData = stream.ToArray();
		return true;
	}

	private static bool TryGetAudio(IUnityObjectBase asset, out byte[]? audioBytes, out string? extension)
	{
		audioBytes = null;
		extension = null;
		if (asset is not IAudioClip clip)
		{
			return false;
		}

		if (!AudioClipDecoder.TryDecode(clip, out byte[]? decodedAudioData, out string? decodedExtension, out _))
		{
			return false;
		}

		audioBytes = decodedAudioData;
		extension = decodedExtension;
		return true;
	}

	private static string? TryGetText(IUnityObjectBase asset)
	{
		return asset switch
		{
			ITextAsset textAsset when !textAsset.Script_C49.IsEmpty => textAsset.Script_C49.String,
			IShader shader when shader.Has_Script() && !shader.Script.IsEmpty => shader.Script,
			IMonoScript monoScript => $"{monoScript.Namespace}.{monoScript.ClassName_R}",
			_ => null,
		};
	}

	public void ExportJson(IEnumerable<RipperAssetEntry> entries, string directory)
	{
		Directory.CreateDirectory(directory);
		foreach (RipperAssetEntry entry in entries)
		{
			string fileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(entry.Name) ? entry.TypeString : entry.Name);
			File.WriteAllText(Path.Combine(directory, fileNameBase + ".json"), SerializeJson(entry.Asset));
		}
	}

	public void ExportYaml(IEnumerable<RipperAssetEntry> entries, string directory)
	{
		Directory.CreateDirectory(directory);
		foreach (RipperAssetEntry entry in entries)
		{
			string fileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(entry.Name) ? entry.TypeString : entry.Name);
			File.WriteAllText(Path.Combine(directory, fileNameBase + ".asset"), SerializeYaml(entry.Asset));
		}
	}

	public void ExportRaw(IEnumerable<RipperAssetEntry> entries, string directory)
	{
		Directory.CreateDirectory(directory);
		foreach (RipperAssetEntry entry in entries)
		{
			if (entry.Asset is not RawDataObject raw)
			{
				continue;
			}
			string fileNameBase = SanitizeFileName(string.IsNullOrWhiteSpace(entry.Name) ? entry.TypeString : entry.Name);
			File.WriteAllBytes(Path.Combine(directory, fileNameBase + ".dat"), raw.RawData);
		}
	}

	private static string SanitizeFileName(string value)
	{
		Span<char> invalidChars = stackalloc char[Path.GetInvalidFileNameChars().Length];
		Path.GetInvalidFileNameChars().CopyTo(invalidChars);
		foreach (char c in invalidChars)
		{
			value = value.Replace(c, '_');
		}
		return value;
	}

	private static string SerializeJson(IUnityObjectBase asset)
	{
		using StringWriter stringWriter = new();
		asset.WalkStandard(new DefaultJsonWalker(stringWriter));
		return stringWriter.ToString();
	}

	private static string SerializeYaml(IUnityObjectBase asset)
	{
		using StringWriter stringWriter = new() { NewLine = "\n" };
		YamlWriter writer = new();
		writer.WriteHead(stringWriter);
		var document = new YamlWalker().ExportYamlDocument(asset, ExportIdHandler.GetMainExportID(asset));
		writer.WriteDocument(document);
		writer.WriteTail(stringWriter);
		return stringWriter.ToString();
	}

	private static string NormalizeForTextBox(string text)
	{
		return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
	}

	public static string FormatHexView(byte[] data, int maxBytes = int.MaxValue)
	{
		int length = Math.Min(data.Length, maxBytes);
		StringBuilder builder = new();
		for (int i = 0; i < length; i += 16)
		{
			int lineLength = Math.Min(16, length - i);
			builder.Append(i.ToString("X8"));
			builder.Append(": ");
			for (int j = 0; j < lineLength; j++)
			{
				builder.Append(data[i + j].ToString("X2"));
				builder.Append(' ');
			}
			if (lineLength < 16)
			{
				builder.Append(' ', (16 - lineLength) * 3);
			}
			builder.Append(" | ");
			for (int j = 0; j < lineLength; j++)
			{
				byte value = data[i + j];
				builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
			}
			builder.AppendLine();
		}
		if (data.Length > length)
		{
			builder.AppendLine();
			builder.AppendLine($"... truncated, showing first {length:N0} of {data.Length:N0} bytes.");
		}
		return builder.ToString();
	}

	private static string GetTextureDetailsText(ITexture2D texture)
	{
		List<string> lines =
		[
			$"Width: {texture.Width_C28}",
			$"Height: {texture.Height_C28}",
			$"Format: {texture.Format_C28E}"
		];

		if (texture.TextureSettings_C28 is { } settings)
		{
			lines.Add($"Filter Mode: {GetFilterModeText(settings.FilterMode)}");
			lines.Add($"Anisotropic level: {settings.Aniso}");
			lines.Add($"Mip map bias: {settings.MipBias}");
			lines.Add($"Wrap mode: {GetWrapModeText(settings)}");
		}

		return string.Join(Environment.NewLine, lines);
	}

	private static string GetSpriteDetailsText(ISprite sprite)
	{
		if (SpriteConverter.TryConvertToBitmap(sprite, out DirectBitmap bitmap) && !bitmap.IsEmpty)
		{
			return string.Join(Environment.NewLine,
			[
				$"Width: {bitmap.Width}",
				$"Height: {bitmap.Height}"
			]);
		}

		return "Sprite preview unavailable.";
	}

	private static string GetFilterModeText(int value) => value switch
	{
		0 => "Point",
		1 => "Bilinear",
		2 => "Trilinear",
		_ => value.ToString()
	};

	private static string GetWrapModeText(IGLTextureSettings settings)
	{
		int wrapMode = settings.Has_WrapMode() ? settings.WrapMode : settings.Has_WrapU() ? settings.WrapU : -1;
		return wrapMode switch
		{
			0 => "Repeat",
			1 => "Clamp",
			2 => "Mirror",
			3 => "MirrorOnce",
			_ => wrapMode.ToString()
		};
	}

	private static string GetObjectKey(IUnityObjectBase asset)
	{
		return asset.Collection.Name + "|" + asset.PathID.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private static bool WouldCreateCycle(GameObjectTreeNode parentNode, GameObjectTreeNode childNode)
	{
		for (TreeNode? current = parentNode; current is not null; current = current.Parent)
		{
			if (ReferenceEquals(current, childNode))
			{
				return true;
			}
		}

		return false;
	}

	private static GameObjectTreeNode GetOrCreateNode(IGameObject gameObject, Dictionary<string, GameObjectTreeNode> nodes, Dictionary<string, AssetItem> assetItemsByObjectKey)
	{
		string objectKey = GetObjectKey(gameObject);
		if (nodes.TryGetValue(objectKey, out GameObjectTreeNode? existing))
		{
			return existing;
		}

		GameObjectTreeNode created = new(gameObject);
		nodes.Add(objectKey, created);
		if (assetItemsByObjectKey.TryGetValue(objectKey, out AssetItem? assetItem))
		{
			assetItem.TreeNode = created;
		}
		return created;
	}
}
