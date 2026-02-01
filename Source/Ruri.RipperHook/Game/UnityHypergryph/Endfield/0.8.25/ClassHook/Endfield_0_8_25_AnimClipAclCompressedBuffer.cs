using AssetRipper.IO.Endian;
using Ruri.SourceGenerated.Subclasses.AnimClipAclCompressedBuffer;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
{
    [RetargetMethod(typeof(AnimClipAclCompressedBuffer), "ReadRelease", isBefore: true, isReturn: true)]
    public void AnimClipAclCompressedBuffer_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as AnimClipAclCompressedBuffer;
        var type = typeof(AnimClipAclCompressedBuffer);

        _this.Header.ReadRelease(ref reader);
        _this.TransformBufferData = reader.ReadRelease_ArrayAlign_Byte();
        _this.RootMotionBufferData = reader.ReadRelease_ArrayAlign_Byte();
        _this.FloatBufferData = reader.ReadRelease_ArrayAlign_Byte();
        _this.TransformSubTrackMasks = reader.ReadRelease_ArrayAlign_Byte();
        _this.TransformSubTrackConstantMasks = reader.ReadRelease_ArrayAlign_Byte();
        _this.OutputTrackCount = reader.ReadUInt16();
        _this.RootPosIndex = reader.ReadUInt16();
        _this.RootRotIndex = reader.ReadUInt16();
        _this.RootScaleIndex = reader.ReadUInt16();
        _this.RootTrackCount = reader.ReadUInt16();
        _this.FloatCurveCount = reader.ReadUInt16();
        _this.DefaultIndexs.ReadRelease_ArrayAlign_UInt16(ref reader);
        _this.ConstantIndexs.ReadRelease_ArrayAlign_UInt16(ref reader);
        _this.ConstantValues.ReadRelease_ArrayAlign_Single(ref reader);
    }
}