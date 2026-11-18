// Wording for the publisher check (E26, engine side): one line per startup row saying who signed the program and
// whether that matches the company the file names. Pure, so the tests compile it. AppDetail shows it in place of
// "From ...", and AppText.LookCloser decides the "Look closer" marker beside the row's name.
using System;

namespace RamOptimiser.App
{
    public static partial class AppText
    {
        /// <summary>
        /// The publisher line for a row, with the real Windows folder. Display only: it never unlocks a switch.
        /// </summary>
        public static string PublisherLine(StartupApp a)
        {
            return PublisherLine(a, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }

        /// <summary>The publisher line for a row, given the Windows folder (so tests need no real one). Pure.</summary>
        public static string PublisherLine(StartupApp a, string windowsFolder)
        {
            if (a == null) return PublisherLine(SignatureState.NotChecked, null, null, false);
            // Only Windows' own files (owned by TrustedInstaller or SYSTEM) get the catalog wording: Tasks, Temp and a
            // few other Windows subfolders are writable by standard users, and a file put there really is unsigned.
            bool windowsFile = a.SystemOwned && StartupApps.IsInside(a.ExePath, windowsFolder);
            return PublisherLine(a.Signature, a.Signer, a.ClaimedCompany, windowsFile, a.RunsFile);
        }

        /// <summary>As below, for a program that runs nothing else.</summary>
        public static string PublisherLine(SignatureState state, string signer, string claimed, bool inWindowsFolder)
        {
            return PublisherLine(state, signer, claimed, inWindowsFolder, false);
        }

        /// <summary>
        /// Exactly one line, without a final full stop (AppDetail joins its parts with ". "):
        /// "Signed by Contoso Ltd", "Signed by Contoso Ltd, though the file says Microsoft Corporation",
        /// "Not signed; the file says Microsoft Corporation", "Not signed",
        /// "Signature does not check out; the file says Microsoft Corporation", "Signature blocked by Windows",
        /// "In the Windows folder; signature not checked" (catalog-signed Windows files carry no embedded signature),
        /// or today's "From Microsoft Corporation" / "Publisher unknown" when the file was not checked. runsFile (a
        /// host such as rundll32 or wscript running another file) adds that the other file is not checked, where the
        /// line would otherwise lend it the host's standing. Pure.
        /// </summary>
        public static string PublisherLine(SignatureState state, string signer, string claimed, bool inWindowsFolder, bool runsFile)
        {
            bool hasClaim = !string.IsNullOrEmpty(claimed);
            string says = hasClaim ? "; the file says " + claimed : "";
            string other = runsFile ? "; it runs another file, which is not checked" : "";
            switch (state)
            {
                case SignatureState.Signed:
                    if (string.IsNullOrEmpty(signer)) return "Signed" + says + other;
                    if (!hasClaim || Signature.SameCompany(signer, claimed)) return "Signed by " + signer + other;
                    return "Signed by " + signer + ", though the file says " + claimed + other;
                case SignatureState.Unsigned:
                    if (inWindowsFolder)
                        return runsFile ? "In the Windows folder; it runs another file; neither is checked" : "In the Windows folder; signature not checked";
                    return "Not signed" + says;
                case SignatureState.Invalid:
                    return "Signature does not check out" + says;
                case SignatureState.Distrusted:
                    return "Signature blocked by Windows";
                default:
                    return hasClaim ? "From " + claimed : "Publisher unknown";
            }
        }
    }
}
