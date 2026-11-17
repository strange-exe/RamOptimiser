// Wording for the publisher check (E26, engine side): one line per startup row saying who signed the program and
// whether that matches the company the file names. Pure, so the tests compile it. Nothing shows it yet: package 6A
// puts it in AppDetail in place of "From ..." and decides the "look closer" marker.
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
            bool inWindows = StartupApps.IsInside(a.ExePath, windowsFolder);
            return PublisherLine(a.Signature, a.Signer, a.ClaimedCompany, inWindows);
        }

        /// <summary>
        /// Exactly one line, without a final full stop (AppDetail joins its parts with ". "):
        /// "Signed by Contoso Ltd", "Signed by Contoso Ltd, though the file says Microsoft Corporation",
        /// "Not signed; the file says Microsoft Corporation", "Not signed",
        /// "Signature does not check out; the file says Microsoft Corporation", "Signature blocked by Windows",
        /// "In the Windows folder; signature not checked" (catalog-signed Windows files carry no embedded signature),
        /// or today's "From Microsoft Corporation" / "Publisher unknown" when the file was not checked. Pure.
        /// </summary>
        public static string PublisherLine(SignatureState state, string signer, string claimed, bool inWindowsFolder)
        {
            bool hasClaim = !string.IsNullOrEmpty(claimed);
            string says = hasClaim ? "; the file says " + claimed : "";
            switch (state)
            {
                case SignatureState.Signed:
                    if (string.IsNullOrEmpty(signer)) return "Signed" + says;
                    if (!hasClaim || Signature.SameCompany(signer, claimed)) return "Signed by " + signer;
                    return "Signed by " + signer + ", though the file says " + claimed;
                case SignatureState.Unsigned:
                    if (inWindowsFolder) return "In the Windows folder; signature not checked";
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
