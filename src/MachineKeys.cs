using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;

namespace RamOptimiser
{
    /// <summary>One access control entry, as MachineKeyTrust sees it.</summary>
    public struct KeyAce
    {
        /// <summary>An entry that can grant rights. Deny entries only take rights away, so the check skips them.</summary>
        public bool Allow;
        /// <summary>The SID as text ("S-1-5-32-544"); null for an entry whose kind is not understood.</summary>
        public string Sid;
        public int Mask;
        /// <summary>The entry applies only to subkeys created later, not to this key.</summary>
        public bool InheritOnly;

        public KeyAce(bool allow, string sid, int mask, bool inheritOnly)
        {
            Allow = allow;
            Sid = sid;
            Mask = mask;
            InheritOnly = inheritOnly;
        }
    }

    /// <summary>
    /// Whether an HKLM key of this app can be trusted by the elevated app: only administrators may have been able to
    /// write it. Pure; the registry I/O is in AppRegistry and MachineRegistry.
    /// </summary>
    public static class MachineKeyTrust
    {
        public const string AdministratorsSid = "S-1-5-32-544", SystemSid = "S-1-5-18", CreatorOwnerSid = "S-1-3-0";

        /// <summary>
        /// The descriptor HKLM\SOFTWARE\RamOptimiser gets when the app creates it: owner Administrators; a protected
        /// DACL (P), so nothing is inherited from SOFTWARE; SYSTEM and Administrators full control and Users read,
        /// each inherited by the subkeys (CI), which are created without a descriptor of their own.
        /// </summary>
        public const string Sddl = "O:BAG:SYD:PAI(A;CI;KA;;;SY)(A;CI;KA;;;BA)(A;CI;KR;;;BU)";

        /// <summary>
        /// Rights that let their holder change what the elevated app reads: set a value (0x2), create a subkey (0x4)
        /// or a link (0x20), delete the key, change its permissions or owner, and the generic and maximum-allowed
        /// rights that can include them.
        /// </summary>
        public const int ChangeRights = 0x0002 | 0x0004 | 0x0020 | 0x00010000 | 0x00040000 | 0x00080000
            | 0x02000000 | 0x10000000 | 0x40000000;

        /// <summary>
        /// Null when the key is safe to trust: Administrators or SYSTEM own it, and no allow entry gives anyone else
        /// a ChangeRights right. Otherwise the reason, as a clause ("it is owned by ..."). dacl null is a missing or
        /// NULL DACL, which lets anyone change the key. Inherit-only entries count too (they reach the subkeys),
        /// except CREATOR OWNER's: it stands for whoever creates a subkey, and that subkey's own owner is checked
        /// when it is opened.
        /// </summary>
        public static string Problem(string ownerSid, IList<KeyAce> dacl)
        {
            if (!IsAdmin(ownerSid))
                return "it is owned by " + (string.IsNullOrEmpty(ownerSid) ? "an unknown account" : ownerSid) + ", not Administrators or SYSTEM";
            if (dacl == null) return "it has no access list, so anyone can change it";
            foreach (KeyAce ace in dacl)
            {
                if (!ace.Allow || (ace.Mask & ChangeRights) == 0 || IsAdmin(ace.Sid)) continue;
                if (ace.InheritOnly && Same(ace.Sid, CreatorOwnerSid)) continue;
                return "its permissions let " + (ace.Sid ?? "an unknown entry") + " change it";
            }
            return null;
        }

        /// <summary>Problem for a self-relative binary security descriptor holding at least the owner and the DACL.</summary>
        public static string ProblemOfDescriptor(byte[] descriptor)
        {
            if (descriptor == null || descriptor.Length == 0) return "its permissions could not be read";
            RawSecurityDescriptor sd;
            try { sd = new RawSecurityDescriptor(descriptor, 0); }
            catch (ArgumentException) { return "its permissions could not be read"; }
            return Problem(sd.Owner == null ? null : sd.Owner.Value, AcesOf(sd));
        }

        /// <summary>
        /// The DACL's entries; null when the descriptor has no DACL or a NULL one. An entry of a kind the framework
        /// cannot read is taken as granting everything to an unknown SID, so it fails the check.
        /// </summary>
        public static List<KeyAce> AcesOf(RawSecurityDescriptor sd)
        {
            if (sd == null || (sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || sd.DiscretionaryAcl == null) return null;
            var list = new List<KeyAce>();
            foreach (GenericAce ace in sd.DiscretionaryAcl)
            {
                bool inheritOnly = (ace.AceFlags & AceFlags.InheritOnly) != 0;
                var known = ace as KnownAce;
                if (known == null) { list.Add(new KeyAce(true, null, -1, inheritOnly)); continue; }
                var qualified = ace as QualifiedAce;
                bool allow = qualified == null || qualified.AceQualifier != AceQualifier.AccessDenied;
                list.Add(new KeyAce(allow, known.SecurityIdentifier.Value, known.AccessMask, inheritOnly));
            }
            return list;
        }

        static bool IsAdmin(string sid)
        {
            return Same(sid, AdministratorsSid) || Same(sid, SystemSid);
        }

        static bool Same(string a, string b)
        {
            return a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A registry key opened without following a link: when the key is itself a link, this is the link key, which
    /// holds the SymbolicLinkValue value.
    /// </summary>
    public interface IMachineKey : IDisposable
    {
        /// <summary>The key's kernel name (\REGISTRY\MACHINE\...); null when it could not be read.</summary>
        string KernelPath { get; }
        /// <summary>Owner and DACL, as a self-relative security descriptor.</summary>
        byte[] ReadSecurity();
        string[] ValueNames();
        /// <summary>The value's text, or null when it is missing or not text.</summary>
        string GetString(string name);
        void SetString(string name, string value);
        /// <summary>No error when the value is missing.</summary>
        void DeleteValue(string name);
        int SubKeyCount { get; }
        /// <summary>Deletes this key through its own handle; it must hold no values or subkeys.</summary>
        void DeleteKey();
    }

    /// <summary>
    /// HKLM access for AppRegistry, behind an interface so tests use an in-memory registry. Access denied is an
    /// UnauthorizedAccessException; other failures are IOException.
    /// </summary>
    public interface IMachineRegistry
    {
        /// <summary>HKLM\SOFTWARE itself: Windows' own key, opened for reading.</summary>
        IMachineKey OpenSoftware();
        /// <summary>One subkey of parent, without following a link (REG_OPTION_OPEN_LINK); null when it does not exist.</summary>
        IMachineKey OpenChild(IMachineKey parent, string name, bool writable);
        /// <summary>Creates one subkey of parent, with the descriptor in sddl, or inheriting from parent when sddl is null.</summary>
        void CreateChild(IMachineKey parent, string name, string sddl);
    }

    /// <summary>
    /// The app's machine-wide records under HKLM\SOFTWARE\RamOptimiser: the Startup tab's ServiceStartup journal and
    /// Power Clean's PowerClean\&lt;SID&gt; journal. The elevated app acts on what they say (it changes start types and
    /// starts services), so it trusts a key only when no one but administrators can have written it.
    ///
    /// Every key from RamOptimiser down is opened one level at a time from its checked parent's handle, without
    /// following a link, and refused when: it is a registry link (it holds SymbolicLinkValue); its kernel name is
    /// not the expected HKLM\SOFTWARE path (a redirection anywhere above it); or MachineKeyTrust says its owner or
    /// permissions let a non-administrator change it. Walking with REG_OPTION_OPEN_LINK and checking each level is
    /// used rather than one open of the whole path plus a final-name check, because a whole-path open (and a
    /// whole-path create) would follow a link before anything is checked, and create keys at its target.
    /// RamOptimiser itself is created with MachineKeyTrust.Sddl; the keys below inherit from it.
    /// </summary>
    public static class AppRegistry
    {
        public const string RootName = "RamOptimiser";
        public const string LinkValueName = "SymbolicLinkValue";
        const string SoftwareKernelPath = @"\REGISTRY\MACHINE\SOFTWARE";

        public static readonly IMachineRegistry Machine = new MachineRegistry();

        /// <summary>Open on the real registry.</summary>
        public static IMachineKey Open(IList<string> path, bool writable, bool create, out string problem)
        {
            return Open(Machine, path, writable, create, out problem);
        }

        /// <summary>
        /// Opens HKLM\SOFTWARE\RamOptimiser\path (path holds one key name per level), checking every key from
        /// RamOptimiser down. With create, missing keys are created. Returns null with problem null when a key is
        /// missing (and create is false); null with problem set when a key is refused or the registry fails.
        /// Throws UnauthorizedAccessException when Windows refuses the access (not elevated), so callers can say so.
        /// Only the last key is opened writable.
        /// </summary>
        public static IMachineKey Open(IMachineRegistry registry, IList<string> path, bool writable, bool create, out string problem)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            if (path == null) throw new ArgumentNullException("path");
            var names = new List<string> { RootName };
            foreach (string n in path)
            {
                if (string.IsNullOrEmpty(n) || n.IndexOf('\\') >= 0) throw new ArgumentException("A key name is empty or holds a backslash.", "path");
                names.Add(n);
            }
            problem = null;
            string expected = SoftwareKernelPath;
            IMachineKey current = null;
            try
            {
                current = registry.OpenSoftware();
                for (int i = 0; i < names.Count; i++)
                {
                    bool last = i == names.Count - 1;
                    expected += @"\" + names[i];
                    IMachineKey child = registry.OpenChild(current, names[i], last && writable);
                    if (child == null)
                    {
                        if (!create) return null;
                        registry.CreateChild(current, names[i], i == 0 ? MachineKeyTrust.Sddl : null);
                        // Opened again like an existing key: whatever is there now is checked before use.
                        child = registry.OpenChild(current, names[i], last && writable);
                        if (child == null) { problem = Display(expected) + " could not be created"; return null; }
                    }
                    current.Dispose();
                    current = child;
                    string why = KeyProblem(child, expected);
                    if (why != null) { problem = Display(expected) + " is not used: " + why; return null; }
                }
                IMachineKey result = current;
                current = null;
                return result;
            }
            catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
            catch (IOException ex) { problem = Display(expected) + " could not be opened: " + ex.Message.TrimEnd('.'); return null; }
            catch (Win32Exception ex) { problem = Display(expected) + " could not be opened: " + ex.Message.TrimEnd('.'); return null; }
            finally { if (current != null) current.Dispose(); }
        }

        /// <summary>Why one opened key is refused, or null. expectedPath is its kernel name.</summary>
        internal static string KeyProblem(IMachineKey key, string expectedPath)
        {
            foreach (string n in key.ValueNames())
                if (string.Equals(n, LinkValueName, StringComparison.OrdinalIgnoreCase)) return "it is a registry link";
            string actual = key.KernelPath;
            if (!string.Equals(actual, expectedPath, StringComparison.OrdinalIgnoreCase))
                return "it resolves to " + (actual ?? "an unknown place") + ", not " + Display(expectedPath);
            return MachineKeyTrust.ProblemOfDescriptor(key.ReadSecurity());
        }

        /// <summary>\REGISTRY\MACHINE\SOFTWARE\x as HKLM\SOFTWARE\x, for messages.</summary>
        static string Display(string kernelPath)
        {
            return "HKLM" + kernelPath.Substring(@"\REGISTRY\MACHINE".Length);
        }
    }
}
