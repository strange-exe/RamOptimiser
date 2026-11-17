using System;
using System.IO;

namespace RamOptimiser
{
    /// <summary>
    /// Where the desktop app and ramopt.exe keep their files: %LOCALAPPDATA%\RamOptimiser of the account the process
    /// runs as. An elevated copy started under another account (over-the-shoulder UAC) therefore uses that account's
    /// folder; the app says so on screen. settings.ini, error.log, stopped-services.txt and cleans.log all live here,
    /// and every write there goes through SafeUserFolder, because the folder is writable without elevation.
    /// </summary>
    public static class AppPaths
    {
        public const string FolderName = "RamOptimiser";

        public static string DataFolder
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, FolderName);
            }
        }
    }
}
