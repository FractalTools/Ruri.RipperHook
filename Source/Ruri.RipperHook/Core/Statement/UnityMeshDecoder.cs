using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.Matrix4x4f;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using System.Buffers.Binary;
using System.Numerics;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// A Unity serialized Mesh as plain buffers. The vertex attributes are interleaved across up
/// to four streams; each of the fixed channels describes one VertexAttribute by stream, byte
/// offset, format and dimension. Unity 2017 and older serialize exactly eight channels in an
/// older order, told apart by the channel COUNT the file declares. A packed normal (one
/// component where three are needed) is offered in every known bit layout and the layout that
/// agrees with the geometry is kept; nothing else is guessed at.
/// </summary>
public static class UnityMeshDecoder
{
    private const int Position = 0;
    private const int Normal = 1;
    private const int Tangent = 2;
    private const int Color = 3;
    private const int Uv0 = 4;
    private const int Uv7 = 11;
    private const int BlendWeight = 12;
    private const int BlendIndices = 13;
    private const int ModernChannelCount = 14;
    private const int LegacyChannelCount = 8;

    private static readonly int[] LegacySlots = BuildLegacySlots();

    private readonly record struct Channel(int Stream, int Offset, int Format, int Dimension);

    private readonly record struct Format(int Size, bool Normalised, bool Integer, bool Signed);

    private static int[] BuildLegacySlots()
    {
        int[] slots = new int[ModernChannelCount];
        Array.Fill(slots, -1);
        slots[Position] = 0;
        slots[Normal] = 1;
        slots[Color] = 2;
        slots[Uv0] = 3;
        slots[Uv0 + 1] = 4;
        slots[Uv0 + 2] = 5;
        slots[Uv0 + 3] = 6;
        slots[Tangent] = 7;
        return slots;
    }

    private static Format FormatOf(int format) => format switch
    {
        0 => new Format(4, false, false, false),
        1 => new Format(2, false, false, false),
        2 => new Format(1, true, false, false),
        3 => new Format(1, true, false, true),
        4 => new Format(2, true, false, false),
        5 => new Format(2, true, false, true),
        6 => new Format(1, false, true, false),
        7 => new Format(1, false, true, true),
        8 => new Format(2, false, true, false),
        9 => new Format(2, false, true, true),
        10 => new Format(4, false, true, false),
        11 => new Format(4, false, true, true),
        _ => new Format(4, false, false, false),
    };

    private static int RealDimension(int dimension) => dimension > 15 ? dimension & 0x0F : dimension;

    public static DecodedMesh Decode(IMesh mesh)
    {
        DecodedMesh decoded = new(mesh.Name.String);
        if (mesh.CompressedMesh.Vertices.NumItems > 0)
        {
            if (!MeshData.TryMakeFromMesh(mesh, out MeshData unpacked) || unpacked.Vertices.Length == 0)
            {
                throw new InvalidDataException(
                    $"'{decoded.Name}' is a compressed mesh whose bit-packed geometry could not be unpacked.");
            }
            DecodeUnpacked(mesh, unpacked, decoded);
            return decoded;
        }

        int count = (int)mesh.VertexData.VertexCount;
        decoded.VertexCount = count;
        List<float[]> packedCandidates = [];
        if (mesh.VertexData.Has_Channels())
        {
            byte[] vertexBytes = mesh.GetVertexDataBytes();
            List<Channel> channels = new(mesh.VertexData.Channels.Count);
            foreach (IChannelInfo channel in mesh.VertexData.Channels)
            {
                channels.Add(new Channel(channel.Stream, channel.Offset, channel.Format, channel.Dimension));
            }
            if (count > 0 && vertexBytes.Length > 0)
            {
                packedCandidates = DecodeVertexChannels(decoded, vertexBytes, channels, count);
            }
        }

        int indexSize = mesh.Is16BitIndices() ? 2 : 4;
        long[] indices = ReadIndices(mesh.IndexBuffer, indexSize);
        List<DecodedSubMesh> subMeshes = new(mesh.SubMeshes.Count);
        foreach (ISubMesh subMesh in mesh.SubMeshes)
        {
            subMeshes.Add(new DecodedSubMesh(subMesh.FirstByte / indexSize, subMesh.IndexCount,
                subMesh.Has_Topology() ? (int)subMesh.TopologyE : 0, subMesh.BaseVertex, subMesh.FirstVertex,
                subMesh.VertexCount));
        }
        BuildTriangles(decoded, indices, subMeshes);
        ResolvePackedNormals(decoded, packedCandidates);

        if (mesh.BindPose.Count > 0)
        {
            float[] bind = new float[mesh.BindPose.Count * 16];
            int cursor = 0;
            foreach (Matrix4x4f matrix in mesh.BindPose)
            {
                bind[cursor++] = matrix.E00; bind[cursor++] = matrix.E01; bind[cursor++] = matrix.E02; bind[cursor++] = matrix.E03;
                bind[cursor++] = matrix.E10; bind[cursor++] = matrix.E11; bind[cursor++] = matrix.E12; bind[cursor++] = matrix.E13;
                bind[cursor++] = matrix.E20; bind[cursor++] = matrix.E21; bind[cursor++] = matrix.E22; bind[cursor++] = matrix.E23;
                bind[cursor++] = matrix.E30; bind[cursor++] = matrix.E31; bind[cursor++] = matrix.E32; bind[cursor++] = matrix.E33;
            }
            decoded.BindPoses = bind;
        }

        decoded.BoneNameHashes = ReadBoneNameHashes(mesh);
        if (decoded.BoneIndices is null && mesh.Has_Skin() && mesh.Skin.Count > 0)
        {
            ApplyLegacySkin(decoded, mesh);
        }
        decoded.VariableBoneCountWeights = mesh.Has_VariableBoneCountWeights()
            ? mesh.VariableBoneCountWeights.Data.Count
            : 0;
        DecodeBlendShapes(mesh, decoded);
        return decoded;
    }

    private static uint[]? ReadBoneNameHashes(IMesh mesh)
    {
        if (!mesh.Has_BoneNameHashes() || mesh.BoneNameHashes.Count == 0)
        {
            return null;
        }
        uint[] hashes = new uint[mesh.BoneNameHashes.Count];
        for (int index = 0; index < hashes.Length; index++)
        {
            hashes[index] = mesh.BoneNameHashes[index];
        }
        return hashes;
    }

    private static void ApplyLegacySkin(DecodedMesh decoded, IMesh mesh)
    {
        int count = mesh.Skin.Count;
        float[] weights = new float[count * 4];
        int[] indices = new int[count * 4];
        for (int vertex = 0; vertex < count; vertex++)
        {
            var entry = mesh.Skin[vertex];
            weights[vertex * 4] = entry.Weight_0_;
            weights[vertex * 4 + 1] = entry.Weight_1_;
            weights[vertex * 4 + 2] = entry.Weight_2_;
            weights[vertex * 4 + 3] = entry.Weight_3_;
            indices[vertex * 4] = entry.BoneIndex_0_;
            indices[vertex * 4 + 1] = entry.BoneIndex_1_;
            indices[vertex * 4 + 2] = entry.BoneIndex_2_;
            indices[vertex * 4 + 3] = entry.BoneIndex_3_;
        }
        decoded.InfluenceCount = 4;
        decoded.BoneWeights = weights;
        decoded.BoneIndices = indices;
    }

    private static void DecodeUnpacked(IMesh mesh, MeshData data, DecodedMesh decoded)
    {
        int count = data.Vertices.Length;
        decoded.VertexCount = count;
        float[] positions = new float[count * 3];
        for (int index = 0; index < count; index++)
        {
            Vector3 vertex = data.Vertices[index];
            positions[index * 3] = vertex.X;
            positions[index * 3 + 1] = vertex.Y;
            positions[index * 3 + 2] = vertex.Z;
        }
        decoded.Positions = positions;
        if (data.HasNormals)
        {
            float[] normals = new float[count * 3];
            for (int index = 0; index < count; index++)
            {
                Vector3 normal = data.Normals[index];
                normals[index * 3] = normal.X;
                normals[index * 3 + 1] = normal.Y;
                normals[index * 3 + 2] = normal.Z;
            }
            if (PredominantlyUnit(normals, 3))
            {
                decoded.Normals = NormalisedCopy(normals);
            }
        }
        if (data.HasTangents)
        {
            float[] tangents = new float[count * 4];
            for (int index = 0; index < count; index++)
            {
                Vector4 tangent = data.Tangents[index];
                tangents[index * 4] = tangent.X;
                tangents[index * 4 + 1] = tangent.Y;
                tangents[index * 4 + 2] = tangent.Z;
                tangents[index * 4 + 3] = tangent.W;
            }
            if (PredominantlyUnit(tangents, 4))
            {
                decoded.Tangents = tangents;
            }
        }
        if (data.HasColors)
        {
            float[] colors = new float[count * 4];
            for (int index = 0; index < count; index++)
            {
                Vector4 color = data.Colors[index].Vector;
                colors[index * 4] = color.X;
                colors[index * 4 + 1] = color.Y;
                colors[index * 4 + 2] = color.Z;
                colors[index * 4 + 3] = color.W;
            }
            decoded.Colors = colors;
        }
        Vector2[]?[] uvs = [data.UV0, data.UV1, data.UV2, data.UV3, data.UV4, data.UV5, data.UV6, data.UV7];
        for (int layer = 0; layer < uvs.Length; layer++)
        {
            Vector2[]? uv = uvs[layer];
            if (uv is null || uv.Length != count)
            {
                continue;
            }
            float[] flat = new float[count * 2];
            for (int index = 0; index < count; index++)
            {
                flat[index * 2] = uv[index].X;
                flat[index * 2 + 1] = uv[index].Y;
            }
            decoded.Uvs[layer] = flat;
        }

        long[] indices = new long[data.ProcessedIndexBuffer.Length];
        for (int index = 0; index < indices.Length; index++)
        {
            indices[index] = data.ProcessedIndexBuffer[index];
        }
        List<DecodedSubMesh> subMeshes = new(data.SubMeshes.Length);
        foreach (SubMeshData subMesh in data.SubMeshes)
        {
            subMeshes.Add(new DecodedSubMesh(subMesh.FirstIndex, subMesh.IndexCount, (int)subMesh.Topology,
                subMesh.BaseVertex, subMesh.FirstVertex, subMesh.VertexCount));
        }
        BuildTriangles(decoded, indices, subMeshes);

        if (data.BindPose is { Length: > 0 } bindPose)
        {
            float[] bind = new float[bindPose.Length * 16];
            int cursor = 0;
            foreach (Matrix4x4 matrix in bindPose)
            {
                bind[cursor++] = matrix.M11; bind[cursor++] = matrix.M12; bind[cursor++] = matrix.M13; bind[cursor++] = matrix.M14;
                bind[cursor++] = matrix.M21; bind[cursor++] = matrix.M22; bind[cursor++] = matrix.M23; bind[cursor++] = matrix.M24;
                bind[cursor++] = matrix.M31; bind[cursor++] = matrix.M32; bind[cursor++] = matrix.M33; bind[cursor++] = matrix.M34;
                bind[cursor++] = matrix.M41; bind[cursor++] = matrix.M42; bind[cursor++] = matrix.M43; bind[cursor++] = matrix.M44;
            }
            decoded.BindPoses = bind;
        }
        else if (mesh.BindPose.Count > 0)
        {
            float[] bind = new float[mesh.BindPose.Count * 16];
            int cursor = 0;
            foreach (Matrix4x4f matrix in mesh.BindPose)
            {
                bind[cursor++] = matrix.E00; bind[cursor++] = matrix.E01; bind[cursor++] = matrix.E02; bind[cursor++] = matrix.E03;
                bind[cursor++] = matrix.E10; bind[cursor++] = matrix.E11; bind[cursor++] = matrix.E12; bind[cursor++] = matrix.E13;
                bind[cursor++] = matrix.E20; bind[cursor++] = matrix.E21; bind[cursor++] = matrix.E22; bind[cursor++] = matrix.E23;
                bind[cursor++] = matrix.E30; bind[cursor++] = matrix.E31; bind[cursor++] = matrix.E32; bind[cursor++] = matrix.E33;
            }
            decoded.BindPoses = bind;
        }
        decoded.BoneNameHashes = ReadBoneNameHashes(mesh);

        if (data.Skin is { Length: > 0 } skin)
        {
            float[] weights = new float[skin.Length * 4];
            int[] boneIndices = new int[skin.Length * 4];
            for (int vertex = 0; vertex < skin.Length; vertex++)
            {
                BoneWeight4 entry = skin[vertex];
                weights[vertex * 4] = entry.Weight0;
                weights[vertex * 4 + 1] = entry.Weight1;
                weights[vertex * 4 + 2] = entry.Weight2;
                weights[vertex * 4 + 3] = entry.Weight3;
                boneIndices[vertex * 4] = entry.Index0;
                boneIndices[vertex * 4 + 1] = entry.Index1;
                boneIndices[vertex * 4 + 2] = entry.Index2;
                boneIndices[vertex * 4 + 3] = entry.Index3;
            }
            decoded.InfluenceCount = 4;
            decoded.BoneWeights = weights;
            decoded.BoneIndices = boneIndices;
        }
        else if (mesh.Has_Skin() && mesh.Skin.Count > 0)
        {
            ApplyLegacySkin(decoded, mesh);
        }
        decoded.VariableBoneCountWeights = mesh.Has_VariableBoneCountWeights()
            ? mesh.VariableBoneCountWeights.Data.Count
            : 0;
        DecodeBlendShapes(mesh, decoded);
    }

    private static long[] ReadIndices(byte[] indexBuffer, int indexSize)
    {
        int count = indexBuffer.Length / indexSize;
        long[] indices = new long[count];
        ReadOnlySpan<byte> bytes = indexBuffer;
        for (int index = 0; index < count; index++)
        {
            indices[index] = indexSize == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4));
        }
        return indices;
    }

    private static List<float[]> DecodeVertexChannels(DecodedMesh decoded, byte[] blob, List<Channel> channels, int count)
    {
        SortedDictionary<int, int> streamStrides = [];
        foreach (Channel channel in channels)
        {
            int dimension = RealDimension(channel.Dimension);
            if (dimension == 0)
            {
                continue;
            }
            int end = channel.Offset + dimension * FormatOf(channel.Format).Size;
            streamStrides[channel.Stream] = Math.Max(streamStrides.GetValueOrDefault(channel.Stream), end);
        }
        Dictionary<int, long> streamOffsets = [];
        long running = 0;
        foreach ((int stream, int stride) in streamStrides)
        {
            running = (running + 15) & ~15L;
            streamOffsets[stream] = running;
            running += (long)stride * count;
        }

        int[] slots = channels.Count <= LegacyChannelCount ? LegacySlots : null!;
        Channel? ChannelOf(int semantic)
        {
            int index = slots is null ? semantic : slots[semantic];
            return index >= 0 && index < channels.Count ? channels[index] : null;
        }
        int DimensionOf(Channel? channel) => channel is null ? 0 : RealDimension(channel.Value.Dimension);

        List<float[]> packedCandidates = [];

        Channel? positionChannel = ChannelOf(Position);
        if (DimensionOf(positionChannel) > 0)
        {
            decoded.Positions = FirstComponents(DecodeChannel(blob, streamOffsets, streamStrides, positionChannel!.Value, count, out _), DimensionOf(positionChannel), 3, count);
        }

        Channel? normalChannel = ChannelOf(Normal);
        if (DimensionOf(normalChannel) > 0)
        {
            int dimension = DimensionOf(normalChannel);
            float[] read = DecodeChannel(blob, streamOffsets, streamStrides, normalChannel!.Value, count, out int[]? integers);
            List<float[]> candidates = [];
            if (dimension >= 3)
            {
                candidates.Add(FirstComponents(read, dimension, 3, count));
            }
            if (dimension == 1)
            {
                uint[] words = new uint[count];
                bool integerFormat = FormatOf(normalChannel.Value.Format).Integer;
                for (int index = 0; index < count; index++)
                {
                    words[index] = integerFormat ? unchecked((uint)integers![index]) : BitConverter.SingleToUInt32Bits(read[index]);
                }
                candidates.AddRange(UnpackNormal101010(words));
            }
            foreach (float[] candidate in candidates)
            {
                if (PredominantlyUnit(candidate, 3))
                {
                    decoded.Normals = NormalisedCopy(candidate);
                    break;
                }
            }
            if (decoded.Normals is null)
            {
                packedCandidates.AddRange(candidates);
            }
        }

        Channel? tangentChannel = ChannelOf(Tangent);
        int tangentDimension = DimensionOf(tangentChannel);
        if (tangentDimension >= 3)
        {
            float[] read = DecodeChannel(blob, streamOffsets, streamStrides, tangentChannel!.Value, count, out _);
            if (PredominantlyUnit(read, tangentDimension))
            {
                decoded.Tangents = tangentDimension == 4 ? read : PadComponents(read, tangentDimension, 4, count, 1f);
            }
        }

        Channel? colorChannel = ChannelOf(Color);
        int colorDimension = DimensionOf(colorChannel);
        if (colorDimension > 0)
        {
            float[] read = DecodeChannel(blob, streamOffsets, streamStrides, colorChannel!.Value, count, out _);
            decoded.Colors = colorDimension == 4 ? read : colorDimension > 4
                ? FirstComponents(read, colorDimension, 4, count)
                : PadComponents(read, colorDimension, 4, count, 1f);
        }

        for (int semantic = Uv0; semantic <= Uv7; semantic++)
        {
            Channel? uvChannel = ChannelOf(semantic);
            int dimension = DimensionOf(uvChannel);
            if (dimension >= 2)
            {
                float[] read = DecodeChannel(blob, streamOffsets, streamStrides, uvChannel!.Value, count, out _);
                decoded.Uvs[semantic - Uv0] = FirstComponents(read, dimension, 2, count);
            }
        }

        Channel? weightChannel = ChannelOf(BlendWeight);
        Channel? indexChannel = ChannelOf(BlendIndices);
        int weightDimension = DimensionOf(weightChannel);
        int indexDimension = DimensionOf(indexChannel);
        if (indexDimension > 0)
        {
            DecodeChannel(blob, streamOffsets, streamStrides, indexChannel!.Value, count, out int[]? integers);
            int[] boneIndices = integers ?? throw new InvalidDataException(
                $"'{decoded.Name}' declares blend indices in a non-integer format {indexChannel.Value.Format}.");
            decoded.InfluenceCount = indexDimension;
            decoded.BoneIndices = boneIndices;
            if (weightDimension > 0)
            {
                float[] weights = DecodeChannel(blob, streamOffsets, streamStrides, weightChannel!.Value, count, out _);
                decoded.BoneWeights = weightDimension == indexDimension
                    ? weights
                    : weightDimension > indexDimension
                        ? FirstComponents(weights, weightDimension, indexDimension, count)
                        : PadComponents(weights, weightDimension, indexDimension, count, 0f);
            }
            else
            {
                float[] weights = new float[count * indexDimension];
                for (int vertex = 0; vertex < count; vertex++)
                {
                    weights[vertex * indexDimension] = 1f;
                }
                decoded.BoneWeights = weights;
            }
        }
        return packedCandidates;
    }

    private static float[] DecodeChannel(byte[] blob, Dictionary<int, long> streamOffsets,
        SortedDictionary<int, int> streamStrides, Channel channel, int count, out int[]? integers)
    {
        int dimension = RealDimension(channel.Dimension);
        Format format = FormatOf(channel.Format);
        long stride = streamStrides[channel.Stream];
        long streamOffset = streamOffsets[channel.Stream];
        long last = streamOffset + (long)(count - 1) * stride + channel.Offset + (long)dimension * format.Size;
        if (last > blob.Length)
        {
            throw new InvalidDataException(
                $"vertex buffer of {blob.Length} byte(s) is too short for a channel reaching byte {last}.");
        }
        float[] values = new float[count * dimension];
        integers = format.Integer ? new int[count * dimension] : null;
        ReadOnlySpan<byte> bytes = blob;
        float maximum = format.Size switch { 1 => format.Signed ? 127f : 255f, 2 => format.Signed ? 32767f : 65535f, _ => format.Signed ? int.MaxValue : uint.MaxValue };
        for (int vertex = 0; vertex < count; vertex++)
        {
            long record = streamOffset + vertex * stride + channel.Offset;
            for (int component = 0; component < dimension; component++)
            {
                ReadOnlySpan<byte> slice = bytes.Slice((int)(record + component * format.Size), format.Size);
                int target = vertex * dimension + component;
                if (format.Integer)
                {
                    int value = format.Size switch
                    {
                        1 => format.Signed ? (sbyte)slice[0] : slice[0],
                        2 => format.Signed ? BinaryPrimitives.ReadInt16LittleEndian(slice) : BinaryPrimitives.ReadUInt16LittleEndian(slice),
                        _ => format.Signed ? BinaryPrimitives.ReadInt32LittleEndian(slice) : unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(slice)),
                    };
                    integers![target] = value;
                    values[target] = value;
                    continue;
                }
                if (!format.Normalised)
                {
                    values[target] = format.Size == 2
                        ? (float)BinaryPrimitives.ReadHalfLittleEndian(slice)
                        : BinaryPrimitives.ReadSingleLittleEndian(slice);
                    continue;
                }
                float raw = format.Size switch
                {
                    1 => format.Signed ? (sbyte)slice[0] : slice[0],
                    _ => format.Signed ? BinaryPrimitives.ReadInt16LittleEndian(slice) : BinaryPrimitives.ReadUInt16LittleEndian(slice),
                };
                values[target] = format.Signed ? MathF.Max(raw / maximum, -1f) : raw / maximum;
            }
        }
        return values;
    }

    private static float[] FirstComponents(float[] values, int dimension, int wanted, int count)
    {
        if (dimension == wanted)
        {
            return values;
        }
        float[] result = new float[count * wanted];
        for (int vertex = 0; vertex < count; vertex++)
        {
            Array.Copy(values, vertex * dimension, result, vertex * wanted, wanted);
        }
        return result;
    }

    private static float[] PadComponents(float[] values, int dimension, int wanted, int count, float fill)
    {
        float[] result = new float[count * wanted];
        for (int vertex = 0; vertex < count; vertex++)
        {
            Array.Copy(values, vertex * dimension, result, vertex * wanted, dimension);
            for (int component = dimension; component < wanted; component++)
            {
                result[vertex * wanted + component] = fill;
            }
        }
        return result;
    }

    private static List<float[]> UnpackNormal101010(uint[] words)
    {
        int count = words.Length;
        float[] snorm = new float[count * 3];
        float[] unorm = new float[count * 3];
        for (int index = 0; index < count; index++)
        {
            uint word = words[index];
            int x = (int)(word & 0x3FF);
            int y = (int)((word >> 10) & 0x3FF);
            int z = (int)((word >> 20) & 0x3FF);
            snorm[index * 3] = Snorm(x);
            snorm[index * 3 + 1] = Snorm(y);
            snorm[index * 3 + 2] = Snorm(z);
            unorm[index * 3] = Unorm(x);
            unorm[index * 3 + 1] = Unorm(y);
            unorm[index * 3 + 2] = Unorm(z);
        }
        return [snorm, unorm];

        static float Snorm(int bits)
        {
            float signed = bits >= 512 ? bits - 1024 : bits;
            return MathF.Max(signed / 511f, -1f);
        }

        static float Unorm(int bits) => bits / 1023f * 2f - 1f;
    }

    /// <summary>Whether a field of vectors is predominantly unit length -- the trust gate for a
    /// stored normal or tangent, measured over the first three components.</summary>
    private static bool PredominantlyUnit(float[] values, int dimension)
    {
        int count = values.Length / dimension;
        if (count == 0)
        {
            return false;
        }
        int unit = 0;
        for (int index = 0; index < count; index++)
        {
            float length = Length3(values, index * dimension);
            if (MathF.Abs(length - 1f) < 0.15f)
            {
                unit++;
            }
        }
        return unit / (double)count > 0.9;
    }

    private static float Length3(float[] values, int offset)
    {
        float x = values[offset];
        float y = values[offset + 1];
        float z = values[offset + 2];
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    private static float[] NormalisedCopy(float[] normals)
    {
        float[] result = new float[normals.Length];
        int count = normals.Length / 3;
        for (int index = 0; index < count; index++)
        {
            float length = MathF.Max(Length3(normals, index * 3), 1e-6f);
            result[index * 3] = normals[index * 3] / length;
            result[index * 3 + 1] = normals[index * 3 + 1] / length;
            result[index * 3 + 2] = normals[index * 3 + 2] / length;
        }
        return result;
    }

    private static void BuildTriangles(DecodedMesh decoded, long[] indices, List<DecodedSubMesh> subMeshes)
    {
        List<uint> triangles = [];
        List<int> materials = [];
        for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
        {
            DecodedSubMesh subMesh = subMeshes[subMeshIndex];
            decoded.SubMeshes.Add(subMesh);
            if (subMesh.Topology != 0)
            {
                continue;
            }
            long start = Math.Clamp(subMesh.FirstIndex, 0, indices.Length);
            long end = Math.Clamp(subMesh.FirstIndex + subMesh.IndexCount, start, indices.Length);
            long triangleCount = (end - start) / 3;
            for (long triangle = 0; triangle < triangleCount; triangle++)
            {
                long at = start + triangle * 3;
                triangles.Add(unchecked((uint)(indices[at] + subMesh.BaseVertex)));
                triangles.Add(unchecked((uint)(indices[at + 1] + subMesh.BaseVertex)));
                triangles.Add(unchecked((uint)(indices[at + 2] + subMesh.BaseVertex)));
                materials.Add(subMeshIndex);
            }
        }
        decoded.Triangles = triangles.ToArray();
        decoded.TriangleMaterial = materials.ToArray();
    }

    /// <summary>Pick a packed-normal decode by testing it against the mesh itself: a correct
    /// vertex normal points the way the faces around it do, a wrong bit layout scores near
    /// zero. Only runs when the ordinary decode already failed.</summary>
    private static void ResolvePackedNormals(DecodedMesh decoded, List<float[]> candidates)
    {
        if (decoded.Normals is not null || candidates.Count == 0 || decoded.Positions is null
            || decoded.Triangles.Length == 0)
        {
            return;
        }
        float[] positions = decoded.Positions;
        uint[] triangles = decoded.Triangles;
        int vertexCount = positions.Length / 3;
        double[] accumulated = new double[vertexCount * 3];
        int triangleCount = triangles.Length / 3;
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            uint a = triangles[triangle * 3];
            uint b = triangles[triangle * 3 + 1];
            uint c = triangles[triangle * 3 + 2];
            if (a >= vertexCount || b >= vertexCount || c >= vertexCount)
            {
                continue;
            }
            float abx = positions[b * 3] - positions[a * 3];
            float aby = positions[b * 3 + 1] - positions[a * 3 + 1];
            float abz = positions[b * 3 + 2] - positions[a * 3 + 2];
            float acx = positions[c * 3] - positions[a * 3];
            float acy = positions[c * 3 + 1] - positions[a * 3 + 1];
            float acz = positions[c * 3 + 2] - positions[a * 3 + 2];
            float nx = aby * acz - abz * acy;
            float ny = abz * acx - abx * acz;
            float nz = abx * acy - aby * acx;
            float length = MathF.Max(MathF.Sqrt(nx * nx + ny * ny + nz * nz), 1e-12f);
            nx /= length;
            ny /= length;
            nz /= length;
            foreach (uint corner in stackalloc uint[] { a, b, c })
            {
                accumulated[corner * 3] += nx;
                accumulated[corner * 3 + 1] += ny;
                accumulated[corner * 3 + 2] += nz;
            }
        }
        bool[] used = new bool[vertexCount];
        int usedCount = 0;
        double[] geometric = new double[vertexCount * 3];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            double x = accumulated[vertex * 3];
            double y = accumulated[vertex * 3 + 1];
            double z = accumulated[vertex * 3 + 2];
            double length = Math.Sqrt(x * x + y * y + z * z);
            used[vertex] = length > 1e-9;
            usedCount += used[vertex] ? 1 : 0;
            double divisor = Math.Max(length, 1e-12);
            geometric[vertex * 3] = x / divisor;
            geometric[vertex * 3 + 1] = y / divisor;
            geometric[vertex * 3 + 2] = z / divisor;
        }
        if (usedCount == 0)
        {
            return;
        }
        double bestScore = 0.0;
        float[]? best = null;
        foreach (float[] candidate in candidates)
        {
            if (candidate.Length != positions.Length)
            {
                continue;
            }
            double sum = 0.0;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (!used[vertex])
                {
                    continue;
                }
                sum += candidate[vertex * 3] * geometric[vertex * 3]
                    + candidate[vertex * 3 + 1] * geometric[vertex * 3 + 1]
                    + candidate[vertex * 3 + 2] * geometric[vertex * 3 + 2];
            }
            double score = sum / usedCount;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        if (best is not null && bestScore > 0.35)
        {
            decoded.Normals = best;
        }
    }

    private static void DecodeBlendShapes(IMesh mesh, DecodedMesh decoded)
    {
        if (!mesh.Has_Shapes() || mesh.Shapes.Channels.Count == 0)
        {
            return;
        }
        var vertices = mesh.Shapes.Vertices;
        var frames = mesh.Shapes.Shapes;
        var fullWeights = mesh.Shapes.FullWeights;
        int channelIndex = 0;
        foreach (var channel in mesh.Shapes.Channels)
        {
            string name = channel.Name_R.String;
            if (name.Length == 0)
            {
                name = "blendshape" + channelIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            DecodedBlendShape shape = new() { Name = name };
            for (int frameIndex = channel.FrameIndex; frameIndex < channel.FrameIndex + channel.FrameCount; frameIndex++)
            {
                if (frameIndex < 0 || frameIndex >= frames.Count)
                {
                    throw new InvalidDataException(
                        $"'{decoded.Name}' blend shape '{name}' names frame {frameIndex} of {frames.Count}.");
                }
                var frame = frames[frameIndex];
                long first = Math.Clamp(frame.FirstVertex, 0, vertices.Count);
                long end = Math.Clamp(frame.FirstVertex + frame.VertexCount, first, vertices.Count);
                int moved = (int)(end - first);
                uint[] indices = new uint[moved];
                float[] positionDeltas = new float[moved * 3];
                float[] normalDeltas = new float[moved * 3];
                float[] tangentDeltas = new float[moved * 3];
                for (int entry = 0; entry < moved; entry++)
                {
                    var vertex = vertices[(int)first + entry];
                    indices[entry] = vertex.Index;
                    positionDeltas[entry * 3] = vertex.Vertex.X;
                    positionDeltas[entry * 3 + 1] = vertex.Vertex.Y;
                    positionDeltas[entry * 3 + 2] = vertex.Vertex.Z;
                    normalDeltas[entry * 3] = vertex.Normal.X;
                    normalDeltas[entry * 3 + 1] = vertex.Normal.Y;
                    normalDeltas[entry * 3 + 2] = vertex.Normal.Z;
                    tangentDeltas[entry * 3] = vertex.Tangent.X;
                    tangentDeltas[entry * 3 + 1] = vertex.Tangent.Y;
                    tangentDeltas[entry * 3 + 2] = vertex.Tangent.Z;
                }
                shape.Frames.Add(new DecodedBlendShapeFrame
                {
                    Weight = frameIndex < fullWeights.Count ? fullWeights[frameIndex] : 100f,
                    HasNormals = frame.HasNormals,
                    HasTangents = frame.HasTangents,
                    VertexIndices = indices,
                    PositionDeltas = positionDeltas,
                    NormalDeltas = normalDeltas,
                    TangentDeltas = tangentDeltas,
                });
            }
            decoded.BlendShapes.Add(shape);
            channelIndex++;
        }
    }
}
