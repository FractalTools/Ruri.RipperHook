using AssetRipper.Assets;
using AssetRipper.IO.Endian;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Subclasses.Matrix4x4f;
using AssetRipper.SourceGenerated.Subclasses.MinMaxAABB;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.VertexData;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    [RetargetMethod(typeof(Mesh_2020_1_0_a19))]
    public void Mesh_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as Mesh_2020_1_0_a19;
        var type = typeof(Mesh_2020_1_0_a19);

        // 1. 基础信息
        _this.m_Name = reader.ReadRelease_Utf8StringAlign();
        _this.m_SubMeshes.ReadRelease_ArrayAlign_Asset<SubMesh_2017_3>(ref reader);
        _this.m_Shapes.ReadRelease(ref reader);
        _this.m_BindPose.ReadRelease_ArrayAlign_Asset<Matrix4x4f>(ref reader);
        _this.m_BoneNameHashes.ReadRelease_ArrayAlign_UInt32(ref reader);
        _this.m_RootBoneNameHash = reader.ReadUInt32();
        _this.m_BonesAABB.ReadRelease_ArrayAlign_Asset<MinMaxAABB>(ref reader);
        _this.m_VariableBoneCountWeights.ReadRelease(ref reader);

        // 2. 压缩与属性
        _this.m_MeshCompression = reader.ReadByte();
        if (_this.m_MeshCompression == 4)
        {
            _this.m_MeshCompression = 0;
        }

        _this.m_IsReadable = reader.ReadBoolean();
        _this.m_KeepVertices = reader.ReadBoolean();
        _this.m_KeepIndices = reader.ReadBoolean();

        // 3. 旧版新增定义 (作为局部变量缓存，用于对齐流)
        var m_CollisionMeshOnly = reader.ReadBoolean();
        var m_CollisionMeshBaked = reader.ReadBoolean();
        var m_CollisionMeshConvex = reader.ReadRelease_BooleanAlign();

        // 4. 数据体
        _this.m_IndexFormat = reader.ReadInt32();
        _this.m_IndexBuffer = reader.ReadRelease_ArrayAlign_Byte();
        _this.m_VertexData.ReadRelease_AssetAlign(ref reader);

        // 5. 根据旧版逻辑处理 CompressedMesh
        if (!m_CollisionMeshBaked)
        {
            _this.m_CompressedMesh.ReadRelease(ref reader);
        }

        _this.m_LocalAABB.ReadRelease(ref reader);
        _this.m_MeshUsageFlags = reader.ReadInt32();
        _this.m_BakedConvexCollisionMesh = reader.ReadRelease_ArrayAlign_Byte();
        _this.m_BakedTriangleCollisionMesh = reader.ReadRelease_ArrayAlign_Byte();

        // 6. 度量信息与对齐
        _this.m_MeshMetrics_0_ = reader.ReadSingle();
        _this.m_MeshMetrics_1_ = reader.ReadSingle();
        var m_MeshMetrics_2_ = reader.ReadRelease_SingleAlign();

        // 7. 流数据与结尾
        _this.m_StreamData.ReadRelease(ref reader);
        var m_BonesPerVertex = reader.ReadUInt32();
    }
}