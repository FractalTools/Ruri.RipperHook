using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Ruri.ACL;

internal static class AclMath
{
    public static Vector4 ToVector(Quaternion rotation) => new(rotation.X, rotation.Y, rotation.Z, rotation.W);
    public static Quaternion ToQuaternion(Vector4 value) => new(value.X, value.Y, value.Z, value.W);
    public static Vector3 ToVector3(Vector4 value) => new(value.X, value.Y, value.Z);
    public static Quaternion EnsurePositiveW(Quaternion value) => BitConverter.SingleToInt32Bits(value.W) < 0 ? new(-value.X, -value.Y, -value.Z, -value.W) : value;

    public static Quaternion Normalize(Quaternion value)
    {
        float dot = (value.X * value.X + value.Z * value.Z) + (value.Y * value.Y + value.W * value.W);
        float inverseLength = SqrtReciprocal(dot);
        return new(value.X * inverseLength, value.Y * inverseLength, value.Z * inverseLength, value.W * inverseLength);
    }

    private static float SqrtReciprocal(float input)
    {
        if (Sse.IsSupported)
        {
            float halfInput = input * 0.5f;
            float estimate = Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(input)).ToScalar();
            float first = estimate * (0.5f - halfInput * (estimate * estimate)) + estimate;
            return first * (0.5f - halfInput * (first * first)) + first;
        }
        return 1.0f / MathF.Sqrt(input);
    }

    public static Quaternion FromPositiveW(Vector4 value) => new(value.X, value.Y, value.Z, MathF.Sqrt(MathF.Abs(((1.0f - value.X * value.X) - value.Y * value.Y) - value.Z * value.Z)));

    public static Quaternion QuatLerp(Quaternion start, Quaternion end, float alpha)
    {
        float dot = (start.X * end.X + start.Z * end.Z) + (start.Y * end.Y + start.W * end.W);
        if (BitConverter.SingleToInt32Bits(dot) < 0)
            end = new(-end.X, -end.Y, -end.Z, -end.W);
        return Normalize(new Quaternion((start.X - alpha * start.X) + alpha * end.X,
            (start.Y - alpha * start.Y) + alpha * end.Y,
            (start.Z - alpha * start.Z) + alpha * end.Z,
            (start.W - alpha * start.W) + alpha * end.W));
    }

    public static Vector3 VectorLerp(Vector3 start, Vector3 end, float alpha) => (start - start * alpha) + end * alpha;
    public static Vector4 VectorLerp(Vector4 start, Vector4 end, float alpha) => (start - start * alpha) + end * alpha;

    public static Quaternion QuatMul(Quaternion left, Quaternion right)
    {
        if (Sse.IsSupported)
        {
            Vector128<float> controlWzyx = Vector128.Create(0.0F, -0.0F, 0.0F, -0.0F);
            Vector128<float> controlZwxy = Vector128.Create(0.0F, 0.0F, -0.0F, -0.0F);
            Vector128<float> controlYxwz = Vector128.Create(-0.0F, 0.0F, 0.0F, -0.0F);
            Vector128<float> leftVector = Vector128.Create(left.X, left.Y, left.Z, left.W);
            Vector128<float> rightVector = Vector128.Create(right.X, right.Y, right.Z, right.W);
            Vector128<float> rightX = Sse.Shuffle(rightVector, rightVector, 0x00);
            Vector128<float> rightY = Sse.Shuffle(rightVector, rightVector, 0x55);
            Vector128<float> rightZ = Sse.Shuffle(rightVector, rightVector, 0xAA);
            Vector128<float> rightW = Sse.Shuffle(rightVector, rightVector, 0xFF);
            Vector128<float> productW = Sse.Multiply(rightW, leftVector);
            Vector128<float> leftWzyx = Sse.Shuffle(leftVector, leftVector, 0x1B);
            Vector128<float> productX = Sse.Multiply(rightX, leftWzyx);
            Vector128<float> leftZwxy = Sse.Shuffle(leftWzyx, leftWzyx, 0xB1);
            productX = Sse.Xor(productX, controlWzyx);
            Vector128<float> productY = Sse.Multiply(rightY, leftZwxy);
            Vector128<float> leftYxwz = Sse.Shuffle(leftZwxy, leftZwxy, 0x1B);
            productY = Sse.Xor(productY, controlZwxy);
            Vector128<float> productZ = Sse.Multiply(rightZ, leftYxwz);
            Vector128<float> result0 = Sse.Add(productW, productX);
            productZ = Sse.Xor(productZ, controlYxwz);
            Vector128<float> result1 = Sse.Add(productY, productZ);
            Vector128<float> result = Sse.Add(result0, result1);
            return new Quaternion(result.GetElement(0), result.GetElement(1), result.GetElement(2), result.GetElement(3));
        }
        return new Quaternion(
            ((right.W * left.X + right.X * left.W) + right.Y * left.Z) - right.Z * left.Y,
            ((right.W * left.Y - right.X * left.Z) + right.Y * left.W) + right.Z * left.X,
            ((right.W * left.Z + right.X * left.Y) - right.Y * left.X) + right.Z * left.W,
            ((right.W * left.W - right.X * left.X) - right.Y * left.Y) - right.Z * left.Z);
    }

    public static Vector3 Rotate(Vector3 vector, Quaternion rotation)
    {
        if (Sse.IsSupported)
        {
            Vector128<float> rotationVector = Vector128.Create(rotation.X, rotation.Y, rotation.Z, rotation.W);
            Vector128<float> inverseRotation = Sse.Xor(rotationVector, Vector128.Create(-0.0F, -0.0F, -0.0F, 0.0F));
            Vector128<float> rotationTemporary0 = Sse.Shuffle(rotationVector, inverseRotation, 0xC9);
            Vector128<float> rotationTemporary1 = Sse.Shuffle(rotationVector, inverseRotation, 0xD8);
            Vector128<float> input = Vector128.Create(vector.X, vector.Y, vector.Z, 0.0F);
            Vector128<float> inputX = Sse.Shuffle(input, input, 0x00);
            Vector128<float> inputY = Sse.Shuffle(input, input, 0x55);
            Vector128<float> inputZ = Sse.Shuffle(input, input, 0xAA);
            Vector128<float> rotationTemporary2 = Sse.Shuffle(rotationTemporary0, rotationTemporary1, 0x27);
            Vector128<float> productX = Sse.Multiply(inputX, rotationTemporary2);
            Vector128<float> rotationTemporary3 = Sse.Shuffle(inverseRotation, rotationVector, 0x4E);
            Vector128<float> productY = Sse.Multiply(inputY, rotationTemporary3);
            Vector128<float> rotationTemporary4 = Sse.Shuffle(rotationTemporary0, rotationTemporary1, 0x78);
            Vector128<float> productZ = Sse.Multiply(inputZ, rotationTemporary4);
            Vector128<float> temporary = Sse.Add(Sse.Add(productX, productY), productZ);
            rotationTemporary0 = Sse.Shuffle(rotationVector, inverseRotation, 0x88);
            Vector128<float> rightX = Sse.Shuffle(rotationTemporary0, rotationTemporary0, 0x88);
            Vector128<float> rightY = Sse.Shuffle(rotationVector, inverseRotation, 0x55);
            Vector128<float> rightZ = Sse.Shuffle(rotationTemporary0, rotationTemporary0, 0xD7);
            Vector128<float> rightW = Sse.Shuffle(rotationVector, rotationVector, 0xFF);
            Vector128<float> productW = Sse.Multiply(rightW, temporary);
            Vector128<float> temporaryWzyx = Sse.Shuffle(temporary, temporary, 0x1B);
            productX = Sse.Multiply(rightX, temporaryWzyx);
            Vector128<float> temporaryZwxy = Sse.Shuffle(temporaryWzyx, temporaryWzyx, 0xB1);
            productY = Sse.Multiply(rightY, temporaryZwxy);
            Vector128<float> temporaryYxwz = Sse.Shuffle(temporaryZwxy, temporaryZwxy, 0x1B);
            productZ = Sse.Multiply(rightZ, temporaryYxwz);
            Vector128<float> result0 = Sse.Add(productW, productX);
            Vector128<float> result1 = Sse.Add(productY, productZ);
            Vector128<float> result = Sse.Add(result0, result1);
            return new Vector3(result.GetElement(0), result.GetElement(1), result.GetElement(2));
        }
        Quaternion inverse = new(-rotation.X, -rotation.Y, -rotation.Z, rotation.W);
        Quaternion scalarTemporary = QuatMul(inverse, new Quaternion(vector, 0.0f));
        Quaternion scalarResult = QuatMul(scalarTemporary, rotation);
        return new Vector3(scalarResult.X, scalarResult.Y, scalarResult.Z);
    }

    public static AclTransform QvvMul(AclTransform left, AclTransform right, bool hasScale = true)
    {
        if (!hasScale)
            return new() { Rotation = QuatMul(left.Rotation, right.Rotation), Translation = Rotate(left.Translation, right.Rotation) + right.Translation, Scale = left.Scale };
        Vector3 scale = left.Scale * right.Scale;
        Vector3 minimumScale = Vector3.Min(left.Scale, right.Scale);
        if (minimumScale.X < 0.0f || minimumScale.Y < 0.0f || minimumScale.Z < 0.0f)
        {
            MatrixFromQvv(left, out Vector3 leftX, out Vector3 leftY, out Vector3 leftZ);
            MatrixFromQvv(right, out Vector3 rightX, out Vector3 rightY, out Vector3 rightZ);
            Vector3 x = MatrixMulAxis(leftX, rightX, rightY, rightZ);
            Vector3 y = MatrixMulAxis(leftY, rightX, rightY, rightZ);
            Vector3 z = MatrixMulAxis(leftZ, rightX, rightY, rightZ);
            Vector3 translation = MatrixMulAxis(left.Translation, rightX, rightY, rightZ) + right.Translation;
            x = NormalizeAxis(x) * MathF.CopySign(1.0f, scale.X);
            y = NormalizeAxis(y) * MathF.CopySign(1.0f, scale.Y);
            z = NormalizeAxis(z) * MathF.CopySign(1.0f, scale.Z);
            return new() { Rotation = QuatFromMatrix(x, y, z), Translation = translation, Scale = scale };
        }
        return new() { Rotation = QuatMul(left.Rotation, right.Rotation), Translation = Rotate(left.Translation * right.Scale, right.Rotation) + right.Translation, Scale = scale };
    }

    private static void MatrixFromQvv(AclTransform transform, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        Quaternion rotation = transform.Rotation;
        float x2 = rotation.X + rotation.X;
        float y2 = rotation.Y + rotation.Y;
        float z2 = rotation.Z + rotation.Z;
        float xx = rotation.X * x2;
        float xy = rotation.X * y2;
        float xz = rotation.X * z2;
        float yy = rotation.Y * y2;
        float yz = rotation.Y * z2;
        float zz = rotation.Z * z2;
        float wx = rotation.W * x2;
        float wy = rotation.W * y2;
        float wz = rotation.W * z2;
        x = new Vector3(1.0f - (yy + zz), xy + wz, xz - wy) * transform.Scale.X;
        y = new Vector3(xy - wz, 1.0f - (xx + zz), yz + wx) * transform.Scale.Y;
        z = new Vector3(xz + wy, yz - wx, 1.0f - (xx + yy)) * transform.Scale.Z;
    }

    private static Vector3 MatrixMulAxis(Vector3 axis, Vector3 rightX, Vector3 rightY, Vector3 rightZ)
    {
        Vector3 result = rightX * axis.X;
        result = rightY * axis.Y + result;
        return rightZ * axis.Z + result;
    }

    private static Vector3 NormalizeAxis(Vector3 axis)
    {
        float lengthSquared = (axis.X * axis.X + axis.Y * axis.Y) + axis.Z * axis.Z;
        return lengthSquared >= 1.0e-8f ? axis * SqrtReciprocal(lengthSquared) : axis;
    }

    private static Quaternion QuatFromMatrix(Vector3 x, Vector3 y, Vector3 z)
    {
        if (NearZero(x) || NearZero(y) || NearZero(z))
            return Quaternion.Identity;
        float trace = x.X + y.Y + z.Z;
        if (trace > 0.0f)
        {
            float inverseTrace = SqrtReciprocal(trace + 1.0f);
            float halfInverseTrace = inverseTrace * 0.5f;
            return Normalize(new((y.Z - z.Y) * halfInverseTrace, (z.X - x.Z) * halfInverseTrace, (x.Y - y.X) * halfInverseTrace, (1.0f / inverseTrace) * 0.5f));
        }
        Span<Vector3> axes = stackalloc Vector3[3] { x, y, z };
        int best = y.Y > x.X ? 1 : 0;
        if (z.Z > axes[best][best])
            best = 2;
        int next = (best + 1) % 3;
        int last = (next + 1) % 3;
        float inversePseudoTrace = SqrtReciprocal(((1.0f + axes[best][best]) - axes[next][next]) - axes[last][last]);
        float halfInverse = inversePseudoTrace * 0.5f;
        Span<float> components = stackalloc float[4];
        components[best] = (1.0f / inversePseudoTrace) * 0.5f;
        components[next] = halfInverse * (axes[best][next] + axes[next][best]);
        components[last] = halfInverse * (axes[best][last] + axes[last][best]);
        components[3] = halfInverse * (axes[next][last] - axes[last][next]);
        return Normalize(new(components[0], components[1], components[2], components[3]));
    }

    private static bool NearZero(Vector3 value) => MathF.Abs(value.X) <= 1.0e-5f && MathF.Abs(value.Y) <= 1.0e-5f && MathF.Abs(value.Z) <= 1.0e-5f;

    public static Vector3 TransformPoint(Vector3 point, AclTransform transform, bool hasScale = true) => Rotate(hasScale ? point * transform.Scale : point, transform.Rotation) + transform.Translation;

    public static float CalculateError(AclTransform raw, AclTransform lossy, float shellDistance, bool hasScale = true)
    {
        Vector3 pointX = new(shellDistance, 0.0f, 0.0f);
        Vector3 pointY = new(0.0f, shellDistance, 0.0f);
        float errorX = Vector3.Distance(TransformPoint(pointX, raw, hasScale), TransformPoint(pointX, lossy, hasScale));
        float errorY = Vector3.Distance(TransformPoint(pointY, raw, hasScale), TransformPoint(pointY, lossy, hasScale));
        float error = MathF.Max(errorX, errorY);
        if (hasScale)
        {
            Vector3 pointZ = new(0.0f, 0.0f, shellDistance);
            error = MathF.Max(error, Vector3.Distance(TransformPoint(pointZ, raw), TransformPoint(pointZ, lossy)));
        }
        return error;
    }

    public static AclTransform QvvLerp(AclTransform start, AclTransform end, float alpha) => new()
    {
        Rotation = QuatLerp(start.Rotation, end.Rotation, alpha),
        Translation = VectorLerp(start.Translation, end.Translation, alpha),
        Scale = VectorLerp(start.Scale, end.Scale, alpha),
    };

    public static void LocalToObjectSpace(ReadOnlySpan<AclTransform> local, ReadOnlySpan<int> parents, ReadOnlySpan<int> dirty, Span<AclTransform> output, bool hasScale)
    {
        foreach (int index in dirty)
        {
            int parent = parents[index];
            AclTransform transform = parent == -1 ? local[index] : QvvMul(local[index], output[parent], hasScale);
            if (parent != -1)
                transform.Rotation = Normalize(transform.Rotation);
            output[index] = transform;
        }
    }
}
