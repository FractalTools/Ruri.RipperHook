using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.Matrix4x4f;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using System.Text.Json;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Serializes an exported Mesh's SERIALIZED fields -- the exact same data its YAML document
/// carries -- as one small JSON index plus one raw little-endian payload, consumed host-side as
/// zero-parse numpy views (ruri_pybridge mesh_decoder.decode_mesh_blob). The channel table, index
/// buffer, bind poses and blendshape deltas cross the bridge as the bytes they already are,
/// instead of round-tripping through megabytes of YAML hex text that CPython then re-parses.
/// Vertex bytes come from <see cref="MeshExtensions"/>.GetVertexDataBytes(), the same source the
/// YAML path inlines (YamlStreamedAssetExportCollection restores external StreamData into
/// VertexData before serializing), so both representations are byte-identical by construction.
///
/// Payload layout (all little-endian), section offsets carried by the JSON index:
///   vertexData · indexBuffer · bindPose (16 f32 row-major per matrix, e00..e33) ·
///   boneNameHashes (u32) · shapeVertices (u32 index + 3 f32 vertex delta + 3 f32 normal delta).
/// Returns null for a mesh whose geometry is NOT plainly in hand (compressed vertex data, a
/// channel-less legacy layout, or unresolvable stream data) -- the caller keeps the YAML document
/// as the only representation and the host-side diagnosis wording stays exactly as it was.
/// </summary>
internal static class MeshRawBlob
{
    private sealed record ChannelEntry(byte stream, byte offset, byte format, byte dimension);

    private sealed record SubMeshEntry(
        long firstByte, long indexCount, int topology, long baseVertex, long firstVertex, long vertexCount);

    private sealed record ShapeChannelEntry(string name, int frameIndex, int frameCount);

    private sealed record ShapeFrameEntry(long firstVertex, long vertexCount, bool hasNormals);

    private sealed record SectionEntry(long off, long len);

    private sealed record MeshIndex(
        string name, long vertexCount, List<ChannelEntry> channels, int indexSize,
        List<SubMeshEntry> subMeshes, List<float> fullWeights,
        List<ShapeChannelEntry> shapeChannels, List<ShapeFrameEntry> shapeFrames,
        long shapeVertexCount, Dictionary<string, SectionEntry> sections);

    public static (string MetaJson, byte[] Payload)? Build(IMesh mesh)
    {
        if (mesh.CompressedMesh.Vertices.NumItems > 0 || !mesh.VertexData.Has_Channels())
        {
            return null;
        }
        byte[] vertexBytes = mesh.GetVertexDataBytes();
        if (vertexBytes.Length == 0 && mesh.VertexData.VertexCount > 0)
        {
            return null;
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
        int boneHashCount = mesh.BoneNameHashes.Count;

        List<float> fullWeights = new();
        List<ShapeChannelEntry> shapeChannels = new();
        List<ShapeFrameEntry> shapeFrames = new();
        long shapeVertexCount = mesh.Shapes.Vertices.Count;
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
            shapeFrames.Add(new ShapeFrameEntry(frame.FirstVertex, frame.VertexCount, frame.HasNormals));
        }

        const int ShapeVertexStride = sizeof(uint) + 6 * sizeof(float);
        long bindPoseBytes = bindPoseCount * 16L * sizeof(float);
        long boneHashBytes = boneHashCount * (long)sizeof(uint);
        long shapeVertexBytes = shapeVertexCount * ShapeVertexStride;
        byte[] payload = new byte[vertexBytes.Length + indexBytes.Length + bindPoseBytes + boneHashBytes + shapeVertexBytes];

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
        foreach (uint hash in mesh.BoneNameHashes)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hashSpan.Slice(hashCursor, 4), hash);
            hashCursor += 4;
        }

        Section("shapeVertices", shapeVertexBytes);
        Span<byte> shapeSpan = payload.AsSpan((int)sections["shapeVertices"].off, (int)shapeVertexBytes);
        int shapeCursor = 0;
        foreach (var vertex in mesh.Shapes.Vertices)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(shapeSpan.Slice(shapeCursor, 4), vertex.Index);
            shapeCursor += 4;
            WriteVector3(vertex.Vertex.X, vertex.Vertex.Y, vertex.Vertex.Z, shapeSpan, ref shapeCursor);
            WriteVector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z, shapeSpan, ref shapeCursor);
        }

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
            sections);
        return (JsonSerializer.Serialize(meta), payload);
    }

    private static void WriteMatrixRowMajor(Matrix4x4f matrix, Span<byte> span, ref int cursor)
    {
        // Row-major e00..e33 -- the exact element order the YAML document spells the same matrix in.
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

    private static void WriteVector3(float x, float y, float z, Span<byte> span, ref int cursor)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor, 4), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor + 4, 4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(cursor + 8, 4), z);
        cursor += 12;
    }
}
