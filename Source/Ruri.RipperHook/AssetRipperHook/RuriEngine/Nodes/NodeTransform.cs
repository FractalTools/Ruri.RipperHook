using System.Numerics;

namespace Ruri.RipperHook.RuriEngine;

internal readonly record struct NodePose(Vector3 Position, Quaternion Rotation, float Scale, Matrix4x4? PostTransform);

internal static class NodeTransform
{
    private const double Tolerance = 1.0e-5;
    private const string PositionKey = "m_LocalPosition";
    private const string RotationKey = "m_LocalRotation";
    private const string ScaleKey = "m_LocalScale";

    public static NodePose Of(UnityYamlMapping transform, string path)
    {
        (double positionX, double positionY, double positionZ) = YamlNodes.Vector(YamlNodes.Field(transform, PositionKey, path), $"{path}.{PositionKey}");
        (double scaleX, double scaleY, double scaleZ) = YamlNodes.Vector(YamlNodes.Field(transform, ScaleKey, path), $"{path}.{ScaleKey}");
        UnityYamlMapping rotationNode = YamlNodes.Mapping(YamlNodes.Field(transform, RotationKey, path), $"{path}.{RotationKey}");
        Quaternion rotation = Quaternion.Normalize(new Quaternion(
            (float)YamlNodes.Number(YamlNodes.Field(rotationNode, "x", path), path),
            (float)YamlNodes.Number(YamlNodes.Field(rotationNode, "y", path), path),
            (float)YamlNodes.Number(YamlNodes.Field(rotationNode, "z", path), path),
            (float)YamlNodes.Number(YamlNodes.Field(rotationNode, "w", path), path)));
        Vector3 position = new((float)positionX, (float)positionY, (float)positionZ);
        if (scaleX > 0.0 && Math.Abs(scaleY - scaleX) <= Tolerance * scaleX && Math.Abs(scaleZ - scaleX) <= Tolerance * scaleX)
        {
            return new NodePose(position, rotation, (float)scaleX, null);
        }
        Matrix4x4 basis = Matrix4x4.CreateScale(new Vector3((float)scaleX, (float)scaleY, (float)scaleZ)) * Matrix4x4.CreateFromQuaternion(rotation);
        Vector3 row0 = new(basis.M11, basis.M12, basis.M13);
        Vector3 row1 = new(basis.M21, basis.M22, basis.M23);
        Vector3 row2 = new(basis.M31, basis.M32, basis.M33);
        float scale = row0.Length();
        bool orthogonal = scale > 0f
            && Math.Abs(row1.Length() - scale) <= Tolerance * scale
            && Math.Abs(row2.Length() - scale) <= Tolerance * scale
            && Math.Abs(Vector3.Dot(row0, row1)) <= Tolerance * scale * scale
            && Math.Abs(Vector3.Dot(row0, row2)) <= Tolerance * scale * scale
            && Math.Abs(Vector3.Dot(row1, row2)) <= Tolerance * scale * scale;
        if (orthogonal && Vector3.Dot(Vector3.Cross(row0, row1), row2) > 0f)
        {
            Matrix4x4 unit = new(row0.X / scale, row0.Y / scale, row0.Z / scale, 0f, row1.X / scale, row1.Y / scale, row1.Z / scale, 0f, row2.X / scale, row2.Y / scale, row2.Z / scale, 0f, 0f, 0f, 0f, 1f);
            return new NodePose(position, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(unit)), scale, null);
        }
        Matrix4x4 post = new(row0.X, row0.Y, row0.Z, 0f, row1.X, row1.Y, row1.Z, 0f, row2.X, row2.Y, row2.Z, 0f, 0f, 0f, 0f, 1f);
        return new NodePose(position, Quaternion.Identity, 1f, post);
    }
}
