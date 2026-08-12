using System;

namespace Ruri.ACL
{
    public class AclFloatDecompressor
    {
        private readonly AclCompressedTracks _tracks;

        public bool IsValid { get; }
        public uint NumTracks { get; }
        public float Duration { get; }

        public AclFloatDecompressor(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
            {
                IsValid = false;
                return;
            }

            _tracks = new AclCompressedTracks(compressedData);
            IsValid = _tracks.IsValid;
            NumTracks = _tracks.IsValid ? _tracks.NumTracks : 0;
            Duration = _tracks.IsValid ? _tracks.FiniteDuration : 0.0f;
        }

        public bool Sample(float time, out float[] values)
        {
            values = null;
            if (NumTracks == 0)
                return false;

            values = new float[NumTracks];
            return _tracks.DecompressFloats(time, values);
        }

        public void Dispose()
        {
        }
    }
}
