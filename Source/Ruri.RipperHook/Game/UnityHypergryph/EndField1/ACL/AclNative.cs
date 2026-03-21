using System;
using System.Runtime.InteropServices;

namespace Ruri.RipperHook.Endfield.ACL
{
    [StructLayout(LayoutKind.Sequential)]
    public struct AclTransform
    {
        public System.Numerics.Quaternion Rotation;
        public System.Numerics.Vector3 Translation;
        public System.Numerics.Vector3 Scale;
    }

    public static class AclNative
    {
        private const string DllName = "ACL.EndField";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AclValidateCompressedTracks(IntPtr compressedData);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint AclGetNumTracks(IntPtr compressedData);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern float AclGetDuration(IntPtr compressedData);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AclDecompressTransforms(IntPtr compressedData, float sampleTime, [Out] AclTransform[] outTransforms, uint numTracks);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AclDecompressFloats(IntPtr compressedData, float sampleTime, [Out] float[] outFloats, uint numTracks);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint AclGetNumFloatTracks(IntPtr compressedData);
    }
}
