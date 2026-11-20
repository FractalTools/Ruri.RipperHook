using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.RuriEngine;

internal readonly record struct VertexChannel(int Stream, int Offset, int Format, int Dimension);

internal sealed class VertexBuffer
{
    private const int StreamAlignment = 16;

    private readonly byte[] data;
    private readonly int vertexCount;
    private readonly VertexChannel[] channels;
    private readonly UnityVersion version;
    private readonly int[] strides;
    private readonly int[] offsets;

    public VertexBuffer(byte[] data, int vertexCount, VertexChannel[] channels, UnityVersion version)
    {
        this.data = data;
        this.vertexCount = vertexCount;
        this.channels = channels;
        this.version = version;
        int streamCount = channels.Length == 0 ? 0 : channels.Max(static channel => channel.Stream) + 1;
        strides = new int[streamCount];
        offsets = new int[streamCount];
        foreach (VertexChannel channel in channels)
        {
            if (channel.Dimension > 0)
            {
                strides[channel.Stream] += channel.Dimension * ComponentSize(channel);
            }
        }
        long cursor = 0;
        for (int stream = 0; stream < streamCount; stream++)
        {
            offsets[stream] = checked((int)cursor);
            cursor += (long)vertexCount * strides[stream];
            cursor = (cursor + StreamAlignment - 1) & ~(long)(StreamAlignment - 1);
        }
    }

    public bool Has(int channel) => channel < channels.Length && channels[channel].Dimension > 0;

    public int Dimension(int channel) => channels[channel].Dimension;

    public float[] Floats(int channel)
    {
        MeshHelper.VertexFormat format = FormatOf(channels[channel]);
        if (MeshHelper.IsIntFormat(format))
        {
            throw new FormatException($"vertex channel {channel} holds integers, not floating point values");
        }
        return MeshHelper.BytesToFloatArray(Components(channel, format), format);
    }

    public int[] Integers(int channel)
    {
        MeshHelper.VertexFormat format = FormatOf(channels[channel]);
        if (!MeshHelper.IsIntFormat(format))
        {
            throw new FormatException($"vertex channel {channel} holds floating point values, not integers");
        }
        return MeshHelper.BytesToIntArray(Components(channel, format), format);
    }

    private byte[] Components(int index, MeshHelper.VertexFormat format)
    {
        VertexChannel channel = channels[index];
        int vertexBytes = channel.Dimension * MeshHelper.GetFormatSize(format);
        int stride = strides[channel.Stream];
        int start = offsets[channel.Stream] + channel.Offset;
        if (vertexCount > 0 && (long)start + (long)stride * (vertexCount - 1) + vertexBytes > data.Length)
        {
            throw new FormatException($"vertex channel {index} reads past the end of the {data.Length} byte vertex buffer");
        }
        byte[] components = new byte[vertexCount * vertexBytes];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            Buffer.BlockCopy(data, start + stride * vertex, components, vertex * vertexBytes, vertexBytes);
        }
        return components;
    }

    private int ComponentSize(VertexChannel channel) => MeshHelper.GetFormatSize(FormatOf(channel));

    private MeshHelper.VertexFormat FormatOf(VertexChannel channel) => MeshHelper.ToVertexFormat(channel.Format, version);
}
