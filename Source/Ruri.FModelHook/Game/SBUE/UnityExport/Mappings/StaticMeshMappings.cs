using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

// UStaticMesh -> Mesh. Geometry comes through CUE4Parse-Conversion's decoder
// (positions / normals / tangents / UVs / colors already unpacked from the
// cooked vertex buffers), then VertexPacker writes AssetRipper's lossless
// uncompressed VertexData + index buffer + submeshes + bounds. Only LOD0 is
// exported (Unity meshes are single-LOD; LOD groups are a separate asset).
public static class StaticMeshMappings
{
    public static void Register()
    {
        MapperRegistry.Map<UStaticMesh, IMesh>(collection => collection.CreateMesh())
            .Set(t => t.Name, s => new Utf8String(s.Name))
            .After(BuildGeometry);
    }

    private static void BuildGeometry(UStaticMesh source, IMesh mesh, ConversionContext context)
    {
        if (!source.TryConvert(out CStaticMesh converted) || converted.LODs.Count == 0)
            return;

        CStaticMeshLod lod = converted.LODs[0];
        if (lod.SkipLod || lod.Verts is null || lod.Verts.Length == 0)
            return;

        MeshData meshData = MeshDataFactory.FromStaticMeshLod(lod);
        VertexPacker.Pack(mesh, meshData);
    }
}
