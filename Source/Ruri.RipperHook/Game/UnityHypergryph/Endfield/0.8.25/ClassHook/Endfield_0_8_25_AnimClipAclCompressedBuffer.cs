using System.Collections.Generic;
using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.GenericBinding;
using Ruri.SourceGenerated.Classes.ClassID_74;
using Ruri.SourceGenerated.Subclasses.AnimClipAclCompressedBuffer;
using Ruri.SourceGenerated.Subclasses.Clip;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
{
    [RetargetMethod(typeof(AnimClipAclCompressedBuffer_2021_3_825), "ReadRelease", isBefore: true, isReturn: true)]
    public void AnimClipAclCompressedBuffer_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as AnimClipAclCompressedBuffer_2021_3_825;
        
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

    [RetargetMethod(typeof(AnimationClip_2021_3_825), "ReadRelease", isBefore: false, isReturn: false)]
    public void AnimationClip_2021_3_825_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as AnimationClip_2021_3_825;
        
        var clipData = _this.MuscleClip_C74?.Clip.Data as IClip;
        if (clipData == null) return;

        // Metadata
        uint streamedCurveCount = 0;
        if (clipData.StreamedClip.Has_CurveCount_UInt32()) streamedCurveCount = clipData.StreamedClip.CurveCount_UInt32;
        else if (clipData.StreamedClip.Has_CurveCount_UInt16()) streamedCurveCount = clipData.StreamedClip.CurveCount_UInt16;

        uint constantCurveCount = 0;
        if (clipData.Has_ConstantClip() && clipData.ConstantClip != null) constantCurveCount = (uint)clipData.ConstantClip.Data.Count;

        var denseClip = clipData.DenseClip;
        var aclCompressedBuffer = _this.AclCompressedBuffer_C74;

        if (aclCompressedBuffer == null || aclCompressedBuffer.TransformBufferData == null || aclCompressedBuffer.TransformBufferData.Length == 0) return;

        // Initialize Decompressors
        var decompressor = new Ruri.RipperHook.Endfield.ACL.AclDecompressor(aclCompressedBuffer.TransformBufferData);
        var floatDecompressor = (aclCompressedBuffer.FloatBufferData != null && aclCompressedBuffer.FloatBufferData.Length > 0) 
            ? new Ruri.RipperHook.Endfield.ACL.AclFloatDecompressor(aclCompressedBuffer.FloatBufferData) : null;
        var rootDecompressor = (aclCompressedBuffer.RootMotionBufferData != null && aclCompressedBuffer.RootMotionBufferData.Length > 0)
            ? new Ruri.RipperHook.Endfield.ACL.AclDecompressor(aclCompressedBuffer.RootMotionBufferData) : null;

        var startTime = _this.MuscleClip_C74.StartTime;
        var endTime = _this.MuscleClip_C74.StopTime;
        var sampleRate = _this.SampleRate_C74;
        var frameCount = (int)((endTime - startTime) * sampleRate) + 1;

        // Calculate Curves
        uint expectedCurves = clipData.Has_CompressedCurveCount() ? clipData.CompressedCurveCount : 0;
        uint aclTargetCurves = (expectedCurves >= (streamedCurveCount + constantCurveCount)) ? expectedCurves - streamedCurveCount - constantCurveCount : 0;
        
        // Masks
        int numTracks = (int)decompressor.NumTracks;
        byte[] fullTrackMasks = new byte[numTracks];
        var transformData = aclCompressedBuffer.TransformBufferData;
        
        // Header Parsing
        int subTrackTypesOffset = System.BitConverter.ToInt32(transformData, 32 + 40);
        int absoluteOffset = 32 + subTrackTypesOffset;
        int totalSubTracks = numTracks * 3;
        int numEntries = (totalSubTracks + 15) / 16;
        int subTypeIdx = 0;

        for (int i = 0; i < numEntries && absoluteOffset + i * 4 + 4 <= transformData.Length; i++)
        {
            uint entry = System.BitConverter.ToUInt32(transformData, absoluteOffset + i * 4);
            for (int j = 0; j < 16 && subTypeIdx < totalSubTracks; j++)
            {
                uint typeVal = (entry >> (30 - 2 * j)) & 3; 
                if (typeVal != 0) fullTrackMasks[subTypeIdx / 3] |= (byte)(1 << (subTypeIdx % 3));
                subTypeIdx++;
            }
        }
        
        uint totalCurves = 0;
        for (int i = 0; i < numTracks; i++)
        {
            byte mask = fullTrackMasks[i];
            if ((mask & 1) != 0) totalCurves += 4;
            if ((mask & 2) != 0) totalCurves += 3;
            if ((mask & 4) != 0) totalCurves += 3;
        }
        if (totalCurves == 0) for(int i=0; i<numTracks; i++) totalCurves += 10;

        uint actualCurveCount = (aclTargetCurves > 0 && aclTargetCurves <= totalCurves) ? aclTargetCurves : totalCurves;

        // Populate
        if (denseClip != null)
        {
            denseClip.CurveCount = actualCurveCount;
            denseClip.FrameCount = (int)frameCount;
            denseClip.SampleRate = sampleRate;
            denseClip.BeginTime = (float)startTime;
            
            float[] sampleData = new float[actualCurveCount * frameCount];
            
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sampleTime = (float)(startTime + frame / sampleRate);
                
                if (decompressor.Sample(sampleTime, out var transforms))
                {
                    int curveOffset = 0;
                    int frameBase = frame * (int)actualCurveCount;
                    
                    for (int bone = 0; bone < numTracks && bone < transforms.Length; bone++)
                    {
                        if (curveOffset + 3 > actualCurveCount) break;
                        var tfm = transforms[bone];
                        sampleData[frameBase + curveOffset + 0] = tfm.Translation.X;
                        sampleData[frameBase + curveOffset + 1] = tfm.Translation.Y;
                        sampleData[frameBase + curveOffset + 2] = tfm.Translation.Z;
                        curveOffset += 3;
                    }
                    for (int bone = 0; bone < numTracks && bone < transforms.Length; bone++)
                    {
                        if (curveOffset + 4 > actualCurveCount) break;
                        var tfm = transforms[bone];
                        sampleData[frameBase + curveOffset + 0] = tfm.Rotation.X;
                        sampleData[frameBase + curveOffset + 1] = tfm.Rotation.Y;
                        sampleData[frameBase + curveOffset + 2] = tfm.Rotation.Z;
                        sampleData[frameBase + curveOffset + 3] = tfm.Rotation.W;
                        curveOffset += 4;
                    }

                    if (floatDecompressor != null && floatDecompressor.Sample(sampleTime, out var floatValues))
                    {
                         int floatBase = (int)actualCurveCount - aclCompressedBuffer.FloatCurveCount;
                         for (int i = 0; i < floatValues.Length && (floatBase + i) < actualCurveCount; i++)
                         {
                             sampleData[frameBase + floatBase + i] = floatValues[i];
                         }
                    }

                    if (rootDecompressor != null && rootDecompressor.Sample(sampleTime, out var rootTransforms) && rootTransforms.Length > 0)
                    {
                        if (aclCompressedBuffer.RootPosIndex != 0xFFFF)
                        {
                             int idx = (int)actualCurveCount - aclCompressedBuffer.FloatCurveCount + aclCompressedBuffer.RootPosIndex;
                             if (idx + 3 <= actualCurveCount)
                             {
                                 sampleData[frameBase + idx + 0] = rootTransforms[0].Translation.X;
                                 sampleData[frameBase + idx + 1] = rootTransforms[0].Translation.Y;
                                 sampleData[frameBase + idx + 2] = rootTransforms[0].Translation.Z;
                             }
                        }
                        if (aclCompressedBuffer.RootRotIndex != 0xFFFF)
                        {
                             int idx = (int)actualCurveCount - aclCompressedBuffer.FloatCurveCount + aclCompressedBuffer.RootRotIndex;
                             if (idx + 4 <= actualCurveCount)
                             {
                                 sampleData[frameBase + idx + 0] = rootTransforms[0].Rotation.X;
                                 sampleData[frameBase + idx + 1] = rootTransforms[0].Rotation.Y;
                                 sampleData[frameBase + idx + 2] = rootTransforms[0].Rotation.Z;
                                 sampleData[frameBase + idx + 3] = rootTransforms[0].Rotation.W;
                             }
                        }
                    }
                }
            }
            denseClip.SampleArray.Clear();
            denseClip.SampleArray.AddRange(sampleData);
        }

        decompressor.Dispose();
        floatDecompressor?.Dispose();
        rootDecompressor?.Dispose();
        _this.Name += "_ACL";
    }
}