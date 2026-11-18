// Wording for typed outcomes (E13) and the service-startup journal (E12). Pure, so the tests compile it.
// AppText.ServiceChangeNote turns a change into the error or note under its Startup row, and ServiceDetail adds
// JournalNote after the start label.
namespace RamOptimiser.App
{
    public static partial class AppText
    {
        /// <summary>
        /// Why a Startup-tab change of a service changed nothing, as the reason ChangeFailedText takes. Today's words,
        /// so the error bar reads as before. Failed carries the Windows text. Null for a change that worked.
        /// </summary>
        public static string StartupChangeReason(StartupChangeKind k, string detail)
        {
            switch (k)
            {
                case StartupChangeKind.RefusedNotListed: return "not in the list of services that are safe to change";
                case StartupChangeKind.RefusedNotAtStartup: return "does not start with Windows";
                case StartupChangeKind.RefusedNotChangedByApp: return "was not changed by this app";
                case StartupChangeKind.RefusedChangedElsewhere: return "its start type was changed after this app turned it off, so it was left as it is";
                case StartupChangeKind.RefusedProtected: return "Windows protects this service, so its startup can't be changed";
                case StartupChangeKind.RefusedNeedsAdmin: return NeedsAdmin;
                case StartupChangeKind.Failed: return Plain(detail, "unknown error");
                default: return null;
            }
        }

        /// <summary>
        /// The neutral note under the row when a change worked only in part. Null when it went as asked, and for
        /// refusals and failures (those use StartupChangeReason in the error bar).
        /// </summary>
        public static string StartupChangeNote(StartupChangeKind k)
        {
            switch (k)
            {
                case StartupChangeKind.TurnedOffStillRunningDependents:
                    return "Won't start with Windows. Other services need it, so it keeps running until you restart.";
                case StartupChangeKind.TurnedOffStopFailed:
                    return "Won't start with Windows. It could not be stopped now, so it keeps running until you restart.";
                case StartupChangeKind.TurnedOffStillStopping:
                    return "Won't start with Windows. It is still stopping.";
                case StartupChangeKind.TurnedOnStartFailed:
                    return "Starts with Windows again. Starting it now failed, so it starts at the next restart.";
                case StartupChangeKind.TurnedOnDelayNotRestored:
                    return "Starts with Windows again, but not delayed as before.";
                case StartupChangeKind.AlreadyOn:
                    return "Already starts with Windows: something else turned it back on.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// A sentence for the row detail when the journal shows something else changed a service this app turned off,
        /// without a closing stop (ServiceDetail joins its parts with ". "). Null otherwise: OffByApp has its own start
        /// label, and an orphan has no row. Shown once in an elevated session, which then tidies the record away.
        /// </summary>
        public static string JournalNote(JournalState s)
        {
            switch (s)
            {
                case JournalState.TurnedBackOn: return "Windows or an updater turned it back on after this app turned it off";
                case JournalState.OffElsewhere: return "Turned off by someone else after this app changed it, so the app leaves it alone";
                default: return null;
            }
        }
    }
}
