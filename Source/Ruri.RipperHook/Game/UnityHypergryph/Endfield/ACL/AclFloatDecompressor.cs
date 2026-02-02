using System;
using System.Runtime.InteropServices;

namespace Ruri.RipperHook.Endfield.ACL
{
    /// <summary>
    /// Decompressor for ACL scalar/float tracks (used for humanoid muscle curves)
    /// </summary>
    public class AclFloatDecompressor
    {
        private IntPtr _rawBuffer;
        private IntPtr _alignedBuffer;
        
        public bool IsValid { get; private set; }
        public uint NumTracks { get; private set; }
        public float Duration { get; private set; }

        public AclFloatDecompressor(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
            {
                IsValid = false;
                return;
            }

            // Allocate unmanaged memory with room for alignment
            _rawBuffer = Marshal.AllocHGlobal(compressedData.Length + 16);
            
            // Align to 16 bytes
            long ptr = _rawBuffer.ToInt64();
            long aligned = (ptr + 15) & ~15;
            _alignedBuffer = new IntPtr(aligned);
            
            // Copy data to aligned pointer
            Marshal.Copy(compressedData, 0, _alignedBuffer, compressedData.Length);
            
            // Validate and read metadata from aligned pointer
            IsValid = AclNative.AclValidateCompressedTracks(_alignedBuffer);
            NumTracks = AclNative.AclGetNumFloatTracks(_alignedBuffer);
            Duration = AclNative.AclGetDuration(_alignedBuffer);
        }

        public bool Sample(float time, out float[] values)
        {
            values = null;
            if (NumTracks == 0) return false;

            values = new float[NumTracks];
            
            return AclNative.AclDecompressFloats(_alignedBuffer, time, values, NumTracks);
        }

        public void Dispose()
        {
            if (_rawBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_rawBuffer);
                _rawBuffer = IntPtr.Zero;
                _alignedBuffer = IntPtr.Zero;
            }
        }

        ~AclFloatDecompressor()
        {
            Dispose();
        }
    }
}
