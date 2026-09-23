using System.Numerics;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// Unity's left-handed Y-up space restated in the basis a host reads. Every conversion here
/// is a reflection (determinant -1) carried consistently: a transform converts by conjugation
/// C M C (a single reflection is its own inverse), triangle winding reverses, and a tangent's
/// handedness sign flips with the cross product -- and flips back when the target also flips
/// the V axis, because that reverses the bitangent itself.
///
/// <c>blender</c> swaps Y and Z (right-handed Z-up; UV origin bottom-left in both, so V stays
/// and tangent w flips) and turns a top-level object by 180 degrees about the up axis so the
/// asset faces the viewer. <c>gltf</c> negates X (right-handed Y-up, asset facing +Z; V flips
/// because glTF's UV origin is the upper left, so tangent w survives). <c>unity</c> is the
/// identity, for a reader that wants the engine's own numbers.
/// </summary>
public sealed class Basis
{
    private readonly double[] _matrix;
    private readonly int[] _permutation;
    private readonly float[] _signs;
    private readonly bool _allPositive;

    public string Name { get; }

    public bool FlipV { get; }

    public bool ReversesWinding { get; }

    /// <summary>Where this basis' own front and up point, in its own axes. A TARGET states
    /// them; nothing about a source belongs here.</summary>
    public Vector3 Front { get; }

    public Vector3 Up { get; }

    public double[] RootRotation { get; }

    public Quaternion RootQuaternion { get; }

    /// <summary>Where a camera or a light of THIS basis points in its own local axes. A target
    /// states it; the engine's own cameras are TURNED onto it rather than restated, which is why
    /// a basis never carries a quarter turn as a number.</summary>
    public Vector3 AimForward { get; }

    public Vector3 AimUp { get; }

    private readonly double[] _aimTurn;

    private Basis(string name, double[,] matrix3, bool flipV, Vector3 front, Vector3 up,
        Vector3 aimForward, Vector3 aimUp)
    {
        Name = name;
        FlipV = flipV;
        Front = front;
        Up = up;
        AimForward = aimForward;
        AimUp = aimUp;
        _matrix = Mat4.Identity();
        _permutation = new int[3];
        _signs = new float[3];
        bool allPositive = true;
        for (int row = 0; row < 3; row++)
        {
            int column = -1;
            for (int candidate = 0; candidate < 3; candidate++)
            {
                _matrix[row * 4 + candidate] = matrix3[row, candidate];
                if (matrix3[row, candidate] != 0.0)
                {
                    column = candidate;
                }
            }
            _permutation[row] = column;
            _signs[row] = (float)matrix3[row, column];
            allPositive &= _signs[row] > 0f;
        }
        _allPositive = allPositive;
        ReversesWinding = Mat4.Determinant3(_matrix) < 0.0;
        (RootRotation, RootQuaternion) = RootRotationFor(UnityForward);
        _aimTurn = TurnBetween(aimForward, aimUp,
            ConvertPoint(EngineAimForward), ConvertPoint(EngineAimUp));
    }

    /// <summary>Which way a source asset's own space faces when nothing says otherwise.
    /// Unity states it: a transform's forward is +Z, and an asset authored to be used
    /// without a turn faces it.</summary>
    public static readonly Vector3 UnityForward = new(0f, 0f, 1f);

    /// <summary>Where a camera or a light of the ENGINE every statement crosses in points, in its
    /// own local axes: a transform's forward, with the transform's own up. Stated once, because
    /// every basis' aim turn is measured against this one pair.</summary>
    public static readonly Vector3 EngineAimForward = new(0f, 0f, 1f);

    public static readonly Vector3 EngineAimUp = new(0f, 1f, 0f);

    public static readonly Basis Unity = new("unity",
        new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, flipV: false,
        front: new Vector3(0f, 0f, 1f), up: new Vector3(0f, 1f, 0f),
        aimForward: new Vector3(0f, 0f, 1f), aimUp: new Vector3(0f, 1f, 0f));

    public static readonly Basis Blender = new("blender",
        new double[,] { { 1, 0, 0 }, { 0, 0, 1 }, { 0, 1, 0 } }, flipV: false,
        front: new Vector3(0f, -1f, 0f), up: new Vector3(0f, 0f, 1f),
        aimForward: new Vector3(0f, 0f, -1f), aimUp: new Vector3(0f, 1f, 0f));

    public static readonly Basis Gltf = new("gltf",
        new double[,] { { -1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, flipV: true,
        front: new Vector3(0f, 0f, 1f), up: new Vector3(0f, 1f, 0f),
        aimForward: new Vector3(0f, 0f, -1f), aimUp: new Vector3(0f, 1f, 0f));

    /// <summary>Every basis, in declaration order -- what a host asks for when it needs
    /// the conversion itself rather than converted numbers.</summary>
    public static readonly Basis[] All = [Unity, Blender, Gltf];

    /// <summary>The conversion, row-major 4x4. It is its own inverse (a permutation with
    /// signs), so one matrix converts in either direction. Published rather than restated:
    /// a host whose animation maths runs in the ENGINE's own basis still has to convert its
    /// answer at the end, and a second copy of these numbers is a second definition of what
    /// the basis IS.</summary>
    public double[] Matrix => (double[])_matrix.Clone();

    /// <summary>The once-only LOCAL turn that lands a converted camera or light on this basis'
    /// own aim convention, row-major 4x4: an aimable node sits at <c>C M C * AimTurn</c> and
    /// nothing else does, so a child of one carries the inverse to stay where it was.
    ///
    /// Derived from the two aim frames rather than tabulated: a basis states WHERE its cameras
    /// point and the quarter turn follows, so nobody remembers a rotation about an axis.</summary>
    public double[] AimTurn => (double[])_aimTurn.Clone();

    public static Basis Parse(string name) => name.ToLowerInvariant() switch
    {
        "unity" => Unity,
        "blender" => Blender,
        "gltf" => Gltf,
        _ => throw new ArgumentException($"basis '{name}' is not one of unity, blender, gltf."),
    };

    /// <summary>What happens to glTF-style tangent handedness w: the reflection flips every
    /// cross product and a V flip reverses the bitangent again. Derived, not tabulated.</summary>
    public float TangentWSign
    {
        get
        {
            float sign = ReversesWinding ? -1f : 1f;
            return FlipV ? -sign : sign;
        }
    }

    public double[] ConvertMatrix(ReadOnlySpan<double> unityMatrix) =>
        Mat4.Multiply(Mat4.Multiply(_matrix, unityMatrix), _matrix);

    /// <summary>The once-only top-level turn for a source whose OWN asset space faces
    /// <paramref name="sourceForward"/>, stated in the engine's axes every source crosses in.
    ///
    /// An engine states where its axes point; it does not state which way a studio modelled
    /// its cast. Unity's transform forward is the default and reproduces the fixed half turn
    /// this used to carry. A source that hands over a different facing -- a bare skeletal mesh
    /// has no component transform to turn it, so the build itself has to say -- gets the turn
    /// that actually lands it on this basis' front.
    ///
    /// The turn is about the up axis and about nothing else: both directions are flattened
    /// onto the plane perpendicular to it first, and a forward parallel to up states no yaw at
    /// all, for which the identity is the honest answer.</summary>
    public (double[] Matrix, Quaternion Rotation) RootRotationFor(Vector3 sourceForward)
    {
        Vector3 facing = Flatten(ConvertPoint(sourceForward));
        Vector3 target = Flatten(Front);
        if (facing == Vector3.Zero || target == Vector3.Zero)
        {
            return (Mat4.Identity(), Quaternion.Identity);
        }
        double cosine = Vector3.Dot(facing, target);
        double sine = Vector3.Dot(Vector3.Cross(facing, target), Up);
        double[] rotation = Mat4.Identity();
        double[] axis = [Up.X, Up.Y, Up.Z];
        double[,] cross =
        {
            { 0.0, -axis[2], axis[1] },
            { axis[2], 0.0, -axis[0] },
            { -axis[1], axis[0], 0.0 },
        };
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                rotation[row * 4 + column] = (row == column ? cosine : 0.0)
                    + (cross[row, column] * sine)
                    + (axis[row] * axis[column] * (1.0 - cosine));
            }
        }
        return (rotation, Quaternion.CreateFromAxisAngle(Up, (float)Math.Atan2(sine, cosine)));
    }

    /// <summary>The rotation carrying one aim frame onto another: <c>to * fromᵀ</c>, with each
    /// frame written as the columns (right, up, forward). Both frames come from the same
    /// reflection, so the product is a proper rotation even though neither frame is.</summary>
    private static double[] TurnBetween(Vector3 fromForward, Vector3 fromUp,
        Vector3 toForward, Vector3 toUp)
    {
        double[] from = AimFrame(fromForward, fromUp);
        double[] to = AimFrame(toForward, toUp);
        double[] turn = Mat4.Identity();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double sum = 0.0;
                for (int axis = 0; axis < 3; axis++)
                {
                    sum += to[(row * 4) + axis] * from[(column * 4) + axis];
                }
                turn[(row * 4) + column] = sum;
            }
        }
        return turn;
    }

    private static double[] AimFrame(Vector3 forward, Vector3 up)
    {
        Vector3[] axes = [Vector3.Cross(forward, up), up, forward];
        double[] frame = Mat4.Identity();
        for (int column = 0; column < 3; column++)
        {
            frame[column] = axes[column].X;
            frame[4 + column] = axes[column].Y;
            frame[8 + column] = axes[column].Z;
        }
        return frame;
    }

    private Vector3 Flatten(Vector3 direction)
    {
        Vector3 flat = direction - (Vector3.Dot(direction, Up) * Up);
        return flat.Length() < 1e-9f ? Vector3.Zero : Vector3.Normalize(flat);
    }

    public Vector3 ConvertPoint(Vector3 point)
    {
        ReadOnlySpan<float> source = [point.X, point.Y, point.Z];
        return new Vector3(
            _signs[0] * source[_permutation[0]],
            _signs[1] * source[_permutation[1]],
            _signs[2] * source[_permutation[2]]);
    }

    public Vector3 ConvertScale(Vector3 scale)
    {
        ReadOnlySpan<float> source = [scale.X, scale.Y, scale.Z];
        return new Vector3(source[_permutation[0]], source[_permutation[1]], source[_permutation[2]]);
    }

    /// <summary>A rotation restated under the reflection: C R C is the proper rotation P R Pᵀ
    /// with P = -C, and a signed permutation P carries a quaternion by permuting its vector
    /// part -- negated here because P is minus the reflection.</summary>
    public Quaternion ConvertRotation(Quaternion rotation)
    {
        Vector3 vector = ConvertPoint(new Vector3(rotation.X, rotation.Y, rotation.Z));
        return ReversesWinding
            ? new Quaternion(-vector.X, -vector.Y, -vector.Z, rotation.W)
            : new Quaternion(vector.X, vector.Y, vector.Z, rotation.W);
    }

    /// <summary>A local TRS in this basis, for a node inside a tree -- no top-level turn.</summary>
    public (Vector3 Position, Quaternion Rotation, Vector3 Scale) ConvertTrs(
        Vector3 position, Quaternion rotation, Vector3 scale) =>
        (ConvertPoint(position), ConvertRotation(rotation), ConvertScale(scale));

    /// <summary>A TRS in this basis with the once-only turn on top, for a top-level node and
    /// for nothing else. How far that turn goes is the SOURCE's statement about which way its
    /// own asset space faces.</summary>
    public (Vector3 Position, Quaternion Rotation, Vector3 Scale) ConvertRootTrs(
        Vector3 position, Quaternion rotation, Vector3 scale, Vector3 sourceForward)
    {
        (Vector3 convertedPosition, Quaternion convertedRotation, Vector3 convertedScale) =
            ConvertTrs(position, rotation, scale);
        (double[] root, Quaternion turn) = RootRotationFor(sourceForward);
        Mat4.TransformPoint(root, convertedPosition.X, convertedPosition.Y, convertedPosition.Z,
            out double x, out double y, out double z);
        return (new Vector3((float)x, (float)y, (float)z),
            Quaternion.Normalize(Quaternion.Multiply(turn, convertedRotation)), convertedScale);
    }

    /// <summary>Convert an interleaved array of 3-component points or directions in place-free
    /// fashion: for a signed permutation this is a gather, exact and allocation-free.</summary>
    public float[] ConvertPoints(ReadOnlySpan<float> points)
    {
        float[] converted = new float[points.Length];
        int count = points.Length / 3;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<float> source = points.Slice(index * 3, 3);
            converted[index * 3] = _signs[0] * source[_permutation[0]];
            converted[index * 3 + 1] = _signs[1] * source[_permutation[1]];
            converted[index * 3 + 2] = _signs[2] * source[_permutation[2]];
        }
        return converted;
    }

    public float[] ConvertTangents(ReadOnlySpan<float> tangents)
    {
        float[] converted = new float[tangents.Length];
        int count = tangents.Length / 4;
        float wSign = TangentWSign;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<float> source = tangents.Slice(index * 4, 4);
            converted[index * 4] = _signs[0] * source[_permutation[0]];
            converted[index * 4 + 1] = _signs[1] * source[_permutation[1]];
            converted[index * 4 + 2] = _signs[2] * source[_permutation[2]];
            converted[index * 4 + 3] = source[3] * wSign;
        }
        return converted;
    }

    public float[] ConvertUvs(ReadOnlySpan<float> uvs)
    {
        float[] converted = uvs.ToArray();
        if (FlipV)
        {
            for (int index = 1; index < converted.Length; index += 2)
            {
                converted[index] = 1f - converted[index];
            }
        }
        return converted;
    }

    public uint[] ConvertTriangles(ReadOnlySpan<uint> indices)
    {
        uint[] converted = indices.ToArray();
        if (ReversesWinding)
        {
            for (int index = 0; index + 2 < converted.Length; index += 3)
            {
                (converted[index + 1], converted[index + 2]) = (converted[index + 2], converted[index + 1]);
            }
        }
        return converted;
    }

    public bool IsIdentity => _allPositive && !ReversesWinding && !FlipV
        && _permutation[0] == 0 && _permutation[1] == 1 && _permutation[2] == 2;
}
