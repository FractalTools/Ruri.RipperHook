using System.Numerics;

namespace Ruri.RipperHook.GlbExporter;

public readonly record struct UnityLocalTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);
