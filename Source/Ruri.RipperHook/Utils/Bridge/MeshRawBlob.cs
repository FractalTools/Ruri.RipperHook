using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.Matrix4x4f;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
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

    public static MeshRawBlobResult Build(IMesh mesh)
    {
        if (mesh.CompressedMesh.Vertices.NumItems > 0)
        {
            return MeshRawBlobResult.Skipped(
                "compressed mesh -- the geometry lives bit-packed in m_CompressedMesh, which no decode path reads");
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

        byte[] indexBytes = mesh.IndexBuffer;
        int bindPoseCount = mesh.BindPose.Count;
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
        long skinCount = mesh.Has_Skin() ? mesh.Skin.Count : 0;
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
        foreach (Matrix4x4f matrix in mesh.BindPose)
        {
            WriteMatrixRowMajor(matrix, bindSpan, ref bindCursor);
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
        if (skinCount > 0)
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
            mesh.VertexData.VertexCount,
            channels,
            mesh.Is16BitIndices() ? 2 : 4,
            subMeshes,
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
