using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RamOptimiser.Gui
{
    /// <summary>
    /// Checks a per-user folder (and files in it) before an elevated instance reads, writes, moves or deletes
    /// there. %LOCALAPPDATA%\RamOptimiser belongs to the non-elevated user, so a process of that user could
    /// swap the folder for a junction or plant links that an elevated File.Replace/Move/Delete would follow.
    ///
    /// Refused: a folder that is a reparse point (junction, symlink, mount point) or is not a directory; a
    /// folder whose owner is not the current user, Administrators or SYSTEM; a file that is a reparse point
    /// or has more than one hard link. Missing items pass: there is nothing to follow.
    ///
    /// The check is by path, so a swap right after it still wins the race; callers check again right before
    /// each step that writes, which narrows that window without closing it.
    /// </summary>
    public static class SafeUserFolder
    {
        public const string AdministratorsSid = "S-1-5-32-544";
        public const string LocalSystemSid = "S-1-5-18";

        /// <summary>Throws IOException naming the problem; returns quietly when everything checks out.</summary>
        public static void Check(string folder, params string[] files)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("A folder is required.", "folder");
            string problem = Problem(folder, files);
            if (problem != null) throw new IOException(problem);
        }

        /// <summary>The same checks as Check, as a bool: false for any problem, including one reading the folder.</summary>
        public static bool IsSafe(string folder, params string[] files)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            try { return Problem(folder, files) == null; }
            catch (Exception) { return false; }
        }

        /// <summary>The owners an elevated instance accepts for its folder: the user, Administrators (the owner
        /// of a folder an elevated process created) and SYSTEM.</summary>
        public static bool IsTrustedOwner(string ownerSid, string userSid)
        {
            if (string.IsNullOrEmpty(ownerSid) || string.IsNullOrEmpty(userSid)) return false;
            return string.Equals(ownerSid, userSid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ownerSid, AdministratorsSid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ownerSid, LocalSystemSid, StringComparison.OrdinalIgnoreCase);
        }

        static string Problem(string folder, string[] files)
        {
            FileAttributes attributes;
            if (TryGetAttributes(folder, out attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return "Refusing to use " + folder + ": the folder is a link (junction or symbolic link).";
                if ((attributes & FileAttributes.Directory) == 0)
                    return "Refusing to use " + folder + ": it is not a folder.";
                string owner = OwnerOf(folder);
                if (!IsTrustedOwner(owner, CurrentUserSid()))
                    return "Refusing to use " + folder + ": the folder is owned by " + (owner ?? "an unknown account")
                        + ", not by you, Administrators or SYSTEM.";
            }
            if (files == null) return null;
            foreach (string file in files)
            {
                if (string.IsNullOrEmpty(file) || !TryGetAttributes(file, out attributes)) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return "Refusing to use " + file + ": the file is a link (symbolic link or junction).";
                if ((attributes & FileAttributes.Directory) != 0) continue; // not a file: the caller's own I/O fails
                if (LinkCount(file) > 1)
                    return "Refusing to use " + file + ": the file has more than one hard link.";
            }
            return null;
        }

        /// <summary>The item's own attributes (a link is not followed). False when nothing is there.</summary>
        static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            attributes = 0;
            return false;
        }

        static string OwnerOf(string folder)
        {
            try
            {
                IdentityReference owner = Directory.GetAccessControl(folder, AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier));
                return owner == null ? null : owner.Value;
            }
            catch (UnauthorizedAccessException) { return null; } // includes PrivilegeNotHeldException
            catch (IdentityNotMappedException) { return null; }
        }

        static uint LinkCount(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                SecurityNative.BY_HANDLE_FILE_INFORMATION info;
                if (!SecurityNative.GetFileInformationByHandle(fs.SafeFileHandle, out info))
                    throw new IOException("Could not read the link count of " + file + ".");
                return info.NumberOfLinks;
            }
        }

        static string CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return identity.User.Value;
        }
    }
}
