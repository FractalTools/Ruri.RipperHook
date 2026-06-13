using AssetRipper.Assets;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

// One registered conversion: a single concrete CUE4Parse UObject type -> a
// single AssetRipper Unity object. Split into Create + Populate so the engine
// can cache a freshly-created object BEFORE running its setters — that is what
// lets cross-object references (and reference cycles) resolve through
// ConversionContext (the "蛇身" in the 牛头蛇尾 pipeline).
//
// Everything is data-driven: supporting a new asset family is a new registration
// (MapperRegistry.Map<,>), never an edit to this seam (CLAUDE.md §0.C).
public interface IUnityObjectMapping
{
    // The concrete CUE4Parse export type this mapping consumes.
    Type SourceType { get; }

    // Construct the empty Unity object inside the context's collection. No field
    // population here — the context caches the result first, then calls Populate.
    IUnityObjectBase Create(ConversionContext context);

    // Run every field setter, resolving cross-references through `context`.
    void Populate(UObject source, IUnityObjectBase destination, ConversionContext context);
}
