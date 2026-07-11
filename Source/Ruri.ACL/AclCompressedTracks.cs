using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ruri.ACL
{
    [StructLayout(LayoutKind.Sequential)]
    public struct AclTransform
    {
        public Quaternion Rotation;
        public Vector3 Translation;
        public Vector3 Scale;
    }

    /// <summary>
    /// Pure C# port of the ACL 2.x uniformly-sampled decompressor (nfrechette/acl develop branch).
    /// Supports transform (qvvf) and scalar (float1f) tracks, versions v02_00_00..v02_01_00,
    /// including stripped-keyframe clips (tier-0 samples only, no external database).
    /// Decompression only; no compression.
    /// </summary>
    internal sealed class AclCompressedTracks
    {
        // buffer_tag32::compressed_tracks
        private const uint TagCompressedTracks = 0xac11ac11;

        // compressed_tracks_version16
        private const ushort Version_02_00_00 = 7;
        private const ushort Version_02_01_99_1 = 9;
        private const ushort VersionFirst = 7;
        private const ushort VersionLatest = 10;

        // track_type8
        private const byte TrackTypeFloat1F = 0;
        private const byte TrackTypeQvvf = 12;

        // rotation_format8
        private const int RotFormatQuatFull = 0;
        private const int RotFormatQuatDropWVariable = 3;

        private const int TracksHeaderOffset = 8;   // after raw_buffer_header
        private const int BodyOffset = 32;          // raw_buffer_header(8) + tracks_header(24)

        // ACL 2.0 scalar bit rate table (k_bit_rate_num_bits_v0)
        private static readonly byte[] ScalarBitRatesV0 = { 0, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 32 };
        // ACL 2.1 scalar bit rate table (k_bit_rate_num_bits)
        private static readonly byte[] ScalarBitRatesV1 = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 32 };

        private readonly byte[] _d; // padded copy of the compressed buffer

        public bool IsValid { get; }
        public ushort Version { get; }
        public byte TrackType { get; }
        public uint NumTracks { get; }
        public uint NumSamples { get; }
        public float SampleRate { get; }

        private readonly uint _misc;

        // transform_tracks_header
        private readonly uint _numSegments;
        private readonly uint _numAnimVar;
        private readonly uint _numAnimRot;
        private readonly uint _numAnimTrans;
        private readonly uint _numAnimScale;
        private readonly uint _numConstRot;
        private readonly uint _numConstTrans;
        private readonly int _segHeadersOff;
        private readonly int _subTrackTypesOff;
        private readonly int _constDataOff;
        private readonly int _clipRangeOff;
        private readonly int _segStartIndicesOff;

        // scalar_tracks_header
        private readonly uint _numBitsPerFrame;
        private readonly int _scalarMetaOff;
        private readonly int _scalarConstOff;
        private readonly int _scalarRangeOff;
        private readonly int _scalarAnimOff;

        public AclCompressedTracks(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length < BodyOffset)
            {
                _d = Array.Empty<byte>();
                return;
            }

            // Padded copy: the bit readers over-read up to 8 bytes like the C++ unsafe unpackers over-read 16
            _d = new byte[compressedData.Length + 32];
            Buffer.BlockCopy(compressedData, 0, _d, 0, compressedData.Length);

            uint tag = U32(TracksHeaderOffset);
            Version = U16(TracksHeaderOffset + 4);
            byte algorithmType = _d[TracksHeaderOffset + 6];
            TrackType = _d[TracksHeaderOffset + 7];
            NumTracks = U32(TracksHeaderOffset + 8);
            NumSamples = U32(TracksHeaderOffset + 12);
            SampleRate = F32(TracksHeaderOffset + 16);
            _misc = U32(TracksHeaderOffset + 20);

            if (tag != TagCompressedTracks || algorithmType != 0 || Version < VersionFirst || Version > VersionLatest)
                return;

            if (TrackType == TrackTypeQvvf)
            {
                if (compressedData.Length < BodyOffset + 52)
                    return;

                _numSegments = U32(BodyOffset + 0);
                _numAnimVar = U32(BodyOffset + 4);
                _numAnimRot = U32(BodyOffset + 8);
                _numAnimTrans = U32(BodyOffset + 12);
                _numAnimScale = U32(BodyOffset + 16);
                _numConstRot = U32(BodyOffset + 20);
                _numConstTrans = U32(BodyOffset + 24);
                // BodyOffset + 28: num_constant_scale_samples (not needed)
                // BodyOffset + 32: database_header_offset (no external database support)
                _segHeadersOff = BodyOffset + (int)U32(BodyOffset + 36);
                _subTrackTypesOff = BodyOffset + (int)U32(BodyOffset + 40);
                _constDataOff = BodyOffset + (int)U32(BodyOffset + 44);
                _clipRangeOff = BodyOffset + (int)U32(BodyOffset + 48);
                _segStartIndicesOff = BodyOffset + 52; // only meaningful when num_segments > 1
            }
            else
            {
                if (compressedData.Length < BodyOffset + 20)
                    return;

                _numBitsPerFrame = U32(BodyOffset + 0);
                _scalarMetaOff = BodyOffset + (int)U32(BodyOffset + 4);
                _scalarConstOff = BodyOffset + (int)U32(BodyOffset + 8);
                _scalarRangeOff = BodyOffset + (int)U32(BodyOffset + 12);
                _scalarAnimOff = BodyOffset + (int)U32(BodyOffset + 16);
            }

            IsValid = true;
        }

        private bool WrapOptimized => Version > Version_02_00_00 && ((_misc >> 30) & 1) != 0;
        private bool HasScale => (_misc & 1) != 0;
        private float DefaultScale => (_misc >> 1) & 1;
        private int ScaleFormat => (int)((_misc >> 2) & 1);
        private int TranslationFormat => (int)((_misc >> 3) & 1);
        private int RotationFormat => (int)((_misc >> 4) & 15);
        private bool HasDatabase => ((_misc >> 8) & 1) != 0;
        private bool HasStrippedKeyframes => ((_misc >> 10) & 1) != 0;
        private bool HasSegments => _numSegments > 1;

        /// <summary>Finite clip duration honoring the compressed looping policy (wrap adds a repeating first sample).</summary>
        public float FiniteDuration
        {
            get
            {
                uint n = NumSamples;
                if (WrapOptimized && n != 0)
                    n++;
                if (n <= 1)
                    return 0.0f;
                return (n - 1) / SampleRate;
            }
        }

        //////////////////////////////////////////////////////////////////////////
        // Little-endian data reads

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(offset, 4));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(offset, 2));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float F32(int offset) => BitConverter.Int32BitsToSingle((int)U32(offset));

        //////////////////////////////////////////////////////////////////////////
        // Big-endian packed animated bit stream reads

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint BitsBE32(int bitOffset)
        {
            int byteOff = bitOffset >> 3;
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(_d.AsSpan(byteOff, 8));
            return (uint)((v << (bitOffset & 7)) >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint BitsBE(int bitOffset, int numBits)
        {
            // numBits <= 23, mirrors unpack_vector3_uXX_unsafe
            int byteOff = bitOffset >> 3;
            uint v = BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(byteOff, 4));
            return (v >> (32 - numBits - (bitOffset & 7))) & ((1u << numBits) - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float FloatBE(int bitOffset) => BitConverter.Int32BitsToSingle((int)BitsBE32(bitOffset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 Vector3BE96(int bitOffset)
            => new Vector3(FloatBE(bitOffset), FloatBE(bitOffset + 32), FloatBE(bitOffset + 64));

        //////////////////////////////////////////////////////////////////////////
        // Seek (find_linear_interpolation_samples_with_sample_rate, rounding policy: none)

        private void FindSamples(float sampleTime, bool wrap, out uint key0, out uint key1, out float alpha)
        {
            uint numSamples = NumSamples;
            uint lastIndex = numSamples - 1;

            float sampleIndex = sampleTime * SampleRate;
            uint index0 = (uint)sampleIndex;
            uint next = index0 + 1;

            uint index1;
            if (!wrap)
            {
                if (index0 > lastIndex)
                    index0 = lastIndex;
                index1 = Math.Min(next, lastIndex);
            }
            else
            {
                if (index0 > lastIndex)
                {
                    // Sampling the artificially repeating first sample with full weight
                    sampleIndex = 0.0f;
                    index0 = 0;
                    index1 = 0;
                }
                else
                {
                    index1 = next >= numSamples ? 0 : next;
                }
            }

            key0 = index0;
            key1 = index1;
            alpha = Math.Clamp(sampleIndex - index0, 0.0f, 1.0f);
        }

        // find_linear_interpolation_alpha with sample_rounding_policy::none
        private static float FindAlpha(float sampleIndex, uint key0, uint key1)
        {
            if (key0 == key1)
                return 0.0f;

            float alpha = key0 < key1
                ? (sampleIndex - key0) / (key1 - key0)
                : (sampleIndex - key0);
            return Math.Clamp(alpha, 0.0f, 1.0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

        private int FindSegment(uint clipKey)
        {
            for (uint i = 1; i < _numSegments; i++)
            {
                if (clipKey < U32(_segStartIndicesOff + (int)i * 4))
                    return (int)(i - 1);
            }
            return (int)(_numSegments - 1);
        }

        //////////////////////////////////////////////////////////////////////////
        // Transform decompression

        private struct Cursor
        {
            public int Meta;      // absolute byte offset into format_per_track_data
            public int SegRange;  // absolute byte offset into segment range data
            public int Bit;       // absolute bit offset into animated data
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SubTrackType(int entriesBase, int trackIndex)
            => (int)((U32(entriesBase + (trackIndex >> 4) * 4) >> ((15 - (trackIndex & 15)) * 2)) & 3);

        public bool DecompressTransforms(float sampleTime, AclTransform[] output)
        {
            if (!IsValid || TrackType != TrackTypeQvvf || output == null || output.Length < NumTracks)
                return false;

            int numTracks = (int)NumTracks;
            if (numTracks == 0)
                return true;
            if (NumSamples == 0)
                return false;

            // ---- seek_v0 ----
            bool wrap = WrapOptimized;
            float clampedTime = Math.Clamp(sampleTime, 0.0f, FiniteDuration);
            FindSamples(clampedTime, wrap, out uint clipKey0, out uint clipKey1, out float alpha);

            bool hasStripped = HasDatabase || HasStrippedKeyframes;
            int segStride = hasStripped ? 20 : 16; // stripped_segment_header_t vs segment_header

            int seg0 = 0;
            int seg1 = 0;
            uint segKey0, segKey1;
            uint segStart0 = 0, segStart1 = 0;

            if (_numSegments == 1)
            {
                segKey0 = clipKey0;
                segKey1 = clipKey1;
            }
            else
            {
                seg0 = FindSegment(clipKey0);
                seg1 = (wrap && clipKey1 == 0) ? 0 : FindSegment(clipKey1);
                segStart0 = U32(_segStartIndicesOff + seg0 * 4);
                segStart1 = U32(_segStartIndicesOff + seg1 * 4);
                segKey0 = clipKey0 - segStart0;
                segKey1 = clipKey1 - segStart1;
            }

            if (hasStripped)
            {
                // Clip relative fractional sample index before remapping
                float sampleIndex = alpha + clipKey0;

                uint sampleIndices0 = U32(_segHeadersOff + seg0 * segStride + 16);
                uint sampleIndices1 = U32(_segHeadersOff + seg1 * segStride + 16);

                // Nearest stored sample at or before key0 (bit 0 = MSB = sample 0)
                uint candidates0 = sampleIndices0 & (0xFFFFFFFFu << (int)(31 - segKey0));
                segKey0 = 31 - (uint)System.Numerics.BitOperations.TrailingZeroCount(candidates0);

                // Nearest stored sample at or after key1
                uint candidates1 = sampleIndices1 & (0xFFFFFFFFu >> (int)segKey1);
                segKey1 = (uint)System.Numerics.BitOperations.LeadingZeroCount(candidates1);

                clipKey0 = segStart0 + segKey0;
                clipKey1 = segStart1 + segKey1;

                alpha = FindAlpha(sampleIndex, clipKey0, clipKey1);

                // Remap to the index within the stored samples
                segKey0 = (uint)System.Numerics.BitOperations.PopCount(~(0xFFFFFFFFu >> (int)segKey0) & sampleIndices0);
                segKey1 = (uint)System.Numerics.BitOperations.PopCount(~(0xFFFFFFFFu >> (int)segKey1) & sampleIndices1);
            }

            // ---- per keyframe segment data pointers ----
            Span<Cursor> rotCtx = stackalloc Cursor[2];
            Span<Cursor> transCtx = stackalloc Cursor[2];
            Span<Cursor> scaleCtx = stackalloc Cursor[2];

            int rotFormat = RotationFormat;
            bool rotVariable = rotFormat == RotFormatQuatDropWVariable;
            bool transVariable = TranslationFormat == 1;
            bool scaleVariable = ScaleFormat == 1;
            bool hasScale = HasScale;
            bool multiSegment = HasSegments;

            for (int j = 0; j < 2; j++)
            {
                int segIdx = j == 0 ? seg0 : seg1;
                uint segKey = j == 0 ? segKey0 : segKey1;

                int hdr = _segHeadersOff + segIdx * segStride;
                uint poseBitSize = U32(hdr + 0);
                uint rotBitSize = U32(hdr + 4);
                uint transBitSize = U32(hdr + 8);
                int segData = BodyOffset + (int)U32(hdr + 12);

                int fmtOff = segData;
                int rangeOff = Align(fmtOff + (int)_numAnimVar, 2);
                int rangeSize = multiSegment ? 6 * (int)_numAnimVar : 0;
                int animOff = Align(rangeOff + rangeSize, 4);

                int keyBit = animOff * 8 + (int)(segKey * poseBitSize);

                int numAnimRotPadded = ((int)_numAnimRot + 3) & ~3;

                rotCtx[j] = new Cursor { Meta = fmtOff, SegRange = rangeOff, Bit = keyBit };
                transCtx[j] = new Cursor
                {
                    Meta = fmtOff + (rotVariable ? numAnimRotPadded : 0),
                    SegRange = rangeOff + (rotVariable ? numAnimRotPadded * 6 : 0),
                    Bit = keyBit + (int)rotBitSize
                };
                scaleCtx[j] = new Cursor
                {
                    Meta = transCtx[j].Meta + (transVariable ? (int)_numAnimTrans : 0),
                    SegRange = transCtx[j].SegRange + (transVariable ? (int)_numAnimTrans * 6 : 0),
                    Bit = transCtx[j].Bit + (int)transBitSize
                };
            }

            int clipRot = _clipRangeOff;
            int clipTrans = clipRot + (rotVariable ? (int)_numAnimRot * 24 : 0);
            int clipScale = clipTrans + (transVariable ? (int)_numAnimTrans * 24 : 0);

            int numEntries = (numTracks + 15) / 16;
            int rotTypesBase = _subTrackTypesOff;
            int transTypesBase = rotTypesBase + numEntries * 4;
            int scaleTypesBase = transTypesBase + numEntries * 4;

            // Constant data cursors
            int constRotSize = rotFormat == RotFormatQuatFull ? 16 : 12;
            int constRotBase = _constDataOff;
            int constTransBase = constRotBase + constRotSize * (int)_numConstRot;
            int constScaleBase = constTransBase + 12 * (int)_numConstTrans;

            int rawBits = Version >= Version_02_01_99_1 ? 31 : 32;

            Span<Vector3> v0 = stackalloc Vector3[4];
            Span<Vector3> v1 = stackalloc Vector3[4];
            Span<Vector4> q0 = stackalloc Vector4[4];
            Span<Vector4> q1 = stackalloc Vector4[4];
            Span<bool> segIgn0 = stackalloc bool[4];
            Span<bool> segIgn1 = stackalloc bool[4];
            Span<bool> clipIgn0 = stackalloc bool[4];
            Span<bool> clipIgn1 = stackalloc bool[4];
            Span<int> pending = stackalloc int[4];

            // ---- rotations ----
            {
                int constIdx = 0;
                int pendingCount = 0;

                for (int t = 0; t < numTracks; t++)
                {
                    int type = SubTrackType(rotTypesBase, t);
                    if (type == 0)
                    {
                        output[t].Rotation = Quaternion.Identity;
                    }
                    else if (type == 1)
                    {
                        output[t].Rotation = ReadConstantRotation(constRotBase, constIdx, rotFormat);
                        constIdx++;
                    }
                    else
                    {
                        pending[pendingCount++] = t;
                        if (pendingCount == 4)
                        {
                            FlushRotationGroup(output, pending, 4, rotCtx, ref clipRot, rotFormat, rawBits, alpha, q0, q1, segIgn0, segIgn1, clipIgn0, clipIgn1);
                            pendingCount = 0;
                        }
                    }
                }

                if (pendingCount > 0)
                    FlushRotationGroup(output, pending, pendingCount, rotCtx, ref clipRot, rotFormat, rawBits, alpha, q0, q1, segIgn0, segIgn1, clipIgn0, clipIgn1);
            }

            // ---- translations ----
            {
                int constOff = constTransBase;
                int pendingCount = 0;

                for (int t = 0; t < numTracks; t++)
                {
                    int type = SubTrackType(transTypesBase, t);
                    if (type == 0)
                    {
                        output[t].Translation = Vector3.Zero;
                    }
                    else if (type == 1)
                    {
                        output[t].Translation = new Vector3(F32(constOff), F32(constOff + 4), F32(constOff + 8));
                        constOff += 12;
                    }
                    else
                    {
                        pending[pendingCount++] = t;
                        if (pendingCount == 4)
                        {
                            FlushVector3Group(pending, 4, transCtx, ref clipTrans, transVariable, rawBits, v0, v1);
                            for (int i = 0; i < 4; i++)
                                output[pending[i]].Translation = Vector3.Lerp(v0[i], v1[i], alpha);
                            pendingCount = 0;
                        }
                    }
                }

                if (pendingCount > 0)
                {
                    FlushVector3Group(pending, pendingCount, transCtx, ref clipTrans, transVariable, rawBits, v0, v1);
                    for (int i = 0; i < pendingCount; i++)
                        output[pending[i]].Translation = Vector3.Lerp(v0[i], v1[i], alpha);
                }
            }

            // ---- scales ----
            if (!hasScale)
            {
                var defaultScale = new Vector3(DefaultScale);
                for (int t = 0; t < numTracks; t++)
                    output[t].Scale = defaultScale;
            }
            else
            {
                var defaultScale = new Vector3(DefaultScale);
                int constOff = constScaleBase;
                int pendingCount = 0;

                for (int t = 0; t < numTracks; t++)
                {
                    int type = SubTrackType(scaleTypesBase, t);
                    if (type == 0)
                    {
                        output[t].Scale = defaultScale;
                    }
                    else if (type == 1)
                    {
                        output[t].Scale = new Vector3(F32(constOff), F32(constOff + 4), F32(constOff + 8));
                        constOff += 12;
                    }
                    else
                    {
                        pending[pendingCount++] = t;
                        if (pendingCount == 4)
                        {
                            FlushVector3Group(pending, 4, scaleCtx, ref clipScale, scaleVariable, rawBits, v0, v1);
                            for (int i = 0; i < 4; i++)
                                output[pending[i]].Scale = Vector3.Lerp(v0[i], v1[i], alpha);
                            pendingCount = 0;
                        }
                    }
                }

                if (pendingCount > 0)
                {
                    FlushVector3Group(pending, pendingCount, scaleCtx, ref clipScale, scaleVariable, rawBits, v0, v1);
                    for (int i = 0; i < pendingCount; i++)
                        output[pending[i]].Scale = Vector3.Lerp(v0[i], v1[i], alpha);
                }
            }

            return true;
        }

        private Quaternion ReadConstantRotation(int constRotBase, int index, int rotFormat)
        {
            if (rotFormat == RotFormatQuatFull)
            {
                int o = constRotBase + index * 16;
                return new Quaternion(F32(o), F32(o + 4), F32(o + 8), F32(o + 12));
            }

            // Drop-w formats are stored in SOA groups of 4 (xxxx yyyy zzzz), last group unpadded
            int group = index >> 2;
            int lane = index & 3;
            int groupSize = Math.Min((int)_numConstRot - group * 4, 4);
            int baseOff = constRotBase + group * 48;

            var v = new Vector4(
                F32(baseOff + (groupSize * 0 + lane) * 4),
                F32(baseOff + (groupSize * 1 + lane) * 4),
                F32(baseOff + (groupSize * 2 + lane) * 4),
                0.0f);
            v = ReconstructW(v);
            return new Quaternion(v.X, v.Y, v.Z, v.W);
        }

        private void FlushRotationGroup(AclTransform[] output, Span<int> pending, int groupSize, Span<Cursor> ctx, ref int clipRot, int rotFormat, int rawBits, float alpha,
            Span<Vector4> q0, Span<Vector4> q1, Span<bool> segIgn0, Span<bool> segIgn1, Span<bool> clipIgn0, Span<bool> clipIgn1)
        {
            ctx[0] = UnpackQuatGroup(ctx[0], groupSize, rotFormat, rawBits, q0, segIgn0, clipIgn0);
            ctx[1] = UnpackQuatGroup(ctx[1], groupSize, rotFormat, rawBits, q1, segIgn1, clipIgn1);

            if (rotFormat == RotFormatQuatDropWVariable)
            {
                RemapClipRangeRotations(clipRot, groupSize, q0, clipIgn0);
                RemapClipRangeRotations(clipRot, groupSize, q1, clipIgn1);
                clipRot += groupSize * 24;
            }

            bool reconstructW = rotFormat != RotFormatQuatFull;
            for (int i = 0; i < groupSize; i++)
            {
                Vector4 a = reconstructW ? ReconstructW(q0[i]) : q0[i];
                Vector4 b = reconstructW ? ReconstructW(q1[i]) : q1[i];
                output[pending[i]].Rotation = LerpQuat(a, b, alpha);
            }
        }

        private Cursor UnpackQuatGroup(Cursor c, int groupSize, int rotFormat, int rawBits, Span<Vector4> outValues, Span<bool> segIgnore, Span<bool> clipIgnore)
        {
            if (rotFormat == RotFormatQuatDropWVariable)
            {
                bool hasSegments = HasSegments;
                for (int i = 0; i < groupSize; i++)
                {
                    int bits = _d[c.Meta + i];
                    Vector4 v;
                    if (bits == 0)
                    {
                        // Constant bit rate: 16-bit sample packed across the SOA segment range bytes
                        int s = c.SegRange + i;
                        uint x = ((uint)_d[s] << 8) | _d[s + 4];
                        uint y = ((uint)_d[s + 8] << 8) | _d[s + 12];
                        uint z = ((uint)_d[s + 16] << 8) | _d[s + 20];
                        v = new Vector4(x, y, z, 0.0f) * (1.0f / 65535.0f);
                        segIgnore[i] = true;
                        clipIgnore[i] = false;
                    }
                    else if (bits == rawBits)
                    {
                        var raw = Vector3BE96(c.Bit);
                        c.Bit += 96;
                        v = new Vector4(raw, 0.0f);
                        segIgnore[i] = true;
                        clipIgnore[i] = true;
                    }
                    else
                    {
                        float invMax = 1.0f / ((1 << bits) - 1);
                        v = new Vector4(
                            BitsBE(c.Bit, bits),
                            BitsBE(c.Bit + bits, bits),
                            BitsBE(c.Bit + bits * 2, bits),
                            0.0f) * invMax;
                        c.Bit += bits * 3;
                        segIgnore[i] = false;
                        clipIgnore[i] = false;
                    }

                    if (hasSegments && !segIgnore[i])
                    {
                        int s = c.SegRange + i;
                        var mn = new Vector4(_d[s], _d[s + 4], _d[s + 8], 0.0f) * (1.0f / 255.0f);
                        var ex = new Vector4(_d[s + 12], _d[s + 16], _d[s + 20], 0.0f) * (1.0f / 255.0f);
                        v = v * ex + mn;
                    }

                    outValues[i] = v;
                }

                // Metadata and segment range groups are padded to 4 samples
                c.Meta += 4;
                c.SegRange += 24;
            }
            else if (rotFormat == RotFormatQuatFull)
            {
                for (int i = 0; i < groupSize; i++)
                {
                    outValues[i] = new Vector4(FloatBE(c.Bit), FloatBE(c.Bit + 32), FloatBE(c.Bit + 64), FloatBE(c.Bit + 96));
                    c.Bit += 128;
                    segIgnore[i] = true;
                    clipIgnore[i] = true;
                }
            }
            else // quatf_drop_w_full
            {
                for (int i = 0; i < groupSize; i++)
                {
                    outValues[i] = new Vector4(Vector3BE96(c.Bit), 0.0f);
                    c.Bit += 96;
                    segIgnore[i] = true;
                    clipIgnore[i] = true;
                }
            }

            return c;
        }

        private void RemapClipRangeRotations(int clipOff, int groupSize, Span<Vector4> values, Span<bool> clipIgnore)
        {
            // Rotation clip range data is SOA within the group: min.x* min.y* min.z* extent.x* extent.y* extent.z*, stride = groupSize floats
            for (int i = 0; i < groupSize; i++)
            {
                if (clipIgnore[i])
                    continue;

                var mn = new Vector4(
                    F32(clipOff + (groupSize * 0 + i) * 4),
                    F32(clipOff + (groupSize * 1 + i) * 4),
                    F32(clipOff + (groupSize * 2 + i) * 4),
                    0.0f);
                var ex = new Vector4(
                    F32(clipOff + (groupSize * 3 + i) * 4),
                    F32(clipOff + (groupSize * 4 + i) * 4),
                    F32(clipOff + (groupSize * 5 + i) * 4),
                    0.0f);
                values[i] = values[i] * ex + mn;
            }
        }

        private void FlushVector3Group(Span<int> pending, int groupSize, Span<Cursor> ctx, ref int clipCursor, bool variable, int rawBits, Span<Vector3> v0, Span<Vector3> v1)
        {
            ctx[0] = UnpackVector3Group(ctx[0], clipCursor, groupSize, variable, rawBits, v0);
            ctx[1] = UnpackVector3Group(ctx[1], clipCursor, groupSize, variable, rawBits, v1);
            if (variable)
                clipCursor += groupSize * 24;
        }

        private Cursor UnpackVector3Group(Cursor c, int clipBase, int groupSize, bool variable, int rawBits, Span<Vector3> outValues)
        {
            if (!variable)
            {
                for (int i = 0; i < groupSize; i++)
                {
                    outValues[i] = Vector3BE96(c.Bit);
                    c.Bit += 96;
                }
                return c;
            }

            bool hasSegments = HasSegments;
            int clip = clipBase;
            for (int i = 0; i < groupSize; i++)
            {
                int bits = _d[c.Meta];
                c.Meta++;

                Vector3 v;
                bool skipClip;
                if (bits == 0)
                {
                    // Constant bit rate: u48 sample stored in the segment range slot
                    int s = c.SegRange;
                    v = new Vector3(U16(s), U16(s + 2), U16(s + 4)) * (1.0f / 65535.0f);
                    c.SegRange += 6;
                    skipClip = false;
                }
                else if (bits == rawBits)
                {
                    v = Vector3BE96(c.Bit);
                    c.Bit += 96;
                    c.SegRange += 6; // raw bit rates have unused range data
                    skipClip = true;
                }
                else
                {
                    float invMax = 1.0f / ((1 << bits) - 1);
                    v = new Vector3(
                        BitsBE(c.Bit, bits),
                        BitsBE(c.Bit + bits, bits),
                        BitsBE(c.Bit + bits * 2, bits)) * invMax;
                    c.Bit += bits * 3;
                    skipClip = false;

                    if (hasSegments)
                    {
                        int s = c.SegRange;
                        var mn = new Vector3(_d[s], _d[s + 1], _d[s + 2]) * (1.0f / 255.0f);
                        var ex = new Vector3(_d[s + 3], _d[s + 4], _d[s + 5]) * (1.0f / 255.0f);
                        c.SegRange += 6;
                        v = v * ex + mn;
                    }
                }

                if (!skipClip)
                {
                    var mn = new Vector3(F32(clip), F32(clip + 4), F32(clip + 8));
                    var ex = new Vector3(F32(clip + 12), F32(clip + 16), F32(clip + 20));
                    v = v * ex + mn;
                }
                clip += 24;

                outValues[i] = v;
            }

            return c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector4 ReconstructW(Vector4 v)
        {
            // quat_from_positive_w + normalize
            float w2 = 1.0f - v.X * v.X - v.Y * v.Y - v.Z * v.Z;
            v.W = MathF.Sqrt(MathF.Abs(w2));
            float lenSq = v.X * v.X + v.Y * v.Y + v.Z * v.Z + v.W * v.W;
            if (lenSq > 0.0f)
                v *= 1.0f / MathF.Sqrt(lenSq);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion LerpQuat(Vector4 start, Vector4 end, float alpha)
        {
            // Shortest path lerp + normalize
            if (Vector4.Dot(start, end) < 0.0f)
                end = -end;

            Vector4 r = start + (end - start) * alpha;
            float lenSq = r.LengthSquared();
            if (lenSq > 0.0f)
                r *= 1.0f / MathF.Sqrt(lenSq);
            return new Quaternion(r.X, r.Y, r.Z, r.W);
        }

        //////////////////////////////////////////////////////////////////////////
        // Scalar (float1f) decompression

        public bool DecompressFloats(float sampleTime, float[] output)
        {
            if (!IsValid || TrackType != TrackTypeFloat1F || output == null || output.Length < NumTracks)
                return false;

            int numTracks = (int)NumTracks;
            if (numTracks == 0)
                return true;
            if (NumSamples == 0)
                return false;

            bool wrap = WrapOptimized;
            float clampedTime = Math.Clamp(sampleTime, 0.0f, FiniteDuration);
            FindSamples(clampedTime, wrap, out uint key0, out uint key1, out float alpha);

            byte[] bitRateTable = Version == Version_02_00_00 ? ScalarBitRatesV0 : ScalarBitRatesV1;

            int bitOffset0 = _scalarAnimOff * 8 + (int)(key0 * _numBitsPerFrame);
            int bitOffset1 = _scalarAnimOff * 8 + (int)(key1 * _numBitsPerFrame);
            int constOff = _scalarConstOff;
            int rangeOff = _scalarRangeOff;

            for (int t = 0; t < numTracks; t++)
            {
                int bitRate = _d[_scalarMetaOff + t];
                if (bitRate >= bitRateTable.Length)
                    return false;
                int bits = bitRateTable[bitRate];

                if (bits == 0)
                {
                    output[t] = F32(constOff);
                    constOff += 4;
                }
                else
                {
                    float v0, v1;
                    if (bits == 32)
                    {
                        v0 = FloatBE(bitOffset0);
                        v1 = FloatBE(bitOffset1);
                    }
                    else
                    {
                        float invMax = 1.0f / ((1 << bits) - 1);
                        v0 = BitsBE(bitOffset0, bits) * invMax;
                        v1 = BitsBE(bitOffset1, bits) * invMax;

                        float mn = F32(rangeOff);
                        float ex = F32(rangeOff + 4);
                        rangeOff += 8;
                        v0 = v0 * ex + mn;
                        v1 = v1 * ex + mn;
                    }

                    output[t] = v0 + (v1 - v0) * alpha;
                    bitOffset0 += bits;
                    bitOffset1 += bits;
                }
            }

            return true;
        }
    }
}
