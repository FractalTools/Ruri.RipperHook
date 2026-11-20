using System.Buffers.Binary;
using System.Numerics;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.RuriEngine;

internal static class MeshDecoder
{
    private const int PositionChannel = 0;
    private const int NormalChannel = 1;
    private const int ColorChannel = 3;
    private const int FirstTextureChannel = 4;
    private const int TextureChannelCount = 8;
    private const int BlendWeightChannel = 12;
    private const int BlendIndicesChannel = 13;
    private const int ChannelLayoutVersion = 2018;
    private const int DimensionMask = 0xF;
    private const int TextureCoordinateDimension = 2;
    private const int TrianglesTopology = 0;
    private const int QuadsTopology = 2;
    private const string VertexInterpolation = "vertex";
    private const string NoSubdivision = "none";
    private const string MaterialBindFamily = "materialBind";

    public static ObjectValue? Decode(DecodeContext context)
    {
        if (context.Collection.Version.LessThan(ChannelLayoutVersion))
        {
            context.Unsupported($"vertex channel layout of Unity {context.Collection.Version}");
            return null;
        }
        if (!IsInline(context))
        {
            return null;
        }
        VertexBuffer? vertices = ReadVertices(context, out int vertexCount);
        if (vertices is null || !vertices.Has(PositionChannel))
        {
            context.Unsupported("mesh without vertex positions");
            return null;
        }

        ObjectValue mesh = new();
        mesh.Add("Extent", ReadExtent(context));
        mesh.Add("Points", Mirrored(Vectors(vertices, PositionChannel)));
        if (vertices.Has(NormalChannel))
        {
            mesh.Add("Normals", Mirrored(Vectors(vertices, NormalChannel)));
        }
        mesh.Add("SubdivisionScheme", ScalarValue.FromText(NoSubdivision));
        ReadFaces(context, vertexCount, mesh);
        ReadColors(context, vertices, mesh);
        ReadTextureCoordinates(context, vertices, mesh);
        ReadSkin(context, vertices, mesh);
        ReadBlendShapes(context, mesh);
        return mesh;
    }

    private static bool IsInline(DecodeContext context)
    {
        bool inline = true;
        if (context.Take("m_MeshCompression") is { } compression && YamlNodes.Integer(compression, "m_MeshCompression") != 0)
        {
            context.Unsupported("compressed vertex data (m_MeshCompression)");
            inline = false;
        }
        context.Take("m_CompressedMesh");
        if (context.Take("m_StreamData") is UnityYamlMapping stream && YamlNodes.Optional(stream, "size") is { } size && YamlNodes.Integer(size, "m_StreamData.size") != 0)
        {
            context.Unsupported("vertex data in an external stream (m_StreamData)");
            inline = false;
        }
        if (context.Take("m_VariableBoneCountWeights") is UnityYamlMapping variable && YamlNodes.Optional(variable, "m_Data") is { } weights && weights is not UnityYamlScalar { IsEmpty: true })
        {
            context.Unsupported("variable bone count weights (m_VariableBoneCountWeights)");
            inline = false;
        }
        return inline;
    }

    private static VertexBuffer? ReadVertices(DecodeContext context, out int vertexCount)
    {
        UnityYamlMapping vertexData = YamlNodes.Mapping(context.Take("m_VertexData"), "m_VertexData");
        vertexCount = checked((int)YamlNodes.Integer(YamlNodes.Field(vertexData, "m_VertexCount", "m_VertexData"), "m_VertexData.m_VertexCount"));
        List<VertexChannel> channels = new();
        foreach (UnityYamlNode node in YamlNodes.Items(YamlNodes.Field(vertexData, "m_Channels", "m_VertexData"), "m_VertexData.m_Channels"))
        {
            string path = $"m_VertexData.m_Channels[{channels.Count}]";
            UnityYamlMapping channel = YamlNodes.Mapping(node, path);
            channels.Add(new VertexChannel(
                checked((int)YamlNodes.Integer(YamlNodes.Field(channel, "stream", path), path + ".stream")),
                checked((int)YamlNodes.Integer(YamlNodes.Field(channel, "offset", path), path + ".offset")),
                checked((int)YamlNodes.Integer(YamlNodes.Field(channel, "format", path), path + ".format")),
                checked((int)YamlNodes.Integer(YamlNodes.Field(channel, "dimension", path), path + ".dimension")) & DimensionMask));
        }
        byte[] data = YamlNodes.Bytes(YamlNodes.Optional(vertexData, "_typelessdata"), "m_VertexData._typelessdata");
        return vertexCount == 0 ? null : new VertexBuffer(data, vertexCount, [.. channels], context.Collection.Version);
    }

    private static ArrayValue ReadExtent(DecodeContext context)
    {
        UnityYamlMapping bounds = YamlNodes.Mapping(context.Take("m_LocalAABB"), "m_LocalAABB");
        (double X, double Y, double Z) center = YamlNodes.Vector(YamlNodes.Field(bounds, "m_Center", "m_LocalAABB"), "m_LocalAABB.m_Center");
        (double X, double Y, double Z) extent = YamlNodes.Vector(YamlNodes.Field(bounds, "m_Extent", "m_LocalAABB"), "m_LocalAABB.m_Extent");
        ArrayValue range = new();
        range.Items.Add(EngineValues.Vector3(-(center.X + extent.X), center.Y - extent.Y, center.Z - extent.Z));
        range.Items.Add(EngineValues.Vector3(-(center.X - extent.X), center.Y + extent.Y, center.Z + extent.Z));
        return range;
    }

    private static Vector3[] Vectors(VertexBuffer vertices, int channel)
    {
        return MeshHelper.FloatArrayToVector3(vertices.Floats(channel), vertices.Dimension(channel));
    }

    private static ArrayValue Mirrored(Vector3[] vectors)
    {
        ArrayValue array = new();
        foreach (Vector3 vector in vectors)
        {
            array.Items.Add(EngineValues.Vector3(-vector.X, vector.Y, vector.Z));
        }
        return array;
    }

    private static void ReadFaces(DecodeContext context, int vertexCount, ObjectValue mesh)
    {
        int indexFormat = context.Take("m_IndexFormat") is { } format ? checked((int)YamlNodes.Integer(format, "m_IndexFormat")) : 0;
        byte[] indexBytes = YamlNodes.Bytes(context.Take("m_IndexBuffer"), "m_IndexBuffer");
        int indexSize = indexFormat == 0 ? sizeof(ushort) : sizeof(uint);
        List<int> counts = new();
        List<int> corners = new();
        List<int[]> subsets = new();
        foreach (UnityYamlNode node in YamlNodes.Items(context.Take("m_SubMeshes"), "m_SubMeshes"))
        {
            string path = $"m_SubMeshes[{subsets.Count}]";
            UnityYamlMapping submesh = YamlNodes.Mapping(node, path);
            int firstByte = checked((int)YamlNodes.Integer(YamlNodes.Field(submesh, "firstByte", path), path + ".firstByte"));
            int indexCount = checked((int)YamlNodes.Integer(YamlNodes.Field(submesh, "indexCount", path), path + ".indexCount"));
            int topology = checked((int)YamlNodes.Integer(YamlNodes.Field(submesh, "topology", path), path + ".topology"));
            int baseVertex = checked((int)YamlNodes.Integer(YamlNodes.Field(submesh, "baseVertex", path), path + ".baseVertex"));
            int cornerCount = topology switch
            {
                TrianglesTopology => 3,
                QuadsTopology => 4,
                _ => 0,
            };
            if (cornerCount == 0 || indexCount % cornerCount != 0)
            {
                context.Unsupported($"sub-mesh topology {topology} with {indexCount} indices has no UsdGeomMesh faces");
                subsets.Add([]);
                continue;
            }
            if ((long)firstByte + (long)indexCount * indexSize > indexBytes.Length)
            {
                throw new FormatException($"{path}: index range lies past the {indexBytes.Length} byte index buffer");
            }
            int firstFace = counts.Count;
            int faceCount = indexCount / cornerCount;
            int firstIndex = firstByte / indexSize;
            for (int face = 0; face < faceCount; face++)
            {
                counts.Add(cornerCount);
                for (int corner = 0; corner < cornerCount; corner++)
                {
                    int index = ReadIndex(indexBytes, indexSize, firstIndex + face * cornerCount + corner) + baseVertex;
                    if ((uint)index >= (uint)vertexCount)
                    {
                        throw new FormatException($"{path}: vertex index {index} is outside the {vertexCount} vertices");
                    }
                    corners.Add(index);
                }
            }
            subsets.Add([.. Enumerable.Range(firstFace, faceCount)]);
        }

        mesh.Add("FaceVertexCounts", EngineValues.Integers(counts));
        mesh.Add("FaceVertexIndices", EngineValues.Integers(corners));
        if (subsets.Count <= 1)
        {
            return;
        }
        ArrayValue array = new();
        foreach (int[] faces in subsets)
        {
            ObjectValue subset = new();
            subset.Add("Indices", EngineValues.Integers(faces));
            subset.Add("FamilyName", ScalarValue.FromText(MaterialBindFamily));
            array.Items.Add(subset);
        }
        mesh.Add("Subsets", array);
    }

    private static int ReadIndex(byte[] bytes, int size, int position)
    {
        return size == sizeof(ushort)
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position * size))
            : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position * size)));
    }

    private static void ReadColors(DecodeContext context, VertexBuffer vertices, ObjectValue mesh)
    {
        if (!vertices.Has(ColorChannel))
        {
            return;
        }
        int dimension = vertices.Dimension(ColorChannel);
        if (dimension < 3)
        {
            context.Unsupported($"vertex color with {dimension} components has no primvars:displayColor");
            return;
        }
        float[] floats = vertices.Floats(ColorChannel);
        ArrayValue color = new();
        ArrayValue opacity = new();
        for (int offset = 0; offset + dimension <= floats.Length; offset += dimension)
        {
            color.Items.Add(EngineValues.Vector3(floats[offset], floats[offset + 1], floats[offset + 2]));
            if (dimension > 3)
            {
                opacity.Items.Add(ScalarValue.FromSingle(floats[offset + 3]));
            }
        }
        mesh.Add("DisplayColor", EngineValues.Primvar(color, VertexInterpolation));
        if (dimension > 3)
        {
            mesh.Add("DisplayOpacity", EngineValues.Primvar(opacity, VertexInterpolation));
        }
    }

    private static void ReadTextureCoordinates(DecodeContext context, VertexBuffer vertices, ObjectValue mesh)
    {
        for (int set = 0; set < TextureChannelCount; set++)
        {
            int channel = FirstTextureChannel + set;
            if (!vertices.Has(channel))
            {
                continue;
            }
            int dimension = vertices.Dimension(channel);
            if (dimension != TextureCoordinateDimension)
            {
                context.Unsupported($"uv set {set} has {dimension} components, primvars:st is texCoord2f");
                continue;
            }
            float[] floats = vertices.Floats(channel);
            ArrayValue values = new();
            for (int offset = 0; offset + 1 < floats.Length; offset += TextureCoordinateDimension)
            {
                values.Items.Add(EngineValues.Vector2(floats[offset], floats[offset + 1]));
            }
            mesh.Add(set == 0 ? "St" : "St" + set, EngineValues.Primvar(values, VertexInterpolation));
        }
    }

    private static void ReadSkin(DecodeContext context, VertexBuffer vertices, ObjectValue mesh)
    {
        IReadOnlyList<UnityYamlNode> bindPoses = YamlNodes.Items(context.Take("m_BindPose"), "m_BindPose");
        uint[] hashes = YamlNodes.UnsignedIntegers(context.Take("m_BoneNameHashes"), "m_BoneNameHashes");
        long rootHash = context.Take("m_RootBoneNameHash") is { } root ? YamlNodes.Integer(root, "m_RootBoneNameHash") : 0;
        if (hashes.Length > 0)
        {
            ArrayValue array = new();
            foreach (uint hash in hashes)
            {
                array.Items.Add(ScalarValue.FromInteger(hash));
            }
            mesh.Add("BoneNameHashes", array);
        }
        if (rootHash != 0)
        {
            mesh.Add("RootBoneNameHash", ScalarValue.FromInteger(rootHash));
        }
        if (bindPoses.Count > 0)
        {
            ArrayValue transforms = new();
            for (int index = 0; index < bindPoses.Count; index++)
            {
                double[] bindPose = YamlNodes.Matrix(bindPoses[index], $"m_BindPose[{index}]");
                transforms.Items.Add(UsdMatrices.ToValue(UsdMatrices.ReflectX(UsdMatrices.Transpose(UsdMatrices.Invert(bindPose)))));
            }
            mesh.Add("BindTransforms", transforms);
        }
        if (!vertices.Has(BlendWeightChannel) || !vertices.Has(BlendIndicesChannel))
        {
            return;
        }
        int dimension = vertices.Dimension(BlendWeightChannel);
        if (dimension != vertices.Dimension(BlendIndicesChannel))
        {
            throw new FormatException("blend weight and blend index channels differ in dimension");
        }
        mesh.Add("JointWeights", EngineValues.Primvar(EngineValues.Singles(vertices.Floats(BlendWeightChannel)), VertexInterpolation, dimension));
        mesh.Add("JointIndices", EngineValues.Primvar(EngineValues.Integers(vertices.Integers(BlendIndicesChannel)), VertexInterpolation, dimension));
    }

    private static void ReadBlendShapes(DecodeContext context, ObjectValue mesh)
    {
        UnityYamlMapping data = YamlNodes.Mapping(context.Take("m_Shapes"), "m_Shapes");
        IReadOnlyList<UnityYamlNode> vertexNodes = YamlNodes.Items(YamlNodes.Optional(data, "vertices"), "m_Shapes.vertices");
        IReadOnlyList<UnityYamlNode> shapeNodes = YamlNodes.Items(YamlNodes.Optional(data, "shapes"), "m_Shapes.shapes");
        IReadOnlyList<UnityYamlNode> channelNodes = YamlNodes.Items(YamlNodes.Optional(data, "channels"), "m_Shapes.channels");
        float[] weights = YamlNodes.Singles(YamlNodes.Optional(data, "fullWeights"), "m_Shapes.fullWeights");
        if (channelNodes.Count == 0)
        {
            return;
        }
        ArrayValue shapes = new();
        for (int channelIndex = 0; channelIndex < channelNodes.Count; channelIndex++)
        {
            string channelPath = $"m_Shapes.channels[{channelIndex}]";
            UnityYamlMapping channel = YamlNodes.Mapping(channelNodes[channelIndex], channelPath);
            string name = YamlNodes.Text(YamlNodes.Field(channel, "name", channelPath), channelPath + ".name");
            int frameIndex = checked((int)YamlNodes.Integer(YamlNodes.Field(channel, "frameIndex", channelPath), channelPath + ".frameIndex"));
            int frameCount = checked((int)YamlNodes.Integer(YamlNodes.Field(channel, "frameCount", channelPath), channelPath + ".frameCount"));
            for (int frame = frameIndex; frame < frameIndex + frameCount; frame++)
            {
                shapes.Items.Add(ReadBlendShapeFrame(context, vertexNodes, shapeNodes, weights, name, frame));
            }
        }
        mesh.Add("BlendShapes", shapes);
    }

    private static ObjectValue ReadBlendShapeFrame(DecodeContext context, IReadOnlyList<UnityYamlNode> vertexNodes, IReadOnlyList<UnityYamlNode> shapeNodes, float[] weights, string name, int frame)
    {
        string path = $"m_Shapes.shapes[{frame}]";
        if ((uint)frame >= (uint)shapeNodes.Count || (uint)frame >= (uint)weights.Length)
        {
            throw new FormatException($"{path}: blend shape frame {frame} of {name} is outside the shape and weight tables");
        }
        UnityYamlMapping shape = YamlNodes.Mapping(shapeNodes[frame], path);
        int first = checked((int)YamlNodes.Integer(YamlNodes.Field(shape, "firstVertex", path), path + ".firstVertex"));
        int count = checked((int)YamlNodes.Integer(YamlNodes.Field(shape, "vertexCount", path), path + ".vertexCount"));
        bool hasNormals = YamlNodes.Boolean(YamlNodes.Field(shape, "hasNormals", path), path + ".hasNormals");
        if (YamlNodes.Boolean(YamlNodes.Field(shape, "hasTangents", path), path + ".hasTangents"))
        {
            context.Unsupported("blend shape tangent deltas have no usdSkel BlendShape attribute");
        }
        ArrayValue offsets = new();
        ArrayValue normalOffsets = new();
        ArrayValue pointIndices = new();
        for (int vertexIndex = first; vertexIndex < first + count; vertexIndex++)
        {
            string vertexPath = $"m_Shapes.vertices[{vertexIndex}]";
            if ((uint)vertexIndex >= (uint)vertexNodes.Count)
            {
                throw new FormatException($"{vertexPath}: outside the {vertexNodes.Count} blend shape vertices");
            }
            UnityYamlMapping vertex = YamlNodes.Mapping(vertexNodes[vertexIndex], vertexPath);
            pointIndices.Items.Add(ScalarValue.FromInteger(YamlNodes.Integer(YamlNodes.Field(vertex, "index", vertexPath), vertexPath + ".index")));
            (double X, double Y, double Z) offset = YamlNodes.Vector(YamlNodes.Field(vertex, "vertex", vertexPath), vertexPath + ".vertex");
            offsets.Items.Add(EngineValues.Vector3(-offset.X, offset.Y, offset.Z));
            if (hasNormals)
            {
                (double X, double Y, double Z) normal = YamlNodes.Vector(YamlNodes.Field(vertex, "normal", vertexPath), vertexPath + ".normal");
                normalOffsets.Items.Add(EngineValues.Vector3(-normal.X, normal.Y, normal.Z));
            }
        }

        ObjectValue result = new();
        result.Add("SourceName", ScalarValue.FromText(name));
        result.Add("FrameWeight", ScalarValue.FromSingle(weights[frame]));
        result.Add("Offsets", offsets);
        if (hasNormals)
        {
            result.Add("NormalOffsets", normalOffsets);
        }
        result.Add("PointIndices", pointIndices);
        return result;
    }
}
