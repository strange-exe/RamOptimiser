// Audit run-4 record 15 and hardening 43/83, record 20 and hardening 77/96: what the elevated app opens and trusts.
// No pipe, share or registry is touched here: the opener flags are read from their constants, the data-folder gate and
// the protected roots from pure functions. The only side effect is a process-local environment variable, restored.
using System;
using System.Collections.Generic;

namespace RamOptimiser.Tests
{
    public static class OpenHardeningTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            Sqos();
            DataFolderGate();
            ProtectedRoots();
            return failed;
        }

        // ---------------------------------------------------------------- record 15: SQOS on every raw open

        static void Sqos()
        {
            // SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION (winbase.h): a pipe server gets an identification token.
            const uint expected = 0x00100000 | 0x00010000;
            Check("sqos: the shared value is SQOS_PRESENT | IDENTIFICATION", SecurityNative.SqosIdentification == expected,
                SecurityNative.SqosIdentification.ToString("X8"));
            var openers = new Dictionary<string, uint> {
                { "CleanLog folder", CleanLog.FolderOpenFlags },
                { "CleanLog log", CleanLog.LogOpenFlags },
                { "Signature.Inspect", Signature.OpenFlags },
                { "StartupApps.ReadRegularFile", StartupApps.ReadOpenFlags },
                { "Cleaner volume flush", Cleaner.VolumeOpenFlags } };
            foreach (KeyValuePair<string, uint> o in openers)
                Check("sqos: " + o.Key + " opens with identification-level SQOS", (o.Value & expected) == expected, o.Value.ToString("X8"));
            // The flags each opener already relied on are kept.
            Check("sqos: folder open keeps backup semantics and no-follow",
                (CleanLog.FolderOpenFlags & (SecurityNative.FILE_FLAG_BACKUP_SEMANTICS | SecurityNative.FILE_FLAG_OPEN_REPARSE_POINT))
                == (SecurityNative.FILE_FLAG_BACKUP_SEMANTICS | SecurityNative.FILE_FLAG_OPEN_REPARSE_POINT));
            Check("sqos: log, Inspect and ReadRegularFile still open a link as itself",
                (CleanLog.LogOpenFlags & Signature.OpenFlags & StartupApps.ReadOpenFlags & SecurityNative.FILE_FLAG_OPEN_REPARSE_POINT) != 0);
        }

        // ---------------------------------------------------------------- hardening 43/83: data folder path kinds

        static void DataFolderGate()
        {
            foreach (string bad in new[] {
                @"\\?\C:\Users\x\AppData\Local", @"\\?\UNC\server\share\x", @"\\?\GLOBALROOT\Device\NamedPipe\x",
                @"\\.\C:\Users\x", @"\\.\pipe\x", @"\\.\PIPE\x",
                @"\\localhost\pipe\x", @"\\HOST\PIPE\x", @"\\server\Pipe", @"\\server\pipe\", @"\\server/pipe/x",
                @"\\server\\pipe\x", @"\\server\pipe.\x", @"\\\?\C:\x", @"\\", @"\??\pipe\x" })
                Check("paths: refused " + bad, AppPaths.DataFolderFor(bad) == null, AppPaths.DataFolderFor(bad));
            Check("paths: a drive base still gets the folder",
                AppPaths.DataFolderFor(@"C:\Users\x\AppData\Local") == @"C:\Users\x\AppData\Local\RamOptimiser");
            Check("paths: a share still gets the folder", AppPaths.DataFolderFor(@"\\server\share\x") == @"\\server\share\x\RamOptimiser");
            Check("paths: a share merely starting with pipe is a share", AppPaths.DataFolderFor(@"\\server\pipeline\x") == @"\\server\pipeline\x\RamOptimiser"
                && AppPaths.DataFolderFor(@"\\server\pipes") == @"\\server\pipes\RamOptimiser");
            Check("paths: a folder named pipe deeper in a share is fine",
                AppPaths.DataFolderFor(@"\\server\share\pipe") == @"\\server\share\pipe\RamOptimiser");

            // IPC$ is the named-pipe share of a host, like \\host\pipe.
            foreach (string bad in new[] { @"\\server\IPC$\x", @"\\server\ipc$", @"\\server\Ipc$\", @"\\server/IPC$/x",
                @"\\server\\ipc$\x", @"\\server\IPC$.\x", @"\\server\IPC$ \x" })
                Check("paths: refused " + bad, AppPaths.DataFolderFor(bad) == null, AppPaths.DataFolderFor(bad));
            Check("paths: a share merely starting with ipc is a share",
                AppPaths.DataFolderFor(@"\\server\ipcshare\x") == @"\\server\ipcshare\x\RamOptimiser"
                && AppPaths.DataFolderFor(@"\\server\ipc\x") == @"\\server\ipc\x\RamOptimiser");
            Check("paths: a folder named IPC$ deeper in a share is fine",
                AppPaths.DataFolderFor(@"\\server\share\IPC$") == @"\\server\share\IPC$\RamOptimiser");
        }

        // ---------------------------------------------------------------- record 20, hardening 77/96: protected roots

        static void ProtectedRoots()
        {
            Check("roots: the build is 64-bit, so SpecialFolder.ProgramFiles is the 64-bit folder", Environment.Is64BitProcess);

            Func<Environment.SpecialFolder, string> fake = f =>
                f == Environment.SpecialFolder.Windows ? @"W:\Windows"
                : f == Environment.SpecialFolder.ProgramFiles ? @"W:\Program Files"
                : f == Environment.SpecialFolder.ProgramFilesX86 ? "" : @"W:\Other";
            string[] roots = StartupApps.ProtectedRootsFrom(fake);
            Check("roots: only the known folders, an empty one dropped", roots.Length == 2 && roots[0] == @"W:\Windows"
                && roots[1] == @"W:\Program Files", string.Join(" | ", roots));

            const string planted = @"C:\Users\Public\RamOptimiserPlantedRoot";
            string before = Environment.GetEnvironmentVariable("ProgramW6432");
            try
            {
                Environment.SetEnvironmentVariable("ProgramW6432", planted);
                string[] live = StartupApps.ProtectedRoots();
                bool hasPlanted = false;
                foreach (string r in live) if (StartupApps.IsInside(planted, r) || StartupApps.IsInside(r, planted)
                    || string.Equals(r, planted, StringComparison.OrdinalIgnoreCase)) hasPlanted = true;
                Check("roots: ProgramW6432 from the environment adds no root", !hasPlanted, string.Join(" | ", live));
                Check("roots: pure roots ignore ProgramW6432 too", StartupApps.ProtectedRootsFrom(fake).Length == 2);
                KnownItem tray = StartupKnowledge.FindApp("SecurityHealthSystray.exe");
                Check("roots: a Keep match under the planted folder is dropped",
                    StartupApps.TrustedKnown(tray, planted + @"\SecurityHealthSystray.exe", live) == null);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ProgramW6432", before);
            }
            Check("roots: ProgramW6432 restored", Environment.GetEnvironmentVariable("ProgramW6432") == before);
        }
    }
}
