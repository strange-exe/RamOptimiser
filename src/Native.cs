// P/Invoke declarations. Documented Win32 APIs are used where one exists
// (GlobalMemoryStatusEx, GetPerformanceInfo, SetSystemFileCacheSize, AdjustTokenPrivileges).
// The memory-list, registry and page-combining operations have no documented API; they use
// NtSetSystemInformation with information classes and structs from the public phnt headers
// (https://github.com/winsiderss/phnt, ntexapi.h). Those are private and may change.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Every DllImport in this assembly (ramopt.exe, RamOptimiser.exe and the test runner all compile this file)
// loads its DLL from System32 only, never from the exe's folder, the current folder or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RamOptimiser
{
    internal static class Native
    {
        public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDefaultDllDirectories(uint directoryFlags);

        /// <summary>
        /// Called first in each Main: later LoadLibrary calls by name (ours, the runtime's, WPF's) search
        /// System32 only, so a DLL planted next to the exe is not picked up. False when the call is
        /// unavailable or fails; the app then runs with the default search order.
        /// </summary>
        public static bool RestrictDllSearchToSystem32()
        {
            try { return SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32); }
            catch (EntryPointNotFoundException) { return false; }
        }

        // SYSTEM_INFORMATION_CLASS values (phnt ntexapi.h).
        public const int SystemMemoryListInformation = 80;
        public const int SystemCombinePhysicalMemoryInformation = 130;
        public const int SystemRegistryReconciliationInformation = 155;

        // SYSTEM_MEMORY_LIST_COMMAND (phnt ntexapi.h).
        public const int MemoryEmptyWorkingSets = 2;
        public const int MemoryFlushModifiedList = 3;
        public const int MemoryPurgeStandbyList = 4;
        public const int MemoryPurgeLowPriorityStandbyList = 5;

        public const int STATUS_SUCCESS = 0;
        public const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);
        public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
        public const int ERROR_NOT_ALL_ASSIGNED = 1300;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        // Memory resource notifications (documented, memoryapi.h): Windows' own low and high memory signals, at fixed
        // kernel levels. Create returns NULL on failure; SafeWaitHandle treats 0 and -1 as invalid. Read-only.
        public const int LowMemoryResourceNotification = 0, HighMemoryResourceNotification = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeWaitHandle CreateMemoryResourceNotification(int notificationType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryMemoryResourceNotification(SafeWaitHandle handle, out int resourceState);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        // SYSTEM_MEMORY_LIST_INFORMATION (phnt): all SIZE_T, counts are in pages.
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public UIntPtr ZeroPageCount;
            public UIntPtr FreePageCount;
            public UIntPtr ModifiedPageCount;
            public UIntPtr ModifiedNoWritePageCount;
            public UIntPtr BadPageCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public UIntPtr[] PageCountByPriority;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public UIntPtr[] RepurposedPagesByPriority;
            public UIntPtr ModifiedPageCountPageFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_COMBINE_INFORMATION_EX
        {
            public IntPtr Handle;
            public UIntPtr PagesCombined;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        public const uint SE_PRIVILEGE_ENABLED = 0x2;
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        public const uint TOKEN_QUERY = 0x8;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSystemFileCacheSize(IntPtr minimum, IntPtr maximum, uint flags);

        // Volume flush (documented: CreateFileW on \\.\X: and FlushFileBuffers on the volume handle).
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle handle);

        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("ntdll.dll")]
        public static extern int RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(string systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
            uint bufferLength, IntPtr previousState, IntPtr returnLength);
    }
}
