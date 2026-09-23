using System.Numerics;

namespace Ruri.RipperHook.Data;

/// <summary>
/// One mip level of a cubemap, sampled the way a GPU samples it: bilinear within a face and seamless
/// across faces.
///
/// <para>Faces are in the order +X, -X, +Y, -Y, +Z, -Z, each <c>size</c> texels square, RGBA, rows in
/// storage order. A direction picks its face and face coordinates by the cube-map table every
/// graphics API shares (the major axis is the face; <c>s</c>, <c>t</c> come from the other two over
/// its magnitude), <c>t = 0</c> being the first stored row.</para>
///
/// <para>A bilinear footprint that leaves its face takes the missing texels from the neighbouring
/// face: the texel centre, extended past the edge on the face's own plane, is a direction, and that
/// direction's texel is the neighbour. At a face corner only three texels exist, and the fourth is
/// their average -- the rule the Direct3D specification gives for seamless cube filtering.</para>
/// </summary>
public sealed class CubeLevel
{
    private readonly int _size;
    private readonly float[][] _faces;

    public CubeLevel(int size, float[][] faces)
    {
        if (faces.Length != 6 || faces.Any(face => face.Length != size * size * 4))
        {
            throw new ArgumentException($"a cubemap level is six {size}x{size} RGBA faces");
        }
        _size = size;
        _faces = faces;
    }

    public int Size => _size;

    /// <summary>Bilinear, seamless sample along <paramref name="direction"/> (need not be unit length).</summary>
    public Vector4 Sample(Vector3 direction)
    {
        (int face, float s, float t) = Project(direction);
        float x = s * _size - 0.5f;
        float y = t * _size - 0.5f;
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        Vector4? c00 = Texel(face, x0, y0);
        Vector4? c10 = Texel(face, x0 + 1, y0);
        Vector4? c01 = Texel(face, x0, y0 + 1);
        Vector4? c11 = Texel(face, x0 + 1, y0 + 1);
        Vector4 a00 = c00 ?? (c10!.Value + c01!.Value + c11!.Value) / 3f;
        Vector4 a10 = c10 ?? (c00!.Value + c01!.Value + c11!.Value) / 3f;
        Vector4 a01 = c01 ?? (c00!.Value + c10!.Value + c11!.Value) / 3f;
        Vector4 a11 = c11 ?? (c00!.Value + c10!.Value + c01!.Value) / 3f;
        Vector4 top = a00 + (a10 - a00) * fx;
        Vector4 bottom = a01 + (a11 - a01) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>The texel at integer face coordinates, reaching into the neighbouring face past an
    /// edge; null past a corner, where no face holds it.</summary>
    private Vector4? Texel(int face, int x, int y)
    {
        bool insideX = x >= 0 && x < _size;
        bool insideY = y >= 0 && y < _size;
        if (!insideX && !insideY)
        {
            return null;
        }
        if (!insideX || !insideY)
        {
            Vector3 beyond = Direction(face, (x + 0.5f) / _size, (y + 0.5f) / _size);
            (int neighbour, float s, float t) = Project(beyond);
            face = neighbour;
            x = Math.Clamp((int)MathF.Floor(s * _size), 0, _size - 1);
            y = Math.Clamp((int)MathF.Floor(t * _size), 0, _size - 1);
        }
        float[] texels = _faces[face];
        int at = (y * _size + x) * 4;
        return new Vector4(texels[at], texels[at + 1], texels[at + 2], texels[at + 3]);
    }

    /// <summary>The face a direction lands on and its (s, t) there, both in [0, 1].</summary>
    public static (int Face, float S, float T) Project(Vector3 direction)
    {
        float ax = MathF.Abs(direction.X);
        float ay = MathF.Abs(direction.Y);
        float az = MathF.Abs(direction.Z);
        int face;
        float sc;
        float tc;
        float major;
        if (ax >= ay && ax >= az)
        {
            major = ax;
            face = direction.X >= 0f ? 0 : 1;
            sc = direction.X >= 0f ? -direction.Z : direction.Z;
            tc = -direction.Y;
        }
        else if (ay >= az)
        {
            major = ay;
            face = direction.Y >= 0f ? 2 : 3;
            sc = direction.X;
            tc = direction.Y >= 0f ? direction.Z : -direction.Z;
        }
        else
        {
            major = az;
            face = direction.Z >= 0f ? 4 : 5;
            sc = direction.Z >= 0f ? direction.X : -direction.X;
            tc = -direction.Y;
        }
        return (face, (sc / major + 1f) * 0.5f, (tc / major + 1f) * 0.5f);
    }

    /// <summary>The direction through (s, t) of a face's plane, the inverse of <see cref="Project"/>;
    /// (s, t) may lie past the face.</summary>
    public static Vector3 Direction(int face, float s, float t)
    {
        float sc = s * 2f - 1f;
        float tc = t * 2f - 1f;
        return face switch
        {
            0 => new Vector3(1f, -tc, -sc),
            1 => new Vector3(-1f, -tc, sc),
            2 => new Vector3(sc, 1f, tc),
            3 => new Vector3(sc, -1f, -tc),
            4 => new Vector3(sc, -tc, 1f),
            5 => new Vector3(-sc, -tc, -1f),
            _ => throw new ArgumentOutOfRangeException(nameof(face), face, null),
        };
    }
}
