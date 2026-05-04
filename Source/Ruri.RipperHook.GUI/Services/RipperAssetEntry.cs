using AssetRipper.Assets;
using AssetRipper.GUI.Web.Paths;

namespace Ruri.RipperHook.GUI.Services;

public sealed record RipperAssetEntry(
	IUnityObjectBase Asset,
	AssetPath Path,
	string Name,
	string Container,
	string TypeString,
	long PathId,
	long Size,
	string SourceFile);
