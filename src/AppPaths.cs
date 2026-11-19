using System;
using System.IO;

namespace RamOptimiser
{
    /// <summary>
    /// Where the desktop app and ramopt.exe keep their files: %LOCALAPPDATA%\RamOptimiser of the account the process
    /// runs as. An elevated copy started under another account (over-the-shoulder UAC) therefore uses that account's
    /// folder; the app says so on screen. settings.ini, error.log and cleans.log live here, and every write there goes
    /// through SafeUserFolder, because the folder is writable without elevation. Power Clean's journal, which the
    /// elevated app acts on, is not here: it is in an administrators-only HKLM key (PowerCleanJournal).
    /// </summary>
    public static class AppPaths
    {
        public const string FolderName = "RamOptimiser";

        /// <summary>
        /// The data folder under a local app data folder, or null when that is empty or not a full path ("C:\..." or
        /// "\\server\share\..."). Windows answers "" for an account with no profile folder (a service account, a
        /// scheduled task with no loaded profile); a relative path would land in the current directory, often
        /// C:\Windows\System32 for such a process. Pure.
        /// </summary>
        public static string DataFolderFor(string localAppData)
        {
            if (string.IsNullOrEmpty(localAppData)) return null;
            bool drive = localAppData.Length >= 3 && char.IsLetter(localAppData[0]) && localAppData[1] == ':' && localAppData[2] == '\\';
            bool unc = localAppData.StartsWith(@"\\", StringComparison.Ordinal);
            return drive || unc ? Path.Combine(localAppData, FolderName) : null;
        }

        /// <summary>The data folder of the account the process runs as, or null when that account has none (DataFolderFor).</summary>
        public static string DataFolder
        {
            get
            {
                // DoNotVerify: a profile whose Local folder does not exist yet still gets its real path, which the
                // writers create; only a missing profile gives "".
                return DataFolderFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));
            }
        }
    }
}
