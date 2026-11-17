using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RamOptimiser.Gui
{
    internal static class GuiNative
    {
        public const int ERROR_ALREADY_EXISTS = 183;
        public const int ERROR_ACCESS_DENIED = 5;
        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const uint SDDL_REVISION_1 = 1;
        public const uint GR_GDIOBJECTS = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle CreateMutex(ref SECURITY_ATTRIBUTES attributes,
            [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle CreateEvent(ref SECURITY_ATTRIBUTES attributes,
            [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle OpenEvent(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEvent(SafeWaitHandle handle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
            out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr mem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern uint GetGuiResources(IntPtr process, uint flags);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        /// <summary>(SIZE_T)-1 for both sizes removes as many pages as possible from the working set.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
    }
}
