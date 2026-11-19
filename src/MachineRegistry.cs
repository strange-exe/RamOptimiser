using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RamOptimiser
{
    /// <summary>
    /// IMachineRegistry on the real HKLM (64-bit view). Subkeys are opened one level at a time with
    /// REG_OPTION_OPEN_LINK, so a link key is opened as itself, never followed
    /// (learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regopenkeyexw); created one level at a time
    /// with RegCreateKeyExW and an optional descriptor (nf-winreg-regcreatekeyexw); and deleted through their own
    /// handle with NtDeleteKey (the user-mode name of ZwDeleteKey, learn.microsoft.com/en-us/windows-hardware/drivers/
    /// ddi/wdm/nf-wdm-zwdeletekey), so a delete never resolves a name again.
    /// </summary>
    public sealed class MachineRegistry : IMachineRegistry
    {
        const uint KEY_READ = 0x20019, KEY_SET_VALUE = 0x0002, DELETE = 0x00010000, KEY_WOW64_64KEY = 0x0100;
        const uint REG_OPTION_NON_VOLATILE = 0, REG_OPTION_OPEN_LINK = 0x0008;
        const uint OWNER_SECURITY_INFORMATION = 0x1, DACL_SECURITY_INFORMATION = 0x4;
        const int ErrorSuccess = 0, ErrorFileNotFound = 2, ErrorAccessDenied = 5, ErrorInsufficientBuffer = 122;
        static readonly IntPtr HkeyLocalMachine = new IntPtr(unchecked((int)0x80000002));

        public IMachineKey OpenSoftware() { return OpenMachineKey("SOFTWARE"); }

        /// <summary>A key directly under HKLM, opened for reading the normal way (Windows' own keys).</summary>
        internal static IMachineKey OpenMachineKey(string name)
        {
            IntPtr h;
            using (var hklm = new SafeRegistryHandle(HkeyLocalMachine, false))
                Check(RegOpenKeyExW(hklm, name, 0, KEY_READ | KEY_WOW64_64KEY, out h), "HKLM\\" + name);
            return new Key(new SafeRegistryHandle(h, true));
        }

        public IMachineKey OpenChild(IMachineKey parent, string name, bool writable)
        {
            uint access = KEY_READ | KEY_WOW64_64KEY | (writable ? KEY_SET_VALUE | DELETE : 0);
            IntPtr h;
            int err = RegOpenKeyExW(Handle(parent), name, REG_OPTION_OPEN_LINK, access, out h);
            if (err == ErrorFileNotFound) return null;
            Check(err, name);
            return new Key(new SafeRegistryHandle(h, true));
        }

        public void CreateChild(IMachineKey parent, string name, string sddl)
        {
            SECURITY_ATTRIBUTES sa = null;
            GCHandle pin = default(GCHandle);
            try
            {
                if (sddl != null)
                {
                    var sd = new RawSecurityDescriptor(sddl);
                    var bytes = new byte[sd.BinaryLength];
                    sd.GetBinaryForm(bytes, 0);
                    pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), lpSecurityDescriptor = pin.AddrOfPinnedObject() };
                }
                IntPtr h;
                uint disposition;
                Check(RegCreateKeyExW(Handle(parent), name, 0, null, REG_OPTION_NON_VOLATILE, KEY_READ | KEY_WOW64_64KEY, sa, out h, out disposition), name);
                // Only the key is wanted here; AppRegistry opens it again without following a link and checks it.
                new SafeRegistryHandle(h, true).Dispose();
            }
            finally { if (pin.IsAllocated) pin.Free(); }
        }

        static SafeRegistryHandle Handle(IMachineKey key)
        {
            var k = key as Key;
            if (k == null) throw new ArgumentException("Not a key from MachineInner.", "key");
            return k.Inner.Handle;
        }

        static void Check(int err, string what)
        {
            if (err == ErrorSuccess) return;
            string message = new Win32Exception(err).Message.TrimEnd('.');
            if (err == ErrorAccessDenied) throw new UnauthorizedAccessException(what + ": " + message);
            throw new IOException(what + ": " + message);
        }

        sealed class Key : IMachineKey
        {
            public readonly RegistryKey Inner;

            // FromHandle keeps the handle and closes it on Dispose; the key it returns allows writes (the handle's own
            // access decides). .NET Framework reference source, RegistryKey.FromHandle.
            public Key(SafeRegistryHandle handle) { Inner = RegistryKey.FromHandle(handle, RegistryView.Registry64); }

            public string KernelPath { get { return StartupApps.KernelPath(Inner); } }

            public byte[] ReadSecurity()
            {
                uint size = 256;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    var buffer = new byte[size];
                    uint got = size;
                    int err = RegGetKeySecurity(Inner.Handle, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, buffer, ref got);
                    if (err == ErrorSuccess) return buffer;
                    if (err != ErrorInsufficientBuffer) Check(err, "the key's permissions");
                    size = Math.Max(got, size * 2);
                }
                throw new IOException("the key's permissions could not be read");
            }

            public string[] ValueNames() { return Inner.GetValueNames(); }

            public string GetString(string name)
            {
                return Inner.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }

            public void SetString(string name, string value) { Inner.SetValue(name, value, RegistryValueKind.String); }

            public void DeleteValue(string name) { Inner.DeleteValue(name, false); }

            public int SubKeyCount { get { return Inner.SubKeyCount; } }

            public void DeleteKey()
            {
                int status = NtDeleteKey(Inner.Handle);
                if (status == StatusAccessDenied) throw new UnauthorizedAccessException("the key could not be deleted: access denied");
                if (status != 0) throw new IOException("the key could not be deleted (NTSTATUS 0x" + status.ToString("X8") + ")");
            }

            public void Dispose() { Inner.Dispose(); }
        }

        const int StatusAccessDenied = unchecked((int)0xC0000022);

        [StructLayout(LayoutKind.Sequential)]
        sealed class SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, uint access, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegCreateKeyExW(SafeRegistryHandle key, string subKey, int reserved, string cls, uint options, uint access,
            SECURITY_ATTRIBUTES security, out IntPtr result, out uint disposition);

        [DllImport("advapi32.dll")]
        static extern int RegGetKeySecurity(SafeRegistryHandle key, uint information, byte[] descriptor, ref uint size);

        [DllImport("ntdll.dll")]
        static extern int NtDeleteKey(SafeRegistryHandle key);
    }
}
