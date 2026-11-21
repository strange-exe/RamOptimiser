using System;
using System.Runtime.InteropServices;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    /// <summary>
    /// Sets this process's COM security (ComSecurityPolicy) once, first thing in Main, before WPF or anything else
    /// marshals an interface: COM takes the registry defaults on first use otherwise, and the call can be made only
    /// once per process. A failure is logged and the app runs on with the defaults.
    /// </summary>
    static class ComSecurity
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool MakeAbsoluteSD(IntPtr selfRelative, IntPtr absolute, ref uint absoluteSize, IntPtr dacl, ref uint daclSize,
            IntPtr sacl, ref uint saclSize, IntPtr owner, ref uint ownerSize, IntPtr group, ref uint groupSize);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        [DllImport("ole32.dll")]
        static extern int CoInitializeSecurity(IntPtr secDesc, int authSvcCount, IntPtr authSvc, IntPtr reserved1,
            uint authnLevel, uint impLevel, IntPtr authList, uint capabilities, IntPtr reserved3);

        const uint SddlRevision1 = 1;

        /// <summary>True once CoInitializeSecurity took the policy. The Explorer route of the update page relies on it.</summary>
        public static bool Applied { get; private set; }

        public static void Apply()
        {
            try
            {
                IntPtr absolute = AbsoluteDescriptor();
                int hr = absolute == IntPtr.Zero ? Marshal.GetHRForLastWin32Error()
                    : CoInitializeSecurity(absolute, -1, IntPtr.Zero, IntPtr.Zero, ComSecurityPolicy.AuthnLevel,
                        ComSecurityPolicy.ImpLevel, IntPtr.Zero, ComSecurityPolicy.Capabilities, IntPtr.Zero);
                Applied = absolute != IntPtr.Zero && hr >= 0;
                // A failed descriptor is never replaced by NULL: that would let any caller in.
                string line = ComSecurityPolicy.LogLine(hr < 0 ? hr : 0);
                if (line != null) ErrorLog.TryWrite(ErrorLog.DefaultPath, line, null);
            }
            catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "could not set COM security", ex); }
        }

        /// <summary>
        /// The policy's descriptor in the absolute form CoInitializeSecurity takes, or IntPtr.Zero (last error set).
        /// Its memory is never freed: COM may keep using it for the life of the process.
        /// </summary>
        static IntPtr AbsoluteDescriptor()
        {
            IntPtr relative;
            uint relativeSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(ComSecurityPolicy.AccessSddl, SddlRevision1, out relative, out relativeSize))
                return IntPtr.Zero;
            try
            {
                uint sdSize = 0, daclSize = 0, saclSize = 0, ownerSize = 0, groupSize = 0;
                // First call: sizes only (fails with ERROR_INSUFFICIENT_BUFFER).
                MakeAbsoluteSD(relative, IntPtr.Zero, ref sdSize, IntPtr.Zero, ref daclSize, IntPtr.Zero, ref saclSize,
                    IntPtr.Zero, ref ownerSize, IntPtr.Zero, ref groupSize);
                if (sdSize == 0) return IntPtr.Zero;
                IntPtr sd = Alloc(sdSize), dacl = Alloc(daclSize), sacl = Alloc(saclSize), owner = Alloc(ownerSize), group = Alloc(groupSize);
                if (!MakeAbsoluteSD(relative, sd, ref sdSize, dacl, ref daclSize, sacl, ref saclSize, owner, ref ownerSize, group, ref groupSize))
                    return IntPtr.Zero;
                return sd;
            }
            finally { LocalFree(relative); }
        }

        static IntPtr Alloc(uint size)
        {
            return size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)size);
        }
    }
}
