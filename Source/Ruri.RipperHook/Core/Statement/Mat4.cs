using System.Numerics;
using System.Runtime.CompilerServices;

namespace Ruri.RipperHook.Statements;

/// <summary>Row-major 4x4 in double precision, column-vector convention (p' = M p), which is
/// how Unity states its transforms and its bind poses (element e_rc at row r, column c).</summary>
public static class Mat4
{
    public static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static double[] Multiply(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        double[] result = new double[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                double sum = 0;
                for (int inner = 0; inner < 4; inner++)
                {
                    sum += left[row * 4 + inner] * right[inner * 4 + column];
                }
                result[row * 4 + column] = sum;
            }
        }
        return result;
    }

    /// <summary>A Unity-space TRS from its components. The quaternion is normalised through a
    /// correctly rounded square root -- the same arithmetic the previous host performed, so a
    /// node's world matrix comes out bit-for-bit where it did.</summary>
    public static double[] UnityTrs(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        double x = rotation.X;
        double y = rotation.Y;
        double z = rotation.Z;
        double w = rotation.W;
        double norm = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (norm > 1e-12)
        {
            x /= norm;
            y /= norm;
            z /= norm;
            w /= norm;
        }
        else
        {
            x = 0;
            y = 0;
            z = 0;
            w = 1;
        }
        double sx = scale.X;
        double sy = scale.Y;
        double sz = scale.Z;
        return
        [
            (1.0 - 2.0 * (y * y + z * z)) * sx, 2.0 * (x * y - z * w) * sy, 2.0 * (x * z + y * w) * sz, position.X,
            2.0 * (x * y + z * w) * sx, (1.0 - 2.0 * (x * x + z * z)) * sy, 2.0 * (y * z - x * w) * sz, position.Y,
            2.0 * (x * z - y * w) * sx, 2.0 * (y * z + x * w) * sy, (1.0 - 2.0 * (x * x + y * y)) * sz, position.Z,
            0, 0, 0, 1,
        ];
    }

    public static double[] FromFloats(ReadOnlySpan<float> rowMajor)
    {
        double[] result = new double[16];
        for (int index = 0; index < 16; index++)
        {
            result[index] = rowMajor[index];
        }
        return result;
    }

    public static double Determinant3(ReadOnlySpan<double> m) =>
        m[0] * (m[5] * m[10] - m[6] * m[9])
        - m[1] * (m[4] * m[10] - m[6] * m[8])
        + m[2] * (m[4] * m[9] - m[5] * m[8]);

    /// <summary>The inverse transpose of the upper 3x3, for carrying normals through a skin
    /// transform. Null when the linear part is singular.</summary>
    public static double[]? NormalMatrix(ReadOnlySpan<double> m)
    {
        double determinant = Determinant3(m);
        if (Math.Abs(determinant) <= 1e-12)
        {
            return null;
        }
        double a = m[0], b = m[1], c = m[2];
        double d = m[4], e = m[5], f = m[6];
        double g = m[8], h = m[9], i = m[10];
        double inverse = 1.0 / determinant;
        double[] adjugate =
        [
            (e * i - f * h) * inverse, (c * h - b * i) * inverse, (b * f - c * e) * inverse,
            (f * g - d * i) * inverse, (a * i - c * g) * inverse, (c * d - a * f) * inverse,
            (d * h - e * g) * inverse, (b * g - a * h) * inverse, (a * e - b * d) * inverse,
        ];
        return
        [
            adjugate[0], adjugate[3], adjugate[6],
            adjugate[1], adjugate[4], adjugate[7],
            adjugate[2], adjugate[5], adjugate[8],
        ];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TransformPoint(ReadOnlySpan<double> m, double x, double y, double z,
        out double ox, out double oy, out double oz)
    {
        ox = m[0] * x + m[1] * y + m[2] * z + m[3];
        oy = m[4] * x + m[5] * y + m[6] * z + m[7];
        oz = m[8] * x + m[9] * y + m[10] * z + m[11];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TransformDirection3(ReadOnlySpan<double> linear3, double x, double y, double z,
        out double ox, out double oy, out double oz)
    {
        ox = linear3[0] * x + linear3[1] * y + linear3[2] * z;
        oy = linear3[3] * x + linear3[4] * y + linear3[5] * z;
        oz = linear3[6] * x + linear3[7] * y + linear3[8] * z;
    }

    public static double[] Linear3(ReadOnlySpan<double> m) =>
        [m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10]];

    public static double[]? Invert(ReadOnlySpan<double> m)
    {
        double[] inverse = new double[16];
        inverse[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inverse[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inverse[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inverse[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inverse[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inverse[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inverse[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inverse[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inverse[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inverse[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inverse[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inverse[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inverse[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inverse[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inverse[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inverse[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        double determinant = m[0] * inverse[0] + m[1] * inverse[4] + m[2] * inverse[8] + m[3] * inverse[12];
        if (Math.Abs(determinant) < 1e-18)
        {
            return null;
        }
        double scale = 1.0 / determinant;
        for (int index = 0; index < 16; index++)
        {
            inverse[index] *= scale;
        }
        return inverse;
    }

    /// <summary>A TRS matrix back into its parts: translation off the last column, scale off
    /// the column lengths (the first negated when the linear part reflects), rotation off the
    /// normalised columns.</summary>
    public static (Vector3 Position, Quaternion Rotation, Vector3 Scale) Decompose(ReadOnlySpan<double> m)
    {
        Vector3 position = new((float)m[3], (float)m[7], (float)m[11]);
        double sx = Math.Sqrt(m[0] * m[0] + m[4] * m[4] + m[8] * m[8]);
        double sy = Math.Sqrt(m[1] * m[1] + m[5] * m[5] + m[9] * m[9]);
        double sz = Math.Sqrt(m[2] * m[2] + m[6] * m[6] + m[10] * m[10]);
        if (Determinant3(m) < 0)
        {
            sx = -sx;
        }
        double ix = sx == 0 ? 0 : 1.0 / sx;
        double iy = sy == 0 ? 0 : 1.0 / sy;
        double iz = sz == 0 ? 0 : 1.0 / sz;
        double r00 = m[0] * ix, r01 = m[1] * iy, r02 = m[2] * iz;
        double r10 = m[4] * ix, r11 = m[5] * iy, r12 = m[6] * iz;
        double r20 = m[8] * ix, r21 = m[9] * iy, r22 = m[10] * iz;
        double trace = r00 + r11 + r22;
        double qx, qy, qz, qw;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            qw = 0.25 * s;
            qx = (r21 - r12) / s;
            qy = (r02 - r20) / s;
            qz = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2;
            qw = (r21 - r12) / s;
            qx = 0.25 * s;
            qy = (r01 + r10) / s;
            qz = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2;
            qw = (r02 - r20) / s;
            qx = (r01 + r10) / s;
            qy = 0.25 * s;
            qz = (r12 + r21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2;
            qw = (r10 - r01) / s;
            qx = (r02 + r20) / s;
            qy = (r12 + r21) / s;
            qz = 0.25 * s;
        }
        Quaternion rotation = Quaternion.Normalize(new Quaternion((float)qx, (float)qy, (float)qz, (float)qw));
        return (position, rotation, new Vector3((float)sx, (float)sy, (float)sz));
    }
}
