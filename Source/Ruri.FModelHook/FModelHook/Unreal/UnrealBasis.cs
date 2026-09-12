using Ruri.RipperHook.Conversion;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// Where Unreal puts its axes, and how far one of its units goes.
///
/// Unreal is Z up, X forward, Y right, in centimeters; the basis every decoded coordinate
/// crosses in is Y up, Z forward, X right, in meters. Both are left-handed, so the map is a
/// proper rotation and no winding changes: X reads Unreal Y, Y reads Unreal Z, Z reads Unreal X.
///
/// Stated once, here, because every reading that hands a coordinate over goes through it.
/// </summary>
public static class UnrealBasis
{
    public static readonly SourceBasis Basis = new(1, 1f, 2, 1f, 0, 1f, 0.01f);
}
