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

        public const uint GENERIC_WRITE = 0x40000000, FILE_LIST_DIRECTORY = 0x1, FILE_READ_ATTRIBUTES = 0x80;
        public const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
        public const uint OPEN_EXISTING = 3, OPEN_ALWAYS = 4;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        public const uint MOVEFILE_REPLACE_EXISTING = 0x1;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// <summary>A rename that replaces the target in one step, so the target is never deleted first.</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileExW(string existing, string replacement, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);

        /// <summary>The handle's normalised path with its drive letter ("\\?\C:\..."), or null.</summary>
        public static string FinalPath(SafeFileHandle h)
        {
            var sb = new System.Text.StringBuilder(32768);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            return n == 0 || n >= sb.Capacity ? null : sb.ToString();
        }
    }
}
