using AssetRipper.Assets;
using Ruri.RipperHook.CabMapping;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_108;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>Optional, plan-owned extensions to the ONE Unity reader. No global policy or title lookup.
/// An ordinary plan supplies none and retains the original load, light, material and texture paths.</summary>
public sealed class UnityStatementAdapter
{
    public required string Signature { get; init; }
    /// <summary>Source-owned closure augmentation; loading and processing stay in the Unity reader.</summary>
    public Func<CabClosure, CabClosure>? PrepareClosure { get; init; }
    public Func<Statement, List<ILight>, UnityHierarchy, UnityRendererFilters, IEnumerable<UnityLightInfo>>? Lights { get; init; }
    public Action<UnityStatementData>? Complete { get; init; }
    public Action<StatementNode, UnityNode, Func<IUnityObjectBase, string>>? Node { get; init; }
    public Action<StatementNode, UnityRendererInfo>? Renderer { get; init; }
    public string? TextureRoleLayer { get; init; }
    public Func<ITexture2D, string, ImageExportFormat[], StatementTexture?>? EncodeTexture { get; init; }
}

/// <summary>Already-read data and existing key/texture registration, not another parser or loader.</summary>
public sealed record UnityStatementData(Statement Output, GameData Data,
    Func<IUnityObjectBase, string> Key, Func<ITexture2D, string> Texture,
    Func<AssetCollection, bool> Own,
    Func<IMaterial, string, IReadOnlyList<MaterialWrite>, string> WriteMaterial);
