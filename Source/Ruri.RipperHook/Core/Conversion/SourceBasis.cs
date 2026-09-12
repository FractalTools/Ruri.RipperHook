using System.Numerics;

namespace Ruri.RipperHook.Conversion;

/// <summary>
/// How a source engine's space maps onto Unity's: which source axis each Unity axis reads,
/// with what sign, and how many Unity meters a source unit is. Both engines this converter
/// serves are left-handed, so the map is a permutation with signs -- a proper rotation when
/// its determinant is +1 -- and rotations travel by permuting quaternion components the same
/// way. A reflecting map (determinant -1) is declared, not guessed: it flips triangle winding
/// and negates the quaternion's vector part, both of which this basis performs itself.
/// </summary>
public sealed class SourceBasis
{
    private readonly int[] axis;
    private readonly float[] sign;

    public float UnitScale { get; }

    public bool Reflects { get; }

    /// <param name="unityXFrom">Source axis index (0..2) Unity X reads, sign-bearing (+/-).</param>
    public SourceBasis(int unityXFrom, float unityXSign, int unityYFrom, float unityYSign, int unityZFrom, float unityZSign, float unitScale)
    {
        axis = [unityXFrom, unityYFrom, unityZFrom];
        sign = [unityXSign, unityYSign, unityZSign];
        UnitScale = unitScale;
        Reflects = Determinant() < 0f;
    }

    public Vector3 Direction(float x, float y, float z)
    {
        ReadOnlySpan<float> source = [x, y, z];
        return new Vector3(
            sign[0] * source[axis[0]],
            sign[1] * source[axis[1]],
            sign[2] * source[axis[2]]);
    }

    public Vector3 Position(float x, float y, float z)
    {
        return Direction(x, y, z) * UnitScale;
    }

    public Vector3 Scale(float x, float y, float z)
    {
        ReadOnlySpan<float> source = [x, y, z];
        return new Vector3(source[axis[0]], source[axis[1]], source[axis[2]]);
    }

    /// <summary>
    /// A rotation restated in Unity's basis. A proper axis permutation carries a quaternion by
    /// permuting its vector part identically; a reflection additionally conjugates it.
    /// </summary>
    public Quaternion Rotation(float x, float y, float z, float w)
    {
        Vector3 vector = Direction(x, y, z);
        return Reflects
            ? new Quaternion(-vector.X, -vector.Y, -vector.Z, w)
            : new Quaternion(vector.X, vector.Y, vector.Z, w);
    }

    private float Determinant()
    {
        float[,] matrix = new float[3, 3];
        for (int row = 0; row < 3; row++)
        {
            matrix[row, axis[row]] = sign[row];
        }
        return matrix[0, 0] * (matrix[1, 1] * matrix[2, 2] - matrix[1, 2] * matrix[2, 1])
            - matrix[0, 1] * (matrix[1, 0] * matrix[2, 2] - matrix[1, 2] * matrix[2, 0])
            + matrix[0, 2] * (matrix[1, 0] * matrix[2, 1] - matrix[1, 1] * matrix[2, 0]);
    }
}
