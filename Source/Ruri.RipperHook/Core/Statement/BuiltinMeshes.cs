namespace Ruri.RipperHook.Statements;

/// <summary>
/// Unity's own built-in primitives, rebuilt to spec. A scene that stands a Cube in a doorway
/// references <c>unity default resources</c>, the engine's own file, which no game's data
/// contains and no extraction ever will; so the primitive is built from the engine's
/// definition, addressed by the file id Unity gives it there. Cube, Quad and Plane are fully
/// determined shapes and come out vertex for vertex; Sphere, Cylinder and Capsule are the
/// engine's dimensions with a tessellation of this file's choosing, and are said to be.
/// Geometry is in Unity coordinates like every other decode.
/// </summary>
public static class BuiltinMeshes
{
    public const string ResourcesGuid = "0000000000000000e000000000000000";

    public const string ResourcesCollectionName = "unity default resources";

    private static readonly Dictionary<long, string> Primitives = new()
    {
        [10202] = "Cube",
        [10206] = "Cylinder",
        [10207] = "Sphere",
        [10208] = "Capsule",
        [10209] = "Plane",
        [10210] = "Quad",
    };

    private static readonly HashSet<string> Exact = ["Cube", "Quad", "Plane"];

    public static string? NameOf(long fileId) => Primitives.GetValueOrDefault(fileId);

    public static bool IsExact(string name) => Exact.Contains(name);

    public static DecodedMesh? Build(string name)
    {
        (float[] Positions, float[] Normals, float[] Uvs, uint[] Triangles)? built = name switch
        {
            "Quad" => Quad(),
            "Cube" => Cube(),
            "Plane" => Plane(),
            "Sphere" => Sphere(24, 16),
            "Cylinder" => Cylinder(24),
            "Capsule" => Capsule(24, 16),
            _ => null,
        };
        if (built is null)
        {
            return null;
        }
        (float[] positions, float[] normals, float[] uvs, uint[] triangles) = built.Value;
        DecodedMesh mesh = new(name)
        {
            Positions = positions,
            Normals = normals,
            VertexCount = positions.Length / 3,
            Triangles = triangles,
            TriangleMaterial = new int[triangles.Length / 3],
            Builtin = $"{name} ({(IsExact(name) ? "exact" : "reconstructed")})",
        };
        mesh.Uvs[0] = uvs;
        mesh.SubMeshes.Add(new DecodedSubMesh(0, triangles.Length, 0, 0, 0, mesh.VertexCount));
        return mesh;
    }

    private sealed class Builder
    {
        public List<float> Positions { get; } = [];
        public List<float> Normals { get; } = [];
        public List<float> Uvs { get; } = [];
        public List<uint> Triangles { get; } = [];

        public int Count => Positions.Count / 3;

        public void Vertex(float x, float y, float z, float nx, float ny, float nz, float u, float v)
        {
            Positions.Add(x); Positions.Add(y); Positions.Add(z);
            Normals.Add(nx); Normals.Add(ny); Normals.Add(nz);
            Uvs.Add(u); Uvs.Add(v);
        }

        public void Triangle(int a, int b, int c)
        {
            Triangles.Add((uint)a); Triangles.Add((uint)b); Triangles.Add((uint)c);
        }

        public (float[], float[], float[], uint[]) Done() =>
            (Positions.ToArray(), Normals.ToArray(), Uvs.ToArray(), Triangles.ToArray());
    }

    private static (float[], float[], float[], uint[]) Quad()
    {
        Builder builder = new();
        builder.Vertex(-0.5f, -0.5f, 0f, 0f, 0f, -1f, 0f, 0f);
        builder.Vertex(0.5f, -0.5f, 0f, 0f, 0f, -1f, 1f, 0f);
        builder.Vertex(0.5f, 0.5f, 0f, 0f, 0f, -1f, 1f, 1f);
        builder.Vertex(-0.5f, 0.5f, 0f, 0f, 0f, -1f, 0f, 1f);
        builder.Triangle(0, 1, 2);
        builder.Triangle(0, 2, 3);
        return builder.Done();
    }

    private static (float[], float[], float[], uint[]) Cube()
    {
        (float[] Normal, float[][] Corners)[] faces =
        [
            ([0, 0, -1], [[-0.5f, -0.5f, -0.5f], [0.5f, -0.5f, -0.5f], [0.5f, 0.5f, -0.5f], [-0.5f, 0.5f, -0.5f]]),
            ([0, 0, 1], [[0.5f, -0.5f, 0.5f], [-0.5f, -0.5f, 0.5f], [-0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f]]),
            ([-1, 0, 0], [[-0.5f, -0.5f, 0.5f], [-0.5f, -0.5f, -0.5f], [-0.5f, 0.5f, -0.5f], [-0.5f, 0.5f, 0.5f]]),
            ([1, 0, 0], [[0.5f, -0.5f, -0.5f], [0.5f, -0.5f, 0.5f], [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, -0.5f]]),
            ([0, 1, 0], [[-0.5f, 0.5f, -0.5f], [0.5f, 0.5f, -0.5f], [0.5f, 0.5f, 0.5f], [-0.5f, 0.5f, 0.5f]]),
            ([0, -1, 0], [[-0.5f, -0.5f, 0.5f], [0.5f, -0.5f, 0.5f], [0.5f, -0.5f, -0.5f], [-0.5f, -0.5f, -0.5f]]),
        ];
        float[][] uvs = [[0f, 0f], [1f, 0f], [1f, 1f], [0f, 1f]];
        Builder builder = new();
        foreach ((float[] normal, float[][] corners) in faces)
        {
            int baseIndex = builder.Count;
            for (int corner = 0; corner < 4; corner++)
            {
                builder.Vertex(corners[corner][0], corners[corner][1], corners[corner][2],
                    normal[0], normal[1], normal[2], uvs[corner][0], uvs[corner][1]);
            }
            builder.Triangle(baseIndex, baseIndex + 1, baseIndex + 2);
            builder.Triangle(baseIndex, baseIndex + 2, baseIndex + 3);
        }
        return builder.Done();
    }

    private static (float[], float[], float[], uint[]) Plane()
    {
        const int Steps = 10;
        Builder builder = new();
        for (int row = 0; row <= Steps; row++)
        {
            for (int column = 0; column <= Steps; column++)
            {
                float u = column / (float)Steps;
                float v = row / (float)Steps;
                builder.Vertex(u * 10f - 5f, 0f, v * 10f - 5f, 0f, 1f, 0f, u, v);
            }
        }
        for (int row = 0; row < Steps; row++)
        {
            for (int column = 0; column < Steps; column++)
            {
                int here = row * (Steps + 1) + column;
                builder.Triangle(here, here + Steps + 1, here + Steps + 2);
                builder.Triangle(here, here + Steps + 2, here + 1);
            }
        }
        return builder.Done();
    }

    private static (float[], float[], float[], uint[]) Sphere(int segments, int rings)
    {
        Builder builder = new();
        for (int ring = 0; ring <= rings; ring++)
        {
            double v = ring / (double)rings;
            double polar = v * Math.PI;
            double y = Math.Cos(polar);
            double radius = Math.Sin(polar);
            for (int segment = 0; segment <= segments; segment++)
            {
                double u = segment / (double)segments;
                double azimuth = u * 2.0 * Math.PI;
                float dx = (float)(Math.Sin(azimuth) * radius);
                float dy = (float)y;
                float dz = (float)(Math.Cos(azimuth) * radius);
                builder.Vertex(dx * 0.5f, dy * 0.5f, dz * 0.5f, dx, dy, dz, (float)u, (float)(1.0 - v));
            }
        }
        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                int here = ring * (segments + 1) + segment;
                int below = here + segments + 1;
                builder.Triangle(here, below, below + 1);
                builder.Triangle(here, below + 1, here + 1);
            }
        }
        return builder.Done();
    }

    private static (float[], float[], float[], uint[]) Cylinder(int segments)
    {
        Builder builder = new();
        const float Half = 1f;
        for (int segment = 0; segment <= segments; segment++)
        {
            double u = segment / (double)segments;
            double azimuth = u * 2.0 * Math.PI;
            float x = (float)(Math.Sin(azimuth) * 0.5);
            float z = (float)(Math.Cos(azimuth) * 0.5);
            float dx = (float)Math.Sin(azimuth);
            float dz = (float)Math.Cos(azimuth);
            builder.Vertex(x, Half, z, dx, 0f, dz, (float)u, 1f);
            builder.Vertex(x, -Half, z, dx, 0f, dz, (float)u, 0f);
        }
        for (int segment = 0; segment < segments; segment++)
        {
            int top = segment * 2;
            int bottom = segment * 2 + 1;
            builder.Triangle(top, bottom, bottom + 2);
            builder.Triangle(top, bottom + 2, top + 2);
        }
        foreach ((float sign, float normalY) in new[] { (Half, 1f), (-Half, -1f) })
        {
            int centre = builder.Count;
            builder.Vertex(0f, sign, 0f, 0f, normalY, 0f, 0.5f, 0.5f);
            for (int segment = 0; segment <= segments; segment++)
            {
                double azimuth = segment / (double)segments * 2.0 * Math.PI;
                float x = (float)(Math.Sin(azimuth) * 0.5);
                float z = (float)(Math.Cos(azimuth) * 0.5);
                builder.Vertex(x, sign, z, 0f, normalY, 0f, x + 0.5f, z + 0.5f);
            }
            for (int segment = 0; segment < segments; segment++)
            {
                int rim = centre + 1 + segment;
                if (sign > 0f)
                {
                    builder.Triangle(centre, rim, rim + 1);
                }
                else
                {
                    builder.Triangle(centre, rim + 1, rim);
                }
            }
        }
        return builder.Done();
    }

    private static (float[], float[], float[], uint[]) Capsule(int segments, int rings)
    {
        Builder builder = new();
        const float Offset = 0.5f;
        for (int ring = 0; ring <= rings; ring++)
        {
            double v = ring / (double)rings;
            double polar = v * Math.PI;
            float y = (float)Math.Cos(polar);
            float radius = (float)Math.Sin(polar);
            float shift = y >= 0f ? Offset : -Offset;
            for (int segment = 0; segment <= segments; segment++)
            {
                double u = segment / (double)segments;
                double azimuth = u * 2.0 * Math.PI;
                float dx = (float)(Math.Sin(azimuth) * radius);
                float dz = (float)(Math.Cos(azimuth) * radius);
                builder.Vertex(dx * 0.5f, y * 0.5f + shift, dz * 0.5f, dx, y, dz, (float)u, (float)(1.0 - v));
            }
        }
        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                int here = ring * (segments + 1) + segment;
                int below = here + segments + 1;
                builder.Triangle(here, below, below + 1);
                builder.Triangle(here, below + 1, here + 1);
            }
        }
        return builder.Done();
    }
}
