using AssetRipper.Assets.Generics;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.StreamInfo;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.VertexData;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

// Packs a MeshData (managed vertex arrays) into a Unity Mesh's UNCOMPRESSED
// VertexData blob — lossless, unlike the quantized CompressedMesh path. The byte
// packing itself is AssetRipper's own VertexDataBlob.Create, so the bytes are
// exactly what AR's reader expects; we only flush the blob onto mesh.VertexData
// (Data + Channels + Streams + current-channel mask) and add the index buffer,
// submeshes and bounds.
public static class VertexPacker
{
    public static void Pack(IMesh mesh, in MeshData meshData)
    {
        mesh.SetIndexFormat(meshData.IndexFormat);

        VertexDataBlob blob = VertexDataBlob.Create(meshData, mesh.Collection.Version, mesh.Collection.EndianType);

        IVertexData vertexData = mesh.VertexData;
        vertexData.VertexCount = (uint)blob.VertexCount;
        vertexData.Data = blob.Data;

        // Channels: one entry per ShaderChannel slot (dimension 0 = unused). The
        // current-channels mask must flag every active channel or the reader skips it.
        if (vertexData.Has_Channels())
        {
            uint channelMask = 0;
            for (int i = 0; i < blob.Channels.Count; i++)
            {
                ChannelInfo source = blob.Channels[i];
                ChannelInfo destination = vertexData.Channels.AddNew();
                destination.Stream = source.Stream;
                destination.Offset = source.Offset;
                destination.Format = source.Format;
                destination.Dimension = source.Dimension;
                if (source.GetDataDimension() > 0) channelMask |= 1u << i;
            }
            vertexData.SetCurrentChannels(channelMask);
        }

        // Streams (modern list schema; the legacy 4-field schema is pre-Unity-4).
        if (vertexData.Has_Streams())
        {
            foreach (IStreamInfo source in blob.Streams)
            {
                IStreamInfo destination = vertexData.Streams.AddNew();
                destination.ChannelMask = source.ChannelMask;
                destination.Offset = source.Offset;
                destination.SetStride(source.GetStride());
            }
        }

        // Index buffer (raw bytes paired with IndexFormat) + submeshes + bounds.
        mesh.SetProcessedIndexBuffer(meshData.ProcessedIndexBuffer);

        AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
        foreach (SubMeshData subMesh in meshData.SubMeshes)
            subMesh.CopyTo(subMeshes.AddNew(), mesh.GetIndexFormat());

        mesh.LocalAABB.CalculateFromVertexArray(meshData.Vertices);
        mesh.SetMeshCompression(ModelImporterMeshCompression.Off);
    }
}
