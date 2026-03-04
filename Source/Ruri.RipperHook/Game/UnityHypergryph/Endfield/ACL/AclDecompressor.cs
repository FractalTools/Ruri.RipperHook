using Ruri.SourceGenerated.Subclasses.AnimClipAclCompressedBuffer;
using System;
using System.Runtime.InteropServices;
using AssetRipper.IO.Endian;

namespace Ruri.RipperHook.Endfield.ACL
{
    public class AclDecompressor
    {
        private IntPtr _rawBuffer;
        private IntPtr _alignedBuffer;
        
        public bool IsValid { get; private set; }
        public uint NumTracks { get; private set; }
        public float Duration { get; private set; }

        public AclDecompressor(byte[] compressedData)
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
            NumTracks = AclNative.AclGetNumTracks(_alignedBuffer);
            Duration = AclNative.AclGetDuration(_alignedBuffer);
        }

        public bool Sample(float time, out AclTransform[] transforms)
        {
            transforms = null;
            if (NumTracks == 0) return false;

            transforms = new AclTransform[NumTracks];
            
            // Use aligned pointer
            return AclNative.AclDecompressTransforms(_alignedBuffer, time, transforms, NumTracks);
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

        ~AclDecompressor()
        {
            Dispose();
        }
    }
}
