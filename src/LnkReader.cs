using System;
using System.Text;

namespace RamOptimiser
{
    /// <summary>
    /// Reads the target path and arguments of a shortcut (.lnk) from its bytes, to the documented Shell Link
    /// Binary File Format (MS-SHLLINK, learn.microsoft.com/en-us/openspecs/windows_protocols/ms-shllink).
    /// Deliberately not IShellLink: resolving a link may search folders and volumes, show a dialog and update the
    /// file (learn.microsoft.com/en-us/windows/win32/shell/links), and the elevated app reads a user-writable file.
    /// Every offset is bounds-checked, there is no recursion, and any inconsistency returns false. Pure.
    /// </summary>
    public static class LnkReader
    {
        const int HeaderSize = 0x4C;
        const uint HasLinkTargetIDList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8,
            HasWorkingDir = 0x10, HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80,
            ForceNoLinkInfo = 0x100, HasExpString = 0x200;
        const uint VolumeIDAndLocalBasePath = 0x1;
        const uint EnvironmentVariableDataBlock = 0xA0000001;
        const int EnvironmentBlockSize = 0x314;
        /// <summary>MS-SHLLINK 2.4: each StringData string holds at most 32767 characters (the doc's cap, not u16's).</summary>
        const int MaxStringChars = 32767;

        static readonly byte[] LinkClsid = { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 };

        /// <summary>
        /// True with the program path (and arguments, "" when none) when the bytes are a well-formed shell link whose
        /// LinkInfo has a local path, or, failing that, whose environment-variable block names one (the block comes
        /// first when HasExpString or ForceNoLinkInfo is set). False otherwise. The path is what the file says, not a
        /// verified program: callers must not trust it for anything a user-writable shortcut could abuse.
        /// </summary>
        public static bool TryReadTarget(byte[] data, out string path, out string arguments)
        {
            path = null; arguments = null;
            try
            {
                string target, args;
                if (!Parse(data, out target, out args) || string.IsNullOrEmpty(target)) return false;
                path = target; arguments = args ?? "";
                return true;
            }
            catch (ArgumentException) { return false; } // a decoder refusing bytes: treated like any other inconsistency
        }

        static bool Parse(byte[] d, out string target, out string args)
        {
            target = null; args = null;
            if (d == null || d.Length < HeaderSize) return false;
            if (U32(d, 0) != HeaderSize) return false;
            for (int i = 0; i < 16; i++) if (d[4 + i] != LinkClsid[i]) return false;
            uint flags = U32(d, 20);
            bool unicode = (flags & IsUnicode) != 0;
            int pos = HeaderSize;

            if ((flags & HasLinkTargetIDList) != 0)
            {
                if (!Fits(d, pos, 2)) return false;
                int size = U16(d, pos);
                pos += 2;
                if (!Fits(d, pos, size)) return false;
                pos += size;
            }

            string linkPath = null;
            if ((flags & HasLinkInfo) != 0)
            {
                if (!Fits(d, pos, 8)) return false;
                long infoSize = U32(d, pos);
                if (!Fits(d, pos, infoSize)) return false;
                if (!ReadLinkInfo(d, pos, (int)infoSize, out linkPath)) return false;
                pos += (int)infoSize;
            }

            uint[] order = { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation };
            foreach (uint flag in order)
            {
                if ((flags & flag) == 0) continue;
                if (!Fits(d, pos, 2)) return false;
                int chars = U16(d, pos);
                pos += 2;
                if (chars > MaxStringChars) return false;
                int bytes = unicode ? chars * 2 : chars;
                if (!Fits(d, pos, bytes)) return false;
                string s = unicode ? Encoding.Unicode.GetString(d, pos, bytes) : Encoding.Default.GetString(d, pos, bytes);
                pos += bytes;
                if (flag == HasArguments) args = s;
            }

            // ExtraData: blocks of (u32 size, u32 signature, ...) ending in a TerminalBlock (a size below 4).
            string envTarget = null;
            while (true)
            {
                if (!Fits(d, pos, 4)) return false; // the TerminalBlock is part of the format: a cut file lacks it
                long blockSize = U32(d, pos);
                if (blockSize < 4) break;
                if (blockSize < 8 || !Fits(d, pos, blockSize)) return false;
                if (U32(d, pos + 4) == EnvironmentVariableDataBlock)
                {
                    if (blockSize != EnvironmentBlockSize) return false;
                    string uni = Terminated(d, pos + 8 + 260, 520, true);
                    string ansi = Terminated(d, pos + 8, 260, false);
                    envTarget = !string.IsNullOrEmpty(uni) ? uni : ansi;
                }
                pos += (int)blockSize;
            }

            // LinkInfo is only a hint for finding a moved target (MS-SHLLINK 2.3). HasExpString says the target is the
            // environment block, and ForceNoLinkInfo says LinkInfo is ignored: both put the block first.
            if ((flags & ForceNoLinkInfo) != 0) linkPath = null;
            bool preferEnv = (flags & HasExpString) != 0 && !string.IsNullOrEmpty(envTarget);
            if (!preferEnv && !string.IsNullOrEmpty(linkPath)) target = linkPath;
            else if (!string.IsNullOrEmpty(envTarget)) target = Environment.ExpandEnvironmentVariables(envTarget);
            if (target != null && (target.Length > MaxStringChars || target.IndexOf('\0') >= 0)) return false;
            return true;
        }

        /// <summary>LocalBasePath + CommonPathSuffix, the Unicode pair preferred when the header has room for it.</summary>
        static bool ReadLinkInfo(byte[] d, int start, int size, out string path)
        {
            path = null;
            if (size < 0x1C) return false;
            uint headerSize = U32(d, start + 4);
            if (headerSize < 0x1C || headerSize > size) return false;
            if (headerSize != 0x1C && headerSize < 0x24) return false; // 0x1C, or at least 0x24 with the Unicode offsets
            uint infoFlags = U32(d, start + 8);
            if ((infoFlags & VolumeIDAndLocalBasePath) == 0) return true; // a network-only link: no local path
            uint baseOff = U32(d, start + 16), suffixOff = U32(d, start + 24);
            string basePath, suffix;
            if (headerSize >= 0x24)
            {
                uint uniBase = U32(d, start + 28), uniSuffix = U32(d, start + 32);
                if (uniBase != 0)
                {
                    if (!InfoString(d, start, size, uniBase, true, out basePath)) return false;
                    if (uniSuffix == 0) suffix = "";
                    else if (!InfoString(d, start, size, uniSuffix, true, out suffix)) return false;
                    path = basePath + suffix;
                    return true;
                }
            }
            if (!InfoString(d, start, size, baseOff, false, out basePath)) return false;
            if (suffixOff == 0) suffix = "";
            else if (!InfoString(d, start, size, suffixOff, false, out suffix)) return false;
            path = basePath + suffix;
            return true;
        }

        /// <summary>A NUL-terminated string at an offset inside the LinkInfo; false when it runs past the structure.</summary>
        static bool InfoString(byte[] d, int start, int size, uint offset, bool unicode, out string s)
        {
            s = null;
            if (offset >= (uint)size) return false;
            int at = start + (int)offset, limit = start + size;
            int step = unicode ? 2 : 1;
            for (int i = at; i + step <= limit; i += step)
            {
                bool nul = unicode ? d[i] == 0 && d[i + 1] == 0 : d[i] == 0;
                if (!nul) continue;
                int bytes = i - at;
                if ((unicode ? bytes / 2 : bytes) > MaxStringChars) return false;
                s = unicode ? Encoding.Unicode.GetString(d, at, bytes) : Encoding.Default.GetString(d, at, bytes);
                return true;
            }
            return false;
        }

        /// <summary>The text before the first NUL in a fixed-size field (the whole field when there is none).</summary>
        static string Terminated(byte[] d, int at, int length, bool unicode)
        {
            int step = unicode ? 2 : 1, end = at + length;
            for (int i = at; i + step <= end; i += step)
                if (unicode ? d[i] == 0 && d[i + 1] == 0 : d[i] == 0)
                {
                    end = i;
                    break;
                }
            return unicode ? Encoding.Unicode.GetString(d, at, end - at) : Encoding.Default.GetString(d, at, end - at);
        }

        static bool Fits(byte[] d, long pos, long count)
        {
            return pos >= 0 && count >= 0 && pos + count <= d.Length;
        }

        static int U16(byte[] d, int at) { return d[at] | (d[at + 1] << 8); }

        static uint U32(byte[] d, int at)
        {
            return (uint)(d[at] | (d[at + 1] << 8) | (d[at + 2] << 16)) | ((uint)d[at + 3] << 24);
        }
    }
}
