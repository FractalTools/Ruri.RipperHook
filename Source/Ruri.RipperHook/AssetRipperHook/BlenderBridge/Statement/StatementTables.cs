using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>The statement as the tables a host reads, every number already in the requested
/// basis: geometry and transforms converted, winding reversed and tangent handedness signed
/// where the basis reflects, UVs flipped where its origin differs.</summary>
public static class StatementTables
{
    public const string Separator = ";";

    private static readonly string[] TrsColumns =
        ["px#", "py#", "pz#", "qx#", "qy#", "qz#", "qw#", "sx#", "sy#", "sz#"];

    public static ColumnTable Roots(string id, Statement statement, Basis basis)
    {
        TableBuilder table = new(id, ["seed", "node#", "label", "kind", .. TrsColumns]);
        table.Role(ColumnRole.Label, "label").Role(ColumnRole.Key | ColumnRole.Payload, "seed");
        foreach (StatementRoot root in statement.Roots)
        {
            (Vector3 position, Quaternion rotation, Vector3 scale) =
                basis.ConvertRootTrs(Vector3.Zero, Quaternion.Identity, Vector3.One, root.Forward);
            table.Row(root.Seed, root.Node, root.Label, root.Kind,
                position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W,
                scale.X, scale.Y, scale.Z);
        }
        return table.Build();
    }

    public static ColumnTable Nodes(string id, Statement statement, Basis basis)
    {
        TableBuilder table = new(id,
        [
            "node#", "parent#", "name", "path", "kind", "active#", "mesh", "skeleton", "materials", "anchor", .. TrsColumns,
            "light_kind#", "light_r#", "light_g#", "light_b#", "light_intensity#", "light_range#", "light_angle#",
            "light_inner_angle#", "light_width#", "light_height#", "light_shadows#", "light_volume#", "fov#", "near#",
            "far#", "ortho#",
            "ortho_size#", "tag", "cast_shadows#", "main_light_shadows#", "light_fade@", "light_parameters@",
        ]);
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Key, "path");
        foreach (StatementNode node in statement.Nodes)
        {
            (Vector3 position, Quaternion rotation, Vector3 scale) =
                basis.ConvertTrs(node.Position, node.Rotation, node.Scale);
            UnityLightInfo? light = node.Light;
            UnityCameraInfo? camera = node.Camera;
            table.Row(node.Index, node.Parent, node.Name, node.Path, node.Kind, node.Active ? 1 : 0, node.Mesh,
                node.Skeleton, string.Join(Separator, node.Materials), node.Anchor,
                position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W,
                scale.X, scale.Y, scale.Z,
                light?.Type ?? -1, light?.Red ?? 0f, light?.Green ?? 0f, light?.Blue ?? 0f, light?.Intensity ?? 0f,
                light?.Range ?? 0f, light?.SpotAngle ?? 0f, light?.InnerSpotAngle ?? 0f, light?.AreaWidth ?? 0f,
                light?.AreaHeight ?? 0f, light is { Shadows: true } ? 1 : 0, light?.VolumeFactor ?? 0f,
                camera?.FieldOfView ?? 0f, camera?.Near ?? 0f, camera?.Far ?? 0f,
                camera is null ? -1 : camera.Orthographic ? 1 : 0, camera?.OrthographicSize ?? 0f, camera?.Tag ?? string.Empty,
                node.Shadows is { } shadows ? (int)shadows : -1, node.MainLightShadows ? 1 : 0,
                Bytes<float>(light is null ? [] : [light.Fade.X, light.Fade.Y, light.Fade.Z, light.Fade.W]),
                Bytes<float>(light?.Parameters ?? []));
        }
        return table.Build();
    }

    public static ColumnTable Meshes(string id, Statement statement, Basis basis)
    {
        TableBuilder table = new(id, "key", "name", "positions@", "normals@", "tangents@", "colors@", "uv@", "uvSets",
            "indices@", "sections@", "skin@", "bones", "bindposes@", "skeleton", "lod#", "baked#");
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Key, "key");
        foreach (StatementMesh mesh in statement.Meshes)
        {
            DecodedMesh geometry = mesh.Geometry;
            float[] positions = basis.ConvertPoints(geometry.Positions ?? []);
            float[] normals = geometry.Normals is null ? [] : basis.ConvertPoints(geometry.Normals);
            float[] tangents = geometry.Tangents is null ? [] : basis.ConvertTangents(geometry.Tangents);
            float[] colors = geometry.Colors ?? [];
            List<float> uv = [];
            List<string> uvSets = [];
            foreach ((int set, float[] coordinates) in geometry.Uvs)
            {
                uv.AddRange(basis.ConvertUvs(coordinates));
                uvSets.Add(set.ToString(CultureInfo.InvariantCulture));
            }
            uint[] indices = basis.ConvertTriangles(geometry.Triangles);
            table.Row(mesh.Key, mesh.Name, Bytes<float>(positions), Bytes<float>(normals), Bytes<float>(tangents),
                Bytes<float>(colors), Bytes<float>(CollectionsMarshal.AsSpan(uv)), string.Join(Separator, uvSets),
                Bytes<uint>(indices), Bytes<int>(Sections(geometry.TriangleMaterial)), Skin(geometry),
                string.Join(Separator, mesh.BonePaths), Bytes<float>(BindPoses(geometry, basis)), mesh.Skeleton,
                mesh.Lod, mesh.Baked ? 1 : 0);
        }
        return table.Build();
    }

    /// <summary>(first index, index count, material slot) per run of triangles drawing one
    /// slot -- the order a material slot list is filled in.</summary>
    private static int[] Sections(int[] triangleMaterial)
    {
        List<int> sections = [];
        int start = 0;
        while (start < triangleMaterial.Length)
        {
            int slot = triangleMaterial[start];
            int end = start;
            while (end < triangleMaterial.Length && triangleMaterial[end] == slot)
            {
                end++;
            }
            sections.Add(start * 3);
            sections.Add((end - start) * 3);
            sections.Add(slot);
            start = end;
        }
        return sections.ToArray();
    }

    /// <summary>Four influences a vertex: weights then bone slots, the layout a decoder packs
    /// a skin in everywhere else. A wider skin is truncated to its first four and reported.</summary>
    private static byte[] Skin(DecodedMesh geometry)
    {
        if (geometry.BoneWeights is null || geometry.BoneIndices is null || geometry.InfluenceCount == 0)
        {
            return [];
        }
        int vertexCount = geometry.BoneIndices.Length / geometry.InfluenceCount;
        byte[] packed = new byte[vertexCount * 32];
        Span<byte> span = packed;
        int influences = geometry.InfluenceCount;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                float weight = slot < influences ? geometry.BoneWeights[vertex * influences + slot] : 0f;
                int index = slot < influences ? geometry.BoneIndices[vertex * influences + slot] : 0;
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(vertex * 32 + slot * 4, 4), weight);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(vertex * 32 + 16 + slot * 4, 4), index);
            }
        }
        return packed;
    }

    private static float[] BindPoses(DecodedMesh geometry, Basis basis)
    {
        if (geometry.BindPoses is null)
        {
            return [];
        }
        float[] converted = new float[geometry.BindPoses.Length];
        for (int bone = 0; bone < geometry.BindPoseCount; bone++)
        {
            double[] matrix = basis.ConvertMatrix(Mat4.FromFloats(geometry.BindPoses.AsSpan(bone * 16, 16)));
            for (int element = 0; element < 16; element++)
            {
                converted[bone * 16 + element] = (float)matrix[element];
            }
        }
        return converted;
    }

    public static ColumnTable Morphs(string id, Statement statement, Basis basis)
    {
        TableBuilder table = new(id, "mesh", "name", "vertices@", "delta_positions@", "delta_normals@", "delta_tangents@",
            "weight#", "frames#");
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Group, "mesh");
        foreach (StatementMorph morph in statement.Morphs)
        {
            table.Row(morph.Mesh, morph.Name, Bytes<uint>(morph.Vertices), Bytes<float>(basis.ConvertPoints(morph.DeltaPositions)),
                Bytes<float>(basis.ConvertPoints(morph.DeltaNormals)), Bytes<float>(basis.ConvertPoints(morph.DeltaTangents)),
                morph.Weight, morph.Frames);
        }
        return table.Build();
    }

    public static ColumnTable Skeletons(string id, Statement statement, Basis basis)
    {
        TableBuilder table = new(id, ["skeleton", "bone#", "parent#", "name", "path", "identity", .. TrsColumns, "humanoid"]);
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Group, "skeleton");
        foreach (StatementSkeleton skeleton in statement.Skeletons)
        {
            foreach (StatementBone bone in skeleton.Bones)
            {
                (Vector3 position, Quaternion rotation, Vector3 scale) =
                    basis.ConvertTrs(bone.Position, bone.Rotation, bone.Scale);
                table.Row(skeleton.Key, bone.Index, bone.Parent, bone.Name, bone.Path, bone.Identity,
                    position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W,
                    scale.X, scale.Y, scale.Z, bone.Humanoid);
            }
        }
        return table.Build();
    }

    public static ColumnTable Avatars(string id, Statement statement)
    {
        TableBuilder table = new(id, "skeleton", "avatar");
        table.Role(ColumnRole.Key, "skeleton");
        foreach (StatementSkeleton skeleton in statement.Skeletons)
        {
            if (skeleton.AvatarJson.Length > 0)
            {
                table.Row(skeleton.Key, skeleton.AvatarJson);
            }
        }
        return table.Build();
    }

    public const string MaterialRow = "m";
    public const string KeywordRow = "k";
    public const string TextureRow = "t";
    public const string ScalarRow = "f";
    public const string VectorRow = "c";
    public const string RoleRow = "r";
    public const string UnclaimedRow = "u";
    public const string PassRow = "p";
    public const string ShaderPassRow = "s";
    public const string EncodingRow = "n";

    public static ColumnTable Materials(string id, Statement statement)
    {
        TableBuilder table = new(id, "material", "kind", "name", "texture", "x#", "y#", "z#", "w#");
        table.Role(ColumnRole.Group, "material");
        foreach (StatementMaterial material in statement.Materials)
        {
            UnityMaterialProperties properties = material.Properties;
            table.Row(material.Key, MaterialRow, properties.Name, properties.ShaderName, 0, 0, 0, 0);
            foreach (string keyword in properties.KeywordList)
            {
                table.Row(material.Key, KeywordRow, keyword, string.Empty, 0, 0, 0, 0);
            }
            foreach (string pass in properties.DisabledPasses)
            {
                table.Row(material.Key, PassRow, pass, string.Empty, 0, 0, 0, 0);
            }
            foreach (UnityShaderPass pass in properties.ShaderPasses)
            {
                table.Row(material.Key, ShaderPassRow, pass.Name, pass.LightMode, 0, 0, 0, 0);
            }
            foreach ((string name, string texture) in properties.Textures)
            {
                float[] st = properties.TextureScaleOffset.TryGetValue(name, out float[]? stated) ? stated : [1f, 1f, 0f, 0f];
                table.Row(material.Key, TextureRow, name, texture, st[0], st[1], st[2], st[3]);
            }
            foreach ((string name, float value) in properties.FloatEntries)
            {
                table.Row(material.Key, ScalarRow, name, string.Empty, value, 0, 0, 0);
            }
            foreach ((string name, float[] value) in properties.ColorEntries)
            {
                table.Row(material.Key, VectorRow, name, string.Empty, value[0], value[1], value[2], value[3]);
            }
            foreach (TextureRoles.TextureRole role in material.Roles.Textures)
            {
                if (role.Encoding.Length > 0)
                {
                    table.Row(material.Key, EncodingRow, role.Encoding, role.Texture, 0, 0, 0, 0);
                }
                if (role.Role is not null)
                {
                    table.Row(material.Key, RoleRow, role.Role, role.Texture, -1, 0, 0, 0);
                }
                foreach ((string channelRole, int channel) in role.Channels)
                {
                    table.Row(material.Key, RoleRow, channelRole, role.Texture, channel, 0, 0, 0);
                }
            }
            foreach ((string role, float[] value) in material.Roles.Colors)
            {
                table.Row(material.Key, RoleRow, role, string.Empty, value[0], value[1], value[2], value[3]);
            }
            foreach ((string role, float value) in material.Roles.Floats)
            {
                table.Row(material.Key, RoleRow, role, string.Empty, value, 0, 0, 0);
            }
            foreach (string unmapped in material.Roles.Unmapped)
            {
                string texture = properties.Textures.FirstOrDefault(pair => pair.Key == unmapped).Value ?? string.Empty;
                table.Row(material.Key, UnclaimedRow, unmapped, texture, 0, 0, 0, 0);
            }
        }
        return table.Build();
    }

    public static ColumnTable Textures(string id, Statement statement)
    {
        // The pixels are NOT a column here. One selection's images run to gigabytes -- more than a
        // single array can hold and more than a host wants resident at once -- and a host loads them
        // one at a time regardless, so the bytes are their own per-texture answer.
        TableBuilder table = new(id, "texture", "name", "srgb#", "container", "bytes#", "wrap_u", "wrap_v", "filter");
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Key, "texture");
        foreach (StatementTexture texture in statement.Textures)
        {
            table.Row(texture.Key, texture.Name, texture.Srgb ? 1 : 0, texture.Container,
                texture.Image?.Length ?? 0, texture.Sampling.WrapU, texture.Sampling.WrapV, texture.Sampling.Filter);
        }
        return table.Build();
    }

    public static ColumnTable Clips(string id, IEnumerable<StatementClip> clips)
    {
        TableBuilder table = new(id, "clip", "name", "meta", "curves@", "cab");
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Key | ColumnRole.Payload, "clip");
        foreach (StatementClip clip in clips)
        {
            table.Row(clip.Key, clip.Name, clip.MetaJson, clip.Curves, clip.Archive);
        }
        return table.Build();
    }

    public static ColumnTable Report(string id, Statement statement)
    {
        TableBuilder table = new(id, "seed", "what", "count#", "detail");
        table.Role(ColumnRole.Label, "what").Role(ColumnRole.Group, "seed");
        foreach (StatementReport entry in statement.Report)
        {
            table.Row(entry.Seed, entry.What, entry.Count, entry.Detail);
        }
        return table.Build();
    }

    public static ColumnTable Bases(string id)
    {
        List<string> columns = ["basis", "reverses_winding#", "flip_v#"];
        for (int element = 0; element < 16; element++)
        {
            columns.Add("m" + element.ToString(CultureInfo.InvariantCulture) + "#");
        }
        for (int element = 0; element < 16; element++)
        {
            columns.Add("r" + element.ToString(CultureInfo.InvariantCulture) + "#");
        }
        for (int element = 0; element < 16; element++)
        {
            columns.Add("a" + element.ToString(CultureInfo.InvariantCulture) + "#");
        }
        TableBuilder table = new(id, columns.ToArray());
        table.Role(ColumnRole.Label | ColumnRole.Key, "basis");
        foreach (Basis basis in Basis.All)
        {
            List<object> values = [basis.Name, basis.ReversesWinding ? 1 : 0, basis.FlipV ? 1 : 0];
            foreach (double element in basis.Matrix)
            {
                values.Add(element);
            }
            foreach (double element in basis.RootRotation)
            {
                values.Add(element);
            }
            foreach (double element in basis.AimTurn)
            {
                values.Add(element);
            }
            table.Row(values.ToArray());
        }
        return table.Build();
    }

    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : struct =>
        MemoryMarshal.AsBytes(values).ToArray();
}
