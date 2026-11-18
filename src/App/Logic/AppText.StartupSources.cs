// Wording for the extra startup sources (E24): packaged (Store / MSIX) startup tasks and RunOnce entries.
// Pure, so the tests compile it. The Startup tab shows these rows read-only (AppText.AppLock), puts the sentences
// in the row detail (AppDetail) and shows the "Open Startup settings" button while a packaged row is listed.
namespace RamOptimiser.App
{
    public static partial class AppText
    {
        /// <summary>The label of a button that opens Settings, Apps, Startup, where packaged apps are turned on or off.</summary>
        public const string OpenStartupSettings = "Open Startup settings";
        /// <summary>A fixed ms-settings URI (learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app); no user data in it.</summary>
        public const string StartupSettingsUri = "ms-settings:startupapps";

        /// <summary>
        /// The words in place of the switch for a row this app can't change: "In Settings" for a packaged app,
        /// "Runs once" for a RunOnce entry. Null for rows with a switch.
        /// </summary>
        public static string StartupSourceLock(StartupApp a)
        {
            if (a == null) return null;
            if (a.Source == StartupSource.Packaged) return "In Settings";
            if (StartupApps.IsRunOnce(a.Source)) return "Runs once";
            return null;
        }

        /// <summary>
        /// The sentences a packaged or RunOnce row adds to its detail, joined with ". " and without a final full
        /// stop, the way AppDetail joins its parts. Null for any other row.
        /// </summary>
        public static string StartupSourceDetail(StartupApp a)
        {
            if (a == null) return null;
            if (a.Source == StartupSource.Packaged)
                return a.PolicySet ? "Your organisation controls whether it starts"
                    : "Store app. Turn it on or off in Settings, Apps, Startup";
            if (StartupApps.IsRunOnce(a.Source)) return "Runs once at the next sign-in, then Windows removes it";
            return null;
        }

        /// <summary>
        /// Whether a row counts as an app that starts with Windows in the Startup summary: an enabled Run, folder or
        /// packaged row. A RunOnce entry runs a single time, so it is never counted.
        /// </summary>
        public static bool CountsAsAppOn(StartupApp a)
        {
            return a != null && a.Enabled && !StartupApps.IsRunOnce(a.Source);
        }
    }
}
