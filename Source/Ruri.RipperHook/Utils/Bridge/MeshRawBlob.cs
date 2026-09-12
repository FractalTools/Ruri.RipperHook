using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.Matrix4x4f;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using System.Numerics;
using System.Text.Json;

namespace Ruri.RipperHook.Bridge;

internal static class MeshRawBlob
{
    private sealed record ChannelEntry(byte stream, byte offset, byte format, byte dimension);

    private sealed record SubMeshEntry(
        long firstByte, long indexCount, int topology, long baseVertex, long firstVertex, long vertexCount);

    private sealed record ShapeChannelEntry(string name, int frameIndex, int frameCount);

    private sealed record ShapeFrameEntry(
        long firstVertex, long vertexCount, bool hasNormals, bool hasTangents);

    private sealed record SectionEntry(long off, long len);

    private sealed record MeshIndex(
        string name, long vertexCount, List<ChannelEntry> channels, int indexSize,
        List<SubMeshEntry> subMeshes, List<float> fullWeights,
        List<ShapeChannelEntry> shapeChannels, List<ShapeFrameEntry> shapeFrames,
        long shapeVertexCount, long variableBoneCountWeights,
        Dictionary<string, SectionEntry> sections);

    private sealed record RawGeometry(
        long vertexCount, List<ChannelEntry> channels, int indexSize, List<SubMeshEntry> subMeshes,
        byte[] vertexBytes, byte[] indexBytes, Matrix4x4[]? bindPose, BoneWeight4[]? skin);

    public static MeshRawBlobResult Build(IMesh mesh)
    {
        // Mesh Compression (Model Importer > Mesh Compression) bit-packs the geometry into
        // m_CompressedMesh and leaves m_VertexData empty. Skipping those used to be safe-looking
        // and was not: a skipped mesh reaches the host as ZERO VERTICES, which is exactly what a
        // collision proxy looks like, so a scene could lose most of its renderers while every
        // count still read as success (measured: 259 of 339 renderers in one VRChat world).
        // AssetRipper unpacks the format itself; this rebuilds plain vertex/index buffers out of
        // that, so the host keeps reading ONE geometry format and never learns this one exists.
        if (mesh.CompressedMesh.Vertices.NumItems > 0)
        {
            return Unpacked(mesh) is { } unpacked
                ? Assemble(mesh, unpacked)
                : MeshRawBlobResult.Skipped(
                    "compressed mesh whose bit-packed geometry AssetRipper could not unpack");
        }
        if (!mesh.VertexData.Has_Channels())
        {
            return MeshRawBlobResult.Skipped(
                "vertex data carries no channel table (pre-5.0 m_CurrentChannels layout)");
        }
        byte[] vertexBytes = mesh.GetVertexDataBytes();
        if (vertexBytes.Length == 0 && mesh.VertexData.VertexCount > 0)
        {
            return MeshRawBlobResult.Skipped(
                $"vertex buffer is empty for {mesh.VertexData.VertexCount} vertices -- the external stream resource did not resolve");
        }

        List<ChannelEntry> channels = new();
        foreach (IChannelInfo channel in mesh.VertexData.Channels)
        {
            channels.Add(new ChannelEntry(channel.Stream, channel.Offset, channel.Format, channel.Dimension));
        }

        List<SubMeshEntry> subMeshes = new();
        foreach (ISubMesh subMesh in mesh.SubMeshes)
        {
            int topology = subMesh.Has_Topology() ? (int)subMesh.TopologyE : 0;
            subMeshes.Add(new SubMeshEntry(subMesh.FirstByte, subMesh.IndexCount, topology,
                subMesh.BaseVertex, subMesh.FirstVertex, subMesh.VertexCount));
        }

        return Assemble(mesh, new RawGeometry(mesh.VertexData.VertexCount, channels,
            mesh.Is16BitIndices() ? 2 : 4, subMeshes, vertexBytes, mesh.IndexBuffer, null, null));
    }

    /// <summary>
    /// A compressed mesh as plain buffers, unpacked by AssetRipper's own reader
    /// (<see cref="MeshData.TryMakeFromMesh"/>) rather than by a second implementation of the
    /// bit packing. The channel table is synthesised in the MODERN 14-slot VertexAttribute
    /// order, every attribute float32 in one stream -- the shape the host's decoder already
    /// reads, so nothing on that side has to know a mesh was ever compressed. Skin stays out
    /// of the channels and goes to the blob's own skin section, which is where the host looks
    /// when no blend channels are declared.
    /// </summary>
    private static RawGeometry? Unpacked(IMesh mesh)
    {
        if (!MeshData.TryMakeFromMesh(mesh, out MeshData data) || data.Vertices.Length == 0)
        {
            return null;
        }

        int count = data.Vertices.Length;
        Vector2[]?[] uvs = [data.UV0, data.UV1, data.UV2, data.UV3, data.UV4, data.UV5, data.UV6, data.UV7];

        // One entry per semantic, in semantic order, dimension 0 for what this mesh does not
        // carry -- a 14-channel table is what tells the host to read the modern order at all.
        List<ChannelEntry> channels = new(14);
        int stride = 0;
        void Channel(bool present, int dimension)
        {
            channels.Add(new ChannelEntry(0, (byte)stride, 0, present ? (byte)dimension : (byte)0));
            stride += present ? dimension * sizeof(float) : 0;
        }
        Channel(true, 3);                       // Position
        Channel(data.HasNormals, 3);            // Normal
        Channel(data.HasTangents, 4);           // Tangent
        Channel(data.HasColors, 4);             // Color
        foreach (Vector2[]? uv in uvs)
        {
            Channel(uv is not null && uv.Length == count, 2);
        }
        Channel(false, 4);                      // BlendWeight  -- carried in the skin section
        Channel(false, 4);                      // BlendIndices --      "

        byte[] vertexBytes = new byte[(long)count * stride <= int.MaxValue ? count * stride : 0];
        if (vertexBytes.Length == 0 && count > 0)
        {
            return null;
        }
        Span<byte> vertexSpan = vertexBytes;
        for (int vertex = 0; vertex < count; vertex++)
        {
            int cursor = vertex * stride;
            Vector3 position = data.Vertices[vertex];
            WriteFloats(vertexSpan, ref cursor, position.X, position.Y, position.Z);
            if (data.HasNormals)
            {
                Vector3 normal = data.Normals[vertex];
                WriteFloats(vertexSpan, ref cursor, normal.X, normal.Y, normal.Z);
            }
            if (data.HasTangents)
            {
                Vector4 tangent = data.Tangents[vertex];
                WriteFloats(vertexSpan, ref cursor, tangent.X, tangent.Y, tangent.Z, tangent.W);
            }
            if (data.HasColors)
            {
                ColorFloat color = data.Colors[vertex];
                WriteFloats(vertexSpan, ref cursor, color.R, color.G, color.B, color.A);
            }
            foreach (Vector2[]? uv in uvs)
            {
                if (uv is not null && uv.Length == count)
                {
                    WriteFloats(vertexSpan, ref cursor, uv[vertex].X, uv[vertex].Y);
                }
            }
        }

        // Unpacked indices are 32-bit whatever the file's own index format said, and a submesh
        // window travels in BYTES of the buffer it indexes -- so it is restated against this one.
        const int IndexSize = sizeof(uint);
        byte[] indexBytes = new byte[data.ProcessedIndexBuffer.Length * IndexSize];
        Span<byte> indexSpan = indexBytes;
        for (int index = 0; index < data.ProcessedIndexBuffer.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                indexSpan.Slice(index * IndexSize, IndexSize), data.ProcessedIndexBuffer[index]);
        }

        List<SubMeshEntry> subMeshes = new(data.SubMeshes.Length);
        foreach (SubMeshData subMesh in data.SubMeshes)
        {
            subMeshes.Add(new SubMeshEntry((long)subMesh.FirstIndex * IndexSize, subMesh.IndexCount,
                (int)subMesh.Topology, subMesh.BaseVertex, subMesh.FirstVertex, subMesh.VertexCount));
        }

        return new RawGeometry(count, channels, IndexSize, subMeshes, vertexBytes, indexBytes,
            data.BindPose, data.Skin);
    }

    private static MeshRawBlobResult Assemble(IMesh mesh, RawGeometry geometry)
    {
        byte[] vertexBytes = geometry.vertexBytes;
        byte[] indexBytes = geometry.indexBytes;
        int bindPoseCount = geometry.bindPose?.Length ?? mesh.BindPose.Count;
        int boneHashCount = mesh.Has_BoneNameHashes() ? mesh.BoneNameHashes.Count : 0;

        List<float> fullWeights = new();
        List<ShapeChannelEntry> shapeChannels = new();
        List<ShapeFrameEntry> shapeFrames = new();
        long shapeVertexCount = 0;
        if (mesh.Has_Shapes())
        {
            shapeVertexCount = mesh.Shapes.Vertices.Count;
            foreach (float weight in mesh.Shapes.FullWeights)
            {
                fullWeights.Add(weight);
            }
            foreach (var channel in mesh.Shapes.Channels)
            {
                shapeChannels.Add(new ShapeChannelEntry(channel.Name_R.String, channel.FrameIndex, channel.FrameCount));
            }
            foreach (var frame in mesh.Shapes.Shapes)
            {
                shapeFrames.Add(new ShapeFrameEntry(frame.FirstVertex, frame.VertexCount,
                    frame.HasNormals, frame.HasTangents));
            }
        }

        // A blend shape vertex IS index + vertex + normal + tangent; packing only the
        // first two deltas silently dropped a third of every entry.
        const int ShapeVertexStride = sizeof(uint) + 9 * sizeof(float);
        const int SkinStride = 4 * sizeof(float) + 4 * sizeof(int);
        long bindPoseBytes = bindPoseCount * 16L * sizeof(float);
        long boneHashBytes = boneHashCount * (long)sizeof(uint);
        long shapeVertexBytes = shapeVertexCount * ShapeVertexStride;
        long skinCount = geometry.skin?.Length ?? (mesh.Has_Skin() ? mesh.Skin.Count : 0);
        long skinBytes = skinCount * SkinStride;
        byte[] payload = new byte[vertexBytes.Length + indexBytes.Length + bindPoseBytes
            + boneHashBytes + shapeVertexBytes + skinBytes];

        long cursor = 0;
        Dictionary<string, SectionEntry> sections = new();

        void Section(string key, long length)
        {
            sections[key] = new SectionEntry(cursor, length);
            cursor += length;
        }

        Section("vertexData", vertexBytes.Length);
        Buffer.BlockCopy(vertexBytes, 0, payload, (int)sections["vertexData"].off, vertexBytes.Length);
        Section("indexBuffer", indexBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, payload, (int)sections["indexBuffer"].off, indexBytes.Length);

        Section("bindPose", bindPoseBytes);
        Span<byte> bindSpan = payload.AsSpan((int)sections["bindPose"].off, (int)bindPoseBytes);
        int bindCursor = 0;
        if (geometry.bindPose is { } unpackedBindPose)
        {
            foreach (Matrix4x4 matrix in unpackedBindPose)
            {
                WriteFloats(bindSpan, ref bindCursor,
                    matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                    matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                    matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                    matrix.M41, matrix.M42, matrix.M43, matrix.M44);
            }
        }
        else
        {
            foreach (Matrix4x4f matrix in mesh.BindPose)
            {
                WriteMatrixRowMajor(matrix, bindSpan, ref bindCursor);
            }
        }

        Section("boneNameHashes", boneHashBytes);
        Span<byte> hashSpan = payload.AsSpan((int)sections["boneNameHashes"].off, (int)boneHashBytes);
        int hashCursor = 0;
        if (boneHashCount > 0)
        {
            foreach (uint hash in mesh.BoneNameHashes)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hashSpan.Slice(hashCursor, 4), hash);
                hashCursor += 4;
            }
        }

        Section("shapeVertices", shapeVertexBytes);
        Span<byte> shapeSpan = payload.AsSpan((int)sections["shapeVertices"].off, (int)shapeVertexBytes);
        int shapeCursor = 0;
        if (shapeVertexCount > 0)
        {
            foreach (var vertex in mesh.Shapes.Vertices)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(shapeSpan.Slice(shapeCursor, 4), vertex.Index);
                shapeCursor += 4;
                WriteVector3(vertex.Vertex.X, vertex.Vertex.Y, vertex.Vertex.Z, shapeSpan, ref shapeCursor);
                WriteVector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z, shapeSpan, ref shapeCursor);
                WriteVector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z, shapeSpan, ref shapeCursor);
            }
        }

        Section("skin", skinBytes);
        Span<byte> skinSpan = payload.AsSpan((int)sections["skin"].off, (int)skinBytes);
        int skinCursor = 0;
        if (geometry.skin is { } unpackedSkin)
        {
            foreach (BoneWeight4 weights in unpackedSkin)
            {
                WriteFloats(skinSpan, ref skinCursor,
                    weights.Weight0, weights.Weight1, weights.Weight2, weights.Weight3);
                WriteInts(skinSpan, ref skinCursor,
                    weights.Index0, weights.Index1, weights.Index2, weights.Index3);
            }
        }
        else if (skinCount > 0)
        {
            foreach (var weights in mesh.Skin)
            {
                WriteFloats(skinSpan, ref skinCursor,
                    weights.Weight_0_, weights.Weight_1_, weights.Weight_2_, weights.Weight_3_);
                WriteInts(skinSpan, ref skinCursor,
                    weights.BoneIndex_0_, weights.BoneIndex_1_, weights.BoneIndex_2_, weights.BoneIndex_3_);
            }
        }

        // Influences past the fixed four live here in a packing no reader in this
        // tree decodes. Carrying the count across is what lets the host say so
        // instead of quietly presenting a four-influence skin as the whole truth.
        long variableBoneCountWeights = mesh.Has_VariableBoneCountWeights()
            ? mesh.VariableBoneCountWeights.Data.Count
            : 0;

        MeshIndex meta = new(
            mesh.Name.String,
            geometry.vertexCount,
            geometry.channels,
            geometry.indexSize,
            geometry.subMeshes,
            fullWeights,
            shapeChannels,
            shapeFrames,
            shapeVertexCount,
            variableBoneCountWeights,
            sections);
        return MeshRawBlobResult.Built(JsonSerializer.Serialize(meta), payload);
    }

    private static void WriteMatrixRowMajor(Matrix4x4f matrix, Span<byte> span, ref int cursor)
    {
        Span<float> values =
        [
            matrix.E00, matrix.E01, matrix.E02, matrix.E03,
            matrix.E10, matrix.E11, matrix.E12, matrix.E13,
            matrix.E20, matrix.E21, matrix.E22, matrix.E23,
            matrix.E30, matrix.E31, matrix.E32, matrix.E33,
        ];
        foreach (float value in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor, 4), value);
            cursor += 4;
        }
    }

    private static void WriteFloats(Span<byte> span, ref int cursor, params ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor, 4), value);
            cursor += 4;
        }
    }

    private static void WriteInts(Span<byte> span, ref int cursor, params ReadOnlySpan<int> values)
    {
        foreach (int value in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor, 4), value);
            cursor += 4;
        }
    }

    private static void WriteVector3(float x, float y, float z, Span<byte> span, ref int cursor)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor, 4), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor + 4, 4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor + 8, 4), z);
        cursor += 12;
    }
}
