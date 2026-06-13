using AssetRipper.Assets.Generics;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.StreamInfo;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.VertexData;
using NativeMeshColliderCookingOptions = AssetRipper.SourceGenerated.NativeEnums.Global.MeshColliderCookingOptions;
using NativeMeshUsageFlags = AssetRipper.SourceGenerated.NativeEnums.Global.MeshUsageFlags;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

// Packs a MeshData (managed vertex arrays) into a Unity Mesh's UNCOMPRESSED
// VertexData blob — lossless, unlike the quantized CompressedMesh path. The byte
// packing itself is AssetRipper's own VertexDataBlob.Create, so the bytes are
// exactly what AR's reader expects; we only flush the blob onto mesh.VertexData
// (Data + Channels + Streams + current-channel mask) and add the index buffer,
// submeshes, bounds, plus the runtime metadata fields (KeepIndices/KeepVertices,
// MeshUsageFlags, CookingOptions, MeshOptimizationFlags, MeshMetrics) that
// AR.YamlStreamedAssetExporter will serialize into the .asset and Unity needs at
// load time for isReadable, MeshCollider cooking and UV-distribution sampling —
// the same set MeshExtensions.FillWithCompressedMeshData writes for skinned meshes.
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

        // Runtime metadata. Same defaults as MeshExtensions.FillWithCompressedMeshData
        // (which the skeletal-mesh path already uses) so the YAML exporter writes
        // sane values instead of zeros:
        //   * KeepIndices / KeepVertices = true  -> Mesh.isReadable at runtime
        //     (Unity strips CPU-side index/vertex buffers from non-readable meshes;
        //      defaults of false would orphan our backfilled data).
        //   * MeshUsageFlags = None              -> matches a freshly imported Unity mesh.
        //   * CookingOptions = DefaultCookingFlags -> MeshCollider will accept this
        //     mesh; the all-zero default disables every cook step and silently
        //     produces an unusable collider.
        //   * MeshOptimizationFlags = Everything -> matches Unity's "Optimize Mesh
        //     = On" import default; without it the inspector reports the mesh as
        //     unoptimized even though the data is exporter-quality.
        //   * MeshMetrics_0_/1_ = 1.0            -> UV distribution metric used by
        //     Mesh.GetUVDistributionMetric for mip-bias sampling; the safe default
        //     is 1.0 (the same fallback CalculateMeshMetric returns when there's
        //     no UV data). Computing the real metric here would duplicate AR's
        //     CalculateMeshMetric, which is private to the static class — 1.0 is
        //     what AR itself falls back to when UV1 is absent on static meshes.
        if (mesh.Has_KeepIndices()) mesh.KeepIndices = true;
        if (mesh.Has_KeepVertices()) mesh.KeepVertices = true;
        mesh.MeshUsageFlags = (int)NativeMeshUsageFlags.MeshUsageFlagNone;
        if (mesh.Has_CookingOptions())
            mesh.CookingOptions = (int)NativeMeshColliderCookingOptions.DefaultCookingFlags;
        mesh.SetMeshOptimizationFlags(MeshOptimizationFlags.Everything);
        if (mesh.Has_MeshMetrics_0_()) mesh.MeshMetrics_0_ = 1.0f;
        if (mesh.Has_MeshMetrics_1_()) mesh.MeshMetrics_1_ = 1.0f;
    }
}
