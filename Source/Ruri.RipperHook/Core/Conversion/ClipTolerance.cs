namespace Ruri.RipperHook.Conversion;

/// <summary>
/// How far a written curve may stray from the sampled motion, in the clip's own units: metres
/// of position, radians of rotation, a fraction of scale, and an absolute step for a float
/// track. A source states these from the precision it was itself compressed at, so the keys
/// that survive reduction reproduce the motion the game shipped, not a smoother or coarser one.
/// </summary>
public readonly record struct ClipTolerance(float Position, float RotationRadians, float Scale, float Float);
