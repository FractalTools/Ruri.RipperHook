using System;

namespace Ruri.ACL
{
    /// <summary>
    /// Decompressor for ACL transform (qvvf) tracks. Pure C# implementation, no native dependency.
    /// </summary>
    public class AclDecompressor
    {
        private readonly AclCompressedTracks _tracks;

        public bool IsValid { get; }
        public uint NumTracks { get; }
        public float Duration { get; }

        public AclDecompressor(byte[] compressedData)
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

        public bool Sample(float time, out AclTransform[] transforms)
        {
            transforms = null;
            if (NumTracks == 0)
                return false;

            transforms = new AclTransform[NumTracks];
            return _tracks.DecompressTransforms(time, transforms);
        }

        public void Dispose()
        {
            // Managed implementation, nothing to release. Kept for API compatibility.
        }
    }
}
