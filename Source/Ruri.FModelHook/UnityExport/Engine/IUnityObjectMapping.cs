using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.UnityExport.Engine;

// One registered conversion: a single concrete CUE4Parse UObject type -> a
// single AssetRipper Unity object. The mapper engine ("蛇身" in the
// 牛头蛇尾 pipeline) looks one up by the runtime type of each UE export and
// applies it. Everything is data-driven: supporting a new asset family is a
// new registration (MapperRegistry.Map<,>), never an edit to this seam
// (CLAUDE.md §0.C — build extension points, not special cases).
public interface IUnityObjectMapping
{
    // The concrete CUE4Parse export type this mapping consumes.
    Type SourceType { get; }

    // Construct the Unity object inside `collection`, run every field setter,
    // and return it. The created object is already a member of `collection`
    // because the CreateXxx factory routes through collection.CreateAsset.
    IUnityObjectBase Apply(UObject source, ProcessedAssetCollection collection);
}
