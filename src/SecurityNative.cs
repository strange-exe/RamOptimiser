using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RamOptimiser
{
    /// <summary>Win32 calls for ownership and link checks (single instance, safe user folder, clean log).</summary>
    internal static class SecurityNative
    {
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint READ_CONTROL = 0x00020000;
        public const int SE_KERNEL_OBJECT = 6;
        public const uint OWNER_SECURITY_INFORMATION = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle OpenMutex(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        /// <summary>Returns a Win32 error code (0 on success); securityDescriptor is freed with LocalFree.</summary>
        [DllImport("advapi32.dll")]
        public static extern uint GetSecurityInfo(SafeWaitHandle handle, int objectType, uint securityInfo,
            out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
    }
}
