using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.ServiceProcess;
using System.Threading;

namespace RamOptimiser
{
    public enum ServiceTier
    {
        /// <summary>Ticked by default: rarely needed day to day, and Windows starts it again when it is needed.</summary>
        Suggested,
        /// <summary>Offered but unticked: stopping it switches off something some people use.</summary>
        Optional
    }

    /// <summary>One service Power Clean may stop. Per-user services match "Name_" plus their per-session suffix.</summary>
    public sealed class ServiceRule
    {
        public readonly string Name;
        public readonly bool PerUser;
        public readonly string Label;
        public readonly ServiceTier Tier;
        public readonly string Why;
        /// <summary>Trigger-start service: Windows may start it again by itself when its event fires.</summary>
        public readonly bool Triggered;

        public ServiceRule(string name, bool perUser, string label, ServiceTier tier, string why, bool triggered = false)
        {
            Name = name; PerUser = perUser; Label = label; Tier = tier; Why = why; Triggered = triggered;
        }

        public bool Matches(string serviceName)
        {
            if (serviceName == null) return false;
            if (string.Equals(serviceName, Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!PerUser || serviceName.Length <= Name.Length + 1) return false;
            if (!serviceName.StartsWith(Name + "_", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = Name.Length + 1; i < serviceName.Length; i++)
                if (!Uri.IsHexDigit(serviceName[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// The only services Power Clean can ever stop. The chosen list comes from settings.ini, which the
    /// non-elevated user can edit, so every name is checked against this catalog: settings can narrow the
    /// list, never widen it to Defender, Windows Update or anything else.
    /// </summary>
    public static class ServiceCatalog
    {
        public static readonly ServiceRule[] Rules = BuildRules();

        // Sources: Microsoft's service guidance for Windows Server 2016 with Desktop Experience
        // (learn.microsoft.com/en-us/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server)
        // and the Windows 10 2004 VDI recommendations; there is no client edition of that list. Left out on purpose:
        // services Microsoft says not to disable (WerSvc, TabletInputService, Themes, UsoSvc), ones client PCs need
        // (dmwappushservice for MDM, BITS, DoSvc, push notifications, audio, Bluetooth, sign-in) and protected ones
        // (WinDefend), which refuse a stop even from an administrator.
        static ServiceRule[] BuildRules()
        {
            return new[]
            {
                // Suggested: nothing most people use day to day stops working.
                new ServiceRule("XblAuthManager", false, "Xbox Live Auth Manager", ServiceTier.Suggested,
                    "Only Xbox apps and games use it"),
                new ServiceRule("XblGameSave", false, "Xbox Live Game Save", ServiceTier.Suggested,
                    "Syncs game saves; starts again when a game needs it", true),
                new ServiceRule("XboxNetApiSvc", false, "Xbox Live Networking", ServiceTier.Suggested,
                    "Only Xbox games use it"),
                new ServiceRule("MapsBroker", false, "Downloaded Maps Manager", ServiceTier.Suggested,
                    "Only the Maps app uses it"),
                new ServiceRule("WalletService", false, "Wallet", ServiceTier.Suggested,
                    "Used by the Wallet app only"),
                new ServiceRule("RetailDemo", false, "Retail Demo", ServiceTier.Suggested,
                    "Only used in shop demo mode"),
                new ServiceRule("Fax", false, "Fax", ServiceTier.Suggested, "Fax sending and receiving"),
                new ServiceRule("upnphost", false, "UPnP Device Host", ServiceTier.Suggested,
                    "Lets this PC host UPnP devices; rarely used"),
                new ServiceRule("lltdsvc", false, "Link-Layer Topology Discovery Mapper", ServiceTier.Suggested,
                    "Draws the network map; rarely used"),
                new ServiceRule("QWAVE", false, "Quality Windows Audio Video Experience", ServiceTier.Suggested,
                    "Old home-network streaming helper"),
                new ServiceRule("wisvc", false, "Windows Insider", ServiceTier.Suggested,
                    "Only Insider preview builds use it", true),

                // Optional: something some people use stops until the service starts again.
                new ServiceRule("SysMain", false, "SysMain (app prefetch)", ServiceTier.Optional,
                    "Refills the standby cache after a clean, so stopping it makes the clean last; apps open a little slower"),
                new ServiceRule("WSearch", false, "Windows Search indexer", ServiceTier.Optional,
                    "Start menu and File Explorer search get slower until it runs again"),
                new ServiceRule("Spooler", false, "Print Spooler", ServiceTier.Optional,
                    "Printing stops until it starts again"),
                new ServiceRule("PrintNotify", false, "Printer Extensions and Notifications", ServiceTier.Optional,
                    "Printer pop-ups; goes with the Print Spooler"),
                new ServiceRule("DiagTrack", false, "Connected User Experiences and Telemetry", ServiceTier.Optional,
                    "Sends diagnostic data to Microsoft"),
                new ServiceRule("PhoneSvc", false, "Phone Service", ServiceTier.Optional,
                    "Calls through Phone Link and VoIP apps", true),
                new ServiceRule("lfsvc", false, "Geolocation", ServiceTier.Optional,
                    "Location for apps", true),
                new ServiceRule("XboxGipSvc", false, "Xbox Accessory Management", ServiceTier.Optional,
                    "Xbox controller settings; starts again when one is plugged in", true),
                new ServiceRule("PcaSvc", false, "Program Compatibility Assistant", ServiceTier.Optional,
                    "Fixes for old installers and programs", true),
                new ServiceRule("SSDPSRV", false, "SSDP Discovery", ServiceTier.Optional,
                    "Finds media players, TVs and printers on the network"),
                new ServiceRule("stisvc", false, "Windows Image Acquisition", ServiceTier.Optional,
                    "Scanners and some cameras", true),
                new ServiceRule("SharedAccess", false, "Internet Connection Sharing", ServiceTier.Optional,
                    "Mobile hotspot", true),
                new ServiceRule("CDPUserSvc", true, "Connected Devices Platform", ServiceTier.Optional,
                    "Phone Link and Nearby sharing"),
                new ServiceRule("OneSyncSvc", true, "Sync Host", ServiceTier.Optional,
                    "Mail, Calendar and People sync"),
            };
        }

        public static ServiceRule Find(string serviceName)
        {
            foreach (ServiceRule r in Rules) if (r.Matches(serviceName)) return r;
            return null;
        }

        /// <summary>Catalog names ticked by default.</summary>
        public static List<string> SuggestedNames()
        {
            var list = new List<string>();
            foreach (ServiceRule r in Rules) if (r.Tier == ServiceTier.Suggested) list.Add(r.Name);
            return list;
        }

        /// <summary>Keeps only catalog names, in catalog order, without duplicates, spelled as in the catalog.</summary>
        public static List<string> Filter(IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null) foreach (string n in names) if (n != null) wanted.Add(n.Trim());
            var list = new List<string>();
            foreach (ServiceRule r in Rules) if (wanted.Contains(r.Name)) list.Add(r.Name);
            return list;
        }
    }

    /// <summary>What one service looks like right now. MemoryBytes is -1 when it is not known.</summary>
    public sealed class ServiceState
    {
        public string Name;
        public string DisplayName;
        public ServiceRule Rule;
        public bool Running;
        public bool CanStop;
        public int ProcessId;
        /// <summary>Running services in the same process: when above 1 the memory is shared, not this service's own.</summary>
        public int SharedWith;
        public long MemoryBytes = -1;
        public List<string> RunningDependents = new List<string>();
        /// <summary>A per-user instance not shown to run in this app's own logon session: another signed-in user's.</summary>
        public bool OtherSession;
    }

    /// <summary>
    /// What happened to one service in a Power Clean or a Restore. Pending: planned, nothing done yet. The Skipped
    /// values are planned or safe skips; the failure values are a stop or start that was tried and did not finish.
    /// </summary>
    public enum ServiceOutcome
    {
        Pending, Stopped, Started,
        SkippedOtherSession, SkippedNotRunning, SkippedCannotStop, SkippedDependents, SkippedDependentStarted,
        /// <summary>The clean was stopped (cancel or time limit) before this service was sent a stop.</summary>
        SkippedCancelled,
        /// <summary>Restore found the service uninstalled (or a per-user instance from an earlier sign-in).</summary>
        SkippedNotInstalled,
        StopFailed, StopTimedOut, StartFailed, StartTimedOut, StillStopping
    }

    /// <summary>Rules over ServiceOutcome, so no caller decides anything from an action's message text. Pure.</summary>
    public static class ServiceOutcomes
    {
        /// <summary>A stop or start was tried and did not finish.</summary>
        public static bool IsFailure(ServiceOutcome o)
        {
            switch (o)
            {
                case ServiceOutcome.StopFailed:
                case ServiceOutcome.StopTimedOut:
                case ServiceOutcome.StartFailed:
                case ServiceOutcome.StartTimedOut:
                case ServiceOutcome.StillStopping:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsSkip(ServiceOutcome o)
        {
            switch (o)
            {
                case ServiceOutcome.SkippedOtherSession:
                case ServiceOutcome.SkippedNotRunning:
                case ServiceOutcome.SkippedCannotStop:
                case ServiceOutcome.SkippedDependents:
                case ServiceOutcome.SkippedDependentStarted:
                case ServiceOutcome.SkippedCancelled:
                case ServiceOutcome.SkippedNotInstalled:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The clean result names failures and skips; a service that was not running needs no word.</summary>
        public static bool ShownInSummary(ServiceOutcome o)
        {
            return IsFailure(o) || (IsSkip(o) && o != ServiceOutcome.SkippedNotRunning);
        }

        /// <summary>The action's outcome; no action is Pending. Never read from the message text.</summary>
        public static ServiceOutcome Of(ServiceAction a)
        {
            return a == null ? ServiceOutcome.Pending : a.Outcome;
        }
    }

    public sealed class ServiceAction
    {
        public string Name;
        public string Label;
        public ServiceOutcome Outcome;
        /// <summary>The detail behind the outcome: the Win32 reason, the dependents, the timeout. Null when it worked.</summary>
        public string Message;

        /// <summary>Derived from Outcome.</summary>
        public bool Stopped { get { return Outcome == ServiceOutcome.Stopped; } }

        /// <summary>Derived from Outcome.</summary>
        public bool Started { get { return Outcome == ServiceOutcome.Started; } }

        /// <summary>A stop or start was tried and did not finish; a planned skip is not a failure. Derived from Outcome.</summary>
        public bool Failed { get { return ServiceOutcomes.IsFailure(Outcome); } }
    }

    /// <summary>
    /// Where Power Clean records the services it stops, so the names outlive a crash, a kill or a sign-out before
    /// the exit restore. The desktop app sets ServiceTrimmer.Journal; the CLI leaves it null. Both methods return
    /// false when the record could not be written.
    /// </summary>
    public interface IStoppedServiceJournal
    {
        bool Add(string name);
        bool Remove(string name);
    }

    /// <summary>
    /// The journal's rules, free of I/O. The journal file sits in a folder the non-elevated user can write, so the
    /// elevated app trusts none of it: only catalog service names are kept, whatever the file says.
    /// </summary>
    public static class StoppedServiceJournal
    {
        /// <summary>The catalog has 25 rules; per-user ones can have a few instances. More lines are not ours.</summary>
        public const int MaxNames = 64;
        /// <summary>Windows' own limit for a service name.</summary>
        public const int MaxNameLength = 256;

        /// <summary>Catalog names only (per-user instances included), trimmed, without duplicates, in order.</summary>
        public static List<string> Keep(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            if (names == null) return list;
            foreach (string raw in names)
            {
                if (raw == null) continue;
                string n = raw.Trim();
                if (n.Length == 0 || n.Length > MaxNameLength || ServiceCatalog.Find(n) == null) continue;
                if (seen.Add(n)) list.Add(n);
            }
            return list;
        }

        /// <summary>One name per line; blank lines and # comments are ignored. At most MaxNames are kept.</summary>
        public static List<string> Parse(string text)
        {
            var lines = new List<string>();
            if (text != null)
                foreach (string line in text.Split('\n'))
                    if (!line.TrimStart().StartsWith("#", StringComparison.Ordinal)) lines.Add(line);
            List<string> kept = Keep(lines);
            if (kept.Count > MaxNames) kept.RemoveRange(MaxNames, kept.Count - MaxNames);
            return kept;
        }

        public static string Serialize(IEnumerable<string> names)
        {
            var sb = new System.Text.StringBuilder("# Services RamOptimiser's Power Clean stopped and has not started again.\r\n");
            foreach (string n in Keep(names)) sb.Append(n).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>The list plus name, when it is a catalog name not already there.</summary>
        public static List<string> With(IEnumerable<string> names, string name)
        {
            List<string> list = Keep(names);
            list.Add(name);
            return Keep(list);
        }

        /// <summary>The list without name, compared ignoring case.</summary>
        public static List<string> Without(IEnumerable<string> names, string name)
        {
            List<string> list = Keep(names);
            if (name != null) list.RemoveAll(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }

    /// <summary>
    /// Stops the chosen catalog services for this session only (the startup type is never changed, so they come
    /// back at the next restart or when Windows triggers them) and starts them again on request.
    /// </summary>
    public static class ServiceTrimmer
    {
        public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
        static readonly object gate = new object();
        static readonly List<string> stoppedThisSession = new List<string>();

        /// <summary>Persistent copy of the stopped list (see IStoppedServiceJournal). Set once, before any clean.</summary>
        public static IStoppedServiceJournal Journal { get; set; }

        /// <summary>StopRecorded's result when the journal could not be written: nothing was stopped.</summary>
        internal const int JournalWriteFailed = -1;
        const int ErrorServiceDoesNotExist = 1060;

        /// <summary>
        /// Records the name first, then sends the stop, so a crash after the stop still leaves the way back (as
        /// StartupServices does). A refused stop forgets the record again, unless keepRecord says this process
        /// stopped the service earlier and has not started it since. Returns stopOnly's error, or JournalWriteFailed.
        /// </summary>
        internal static int StopRecorded(string name, IStoppedServiceJournal journal, bool keepRecord, Func<string, int> stopOnly)
        {
            if (journal != null && !journal.Add(name)) return JournalWriteFailed;
            int err = stopOnly(name);
            if (err != 0 && journal != null && !keepRecord) journal.Remove(name);
            return err;
        }

        /// <summary>True when the exception says the service is not installed (any more). Pure.</summary>
        internal static bool IsMissingService(InvalidOperationException ex)
        {
            var inner = ex == null ? null : ex.InnerException as System.ComponentModel.Win32Exception;
            return inner != null && inner.NativeErrorCode == ErrorServiceDoesNotExist;
        }

        static bool IsStoppedByUs(string name)
        {
            lock (gate) return stoppedThisSession.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        static void Forget(string name)
        {
            lock (gate) stoppedThisSession.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            IStoppedServiceJournal journal = Journal;
            if (journal != null) journal.Remove(name);
        }

        /// <summary>
        /// Takes on services an earlier run stopped and never started again (read from the journal), so Restore and
        /// the Restore button cover them. Only catalog names are taken. Returns the names taken.
        /// </summary>
        public static List<string> Adopt(IEnumerable<string> names)
        {
            List<string> kept = StoppedServiceJournal.Keep(names);
            foreach (string n in kept)
                lock (gate) if (!stoppedThisSession.Exists(s => string.Equals(s, n, StringComparison.OrdinalIgnoreCase))) stoppedThisSession.Add(n);
            return kept;
        }

        /// <summary>Services this process stopped and has not started again.</summary>
        public static List<string> StoppedThisSession()
        {
            lock (gate) return new List<string>(stoppedThisSession);
        }

        /// <summary>Every catalog service installed on this PC, with state and memory. Needs no elevation to read.</summary>
        public static List<ServiceState> Query()
        {
            var result = new List<ServiceState>();
            var runningPerPid = new Dictionary<int, int>();
            using (var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, AcceptStop, ProcessId FROM Win32_Service"))
            using (ManagementObjectCollection all = searcher.Get())
            {
                foreach (ManagementObject mo in all)
                {
                    using (mo)
                    {
                        string name = mo["Name"] as string;
                        bool running = string.Equals(mo["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
                        int pid = mo["ProcessId"] == null ? 0 : Convert.ToInt32(mo["ProcessId"], CultureInfo.InvariantCulture);
                        if (running && pid > 0)
                        {
                            int n; runningPerPid.TryGetValue(pid, out n); runningPerPid[pid] = n + 1;
                        }
                        ServiceRule rule = ServiceCatalog.Find(name);
                        if (rule == null) continue;
                        result.Add(new ServiceState
                        {
                            Name = name,
                            DisplayName = mo["DisplayName"] as string ?? name,
                            Rule = rule,
                            Running = running,
                            CanStop = mo["AcceptStop"] is bool && (bool)mo["AcceptStop"],
                            ProcessId = running ? pid : 0,
                        });
                    }
                }
            }
            int mySession;
            using (Process me = Process.GetCurrentProcess()) mySession = me.SessionId;
            foreach (ServiceState s in result)
            {
                // Every running service gets the dependents check, whether or not it reports a process id.
                if (s.Running) s.RunningDependents = RunningDependents(s.Name);
                bool perUserInstance = !string.Equals(s.Name, s.Rule.Name, StringComparison.OrdinalIgnoreCase);
                // Only a running instance can be stopped; one whose session can't be confirmed counts as someone else's.
                s.OtherSession = perUserInstance && s.Running && SessionOf(s.ProcessId) != mySession;
                if (s.ProcessId <= 0) continue;
                int n; runningPerPid.TryGetValue(s.ProcessId, out n);
                s.SharedWith = n;
                try { using (Process p = Process.GetProcessById(s.ProcessId)) s.MemoryBytes = p.PrivateMemorySize64; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            result.Sort((a, b) => IndexOf(a.Rule).CompareTo(IndexOf(b.Rule)));
            return result;
        }

        static int IndexOf(ServiceRule r) { return Array.IndexOf(ServiceCatalog.Rules, r); }

        /// <summary>The logon session a process runs in; -1 when unknown (no process, or it has exited).</summary>
        static int SessionOf(int pid)
        {
            if (pid <= 0) return -1;
            try { using (Process p = Process.GetProcessById(pid)) return p.SessionId; }
            catch (ArgumentException) { return -1; }
            catch (InvalidOperationException) { return -1; }
            catch (System.ComponentModel.Win32Exception) { return -1; }
        }

        static List<string> RunningDependents(string name)
        {
            var list = new List<string>();
            try
            {
                using (var sc = new ServiceController(name))
                    foreach (ServiceController d in sc.DependentServices)
                        using (d) if (d.Status != ServiceControllerStatus.Stopped) list.Add(d.ServiceName);
            }
            catch (InvalidOperationException) { }
            return list;
        }

        /// <summary>
        /// Which of the chosen services to stop, and why the others are left alone. Pure: no system calls.
        /// Only catalog services, only running ones that accept a stop, never another signed-in user's per-user
        /// instance, and never one another running service depends on (Stop re-checks that at the moment it stops).
        /// </summary>
        public static List<ServiceAction> Plan(IEnumerable<ServiceState> states, ICollection<string> chosen)
        {
            var picked = new HashSet<string>(ServiceCatalog.Filter(chosen), StringComparer.OrdinalIgnoreCase);
            var plan = new List<ServiceAction>();
            foreach (ServiceState s in states)
            {
                if (s.Rule == null || !s.Rule.Matches(s.Name) || !picked.Contains(s.Rule.Name)) continue;
                var a = new ServiceAction { Name = s.Name, Label = s.Rule.Label };
                if (s.OtherSession) Mark(a, ServiceOutcome.SkippedOtherSession, "belongs to another signed-in user");
                else if (!s.Running) Mark(a, ServiceOutcome.SkippedNotRunning, NotRunning);
                else if (!s.CanStop) Mark(a, ServiceOutcome.SkippedCannotStop, "does not accept a stop right now");
                else if (s.RunningDependents.Count > 0)
                    Mark(a, ServiceOutcome.SkippedDependents, "needed by " + string.Join(", ", s.RunningDependents.ToArray()));
                plan.Add(a);
            }
            return plan;
        }

        /// <summary>The reason shown for a service that was not running. Display only; outcomes decide.</summary>
        internal const string NotRunning = "not running";

        static void Mark(ServiceAction a, ServiceOutcome outcome, string message) { a.Outcome = outcome; a.Message = message; }

        /// <summary>
        /// Held by every action that stops, starts or reconfigures a service (Power Clean, Restore, the Startup
        /// menu), so two of them never act on the same service at once. Taken only on background threads.
        /// </summary>
        internal static readonly object Operation = new object();

        /// <summary>Stops every planned service that has no skip reason. Needs administrator rights.</summary>
        public static List<ServiceAction> Stop(ICollection<string> chosen)
        {
            return Stop(chosen, CancellationToken.None);
        }

        /// <summary>The wait for one service to stop is cut into slices this long, so a cancel ends it soon.</summary>
        public static readonly TimeSpan StopSlice = TimeSpan.FromMilliseconds(250);

        /// <summary>As above; cancel is checked before each service and between the slices of each wait.</summary>
        public static List<ServiceAction> Stop(ICollection<string> chosen, CancellationToken cancel)
        {
            lock (Operation)
            {
                List<ServiceAction> plan = Plan(Query(), chosen);
                StopPlanned(plan, name => StopRecorded(name, Journal, IsStoppedByUs(name), NativeService.StopOnly), WaitStopped, cancel,
                    name => { lock (gate) if (!stoppedThisSession.Contains(name)) stoppedThisSession.Add(name); });
                return plan;
            }
        }

        /// <summary>
        /// Stops each planned service that has no skip reason, recording the outcome in its action. stopOne sends the
        /// stop (0 or a Win32 error, or JournalWriteFailed); waitStopped waits up to one slice and says whether the
        /// service stopped (it may throw InvalidOperationException); accepted records a stop Windows accepted, before
        /// any wait, so Restore can start it again whatever happens next. A cancel leaves the services not yet sent a
        /// stop running, and ends a wait under way as "still stopping; clean was stopped" (failed, and still recorded).
        /// Tests pass fakes.
        /// </summary>
        internal static void StopPlanned(List<ServiceAction> plan, Func<string, int> stopOne, Func<string, TimeSpan, bool> waitStopped,
            CancellationToken cancel, Action<string> accepted)
        {
            Stopwatch clock = Stopwatch.StartNew();
            StopPlanned(plan, stopOne, waitStopped, cancel, accepted, () => clock.Elapsed);
        }

        /// <summary>
        /// As above, with the monotonic clock the wait is measured on. A real wait overruns its slice
        /// (ServiceController.WaitForStatus sleeps 250 ms after a check that misses), so the time that passed counts,
        /// not the slices asked for, and the last slice is cut to what is left of StopTimeout.
        /// </summary>
        internal static void StopPlanned(List<ServiceAction> plan, Func<string, int> stopOne, Func<string, TimeSpan, bool> waitStopped,
            CancellationToken cancel, Action<string> accepted, Func<TimeSpan> elapsed)
        {
            foreach (ServiceAction a in plan)
            {
                if (ServiceOutcomes.Of(a) != ServiceOutcome.Pending) continue;
                if (cancel.IsCancellationRequested) { Mark(a, ServiceOutcome.SkippedCancelled, "left running: clean was stopped"); continue; }
                int err = stopOne(a.Name);
                if (err == JournalWriteFailed) { Mark(a, ServiceOutcome.StopFailed, "left running: the list of stopped services could not be saved"); continue; }
                if (err == NativeService.ErrorDependentServicesRunning) { Mark(a, ServiceOutcome.SkippedDependentStarted, "needed by a service that just started"); continue; }
                if (err == NativeService.ErrorServiceNotActive) { Mark(a, ServiceOutcome.SkippedNotRunning, NotRunning); continue; }
                if (err != 0) { Mark(a, ServiceOutcome.StopFailed, new System.ComponentModel.Win32Exception(err).Message.TrimEnd('.')); continue; }
                // The stop was accepted: from here Restore must be able to start it again, even if the wait times out.
                accepted(a.Name);
                try
                {
                    TimeSpan start = elapsed();
                    while (true)
                    {
                        TimeSpan left = StopTimeout - (elapsed() - start);
                        TimeSpan slice = left < StopSlice ? left : StopSlice;
                        if (slice < TimeSpan.Zero) slice = TimeSpan.Zero;
                        if (waitStopped(a.Name, slice)) { a.Outcome = ServiceOutcome.Stopped; break; }
                        if (elapsed() - start >= StopTimeout) { Mark(a, ServiceOutcome.StopTimedOut, "still stopping after " + StopTimeout.TotalSeconds + " s"); break; }
                        if (cancel.IsCancellationRequested) { Mark(a, ServiceOutcome.StillStopping, "still stopping; clean was stopped"); break; }
                    }
                }
                catch (InvalidOperationException ex) { Mark(a, ServiceOutcome.StopFailed, Describe(ex)); }
            }
        }

        /// <summary>Waits up to slice for the service to stop; false when it has not yet.</summary>
        static bool WaitStopped(string name, TimeSpan slice)
        {
            using (var sc = new ServiceController(name))
            {
                try { sc.WaitForStatus(ServiceControllerStatus.Stopped, slice); return true; }
                catch (System.ServiceProcess.TimeoutException) { return false; }
            }
        }

        /// <summary>Starts again every service this process stopped. Ones already running count as restored.</summary>
        public static List<ServiceAction> Restore()
        {
            lock (Operation) return RestoreLocked(StoppedThisSession());
        }

        /// <summary>
        /// Starts again only the named services that are on this process's stopped list. Start-up uses it for the
        /// journal's leftovers, so a Power Clean that got the lock first in this run is not undone.
        /// </summary>
        public static List<ServiceAction> Restore(IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(StoppedServiceJournal.Keep(names), StringComparer.OrdinalIgnoreCase);
            lock (Operation) return RestoreLocked(StoppedThisSession().FindAll(wanted.Contains));
        }

        static List<ServiceAction> RestoreLocked(List<string> names)
        {
            var done = new List<ServiceAction>();
            foreach (string name in names)
            {
                ServiceRule rule = ServiceCatalog.Find(name);
                var a = new ServiceAction { Name = name, Label = rule != null ? rule.Label : name };
                try
                {
                    using (var sc = new ServiceController(name)) RestoreOne(new ServiceControl(sc), a, StopTimeout);
                    if (a.Started) Forget(name);
                }
                catch (InvalidOperationException ex)
                {
                    // Uninstalled, or a per-user instance from an earlier sign-in: nothing left to start.
                    if (IsMissingService(ex)) { Mark(a, ServiceOutcome.SkippedNotInstalled, "no longer installed"); Forget(name); }
                    else Mark(a, ServiceOutcome.StartFailed, Describe(ex));
                }
                done.Add(a);
            }
            return done;
        }

        /// <summary>
        /// Starts one service again and records the outcome in a. A service still stopping (its stop timed out in
        /// Stop) is waited for first, then started; a paused one is continued. Tests pass a fake service.
        /// </summary>
        internal static void RestoreOne(IServiceControl sc, ServiceAction a, TimeSpan timeout)
        {
            ServiceControllerStatus status = sc.Status;
            if (status == ServiceControllerStatus.StopPending)
            {
                try { sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout); }
                catch (System.ServiceProcess.TimeoutException)
                {
                    Mark(a, ServiceOutcome.StillStopping, "was still stopping after " + timeout.TotalSeconds + " s");
                    return;
                }
                status = ServiceControllerStatus.Stopped;
            }
            try
            {
                if (status == ServiceControllerStatus.Stopped) sc.Start();
                else if (status == ServiceControllerStatus.Paused) sc.Continue();
                sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                a.Outcome = ServiceOutcome.Started;
            }
            catch (System.ServiceProcess.TimeoutException) { Mark(a, ServiceOutcome.StartTimedOut, "still starting after " + timeout.TotalSeconds + " s"); }
        }

        /// <summary>The ServiceController calls RestoreOne makes.</summary>
        internal interface IServiceControl
        {
            ServiceControllerStatus Status { get; }
            void Start();
            void Continue();
            /// <summary>Throws System.ServiceProcess.TimeoutException when the status is not reached in time.</summary>
            void WaitForStatus(ServiceControllerStatus status, TimeSpan timeout);
        }

        sealed class ServiceControl : IServiceControl
        {
            readonly ServiceController sc;
            public ServiceControl(ServiceController sc) { this.sc = sc; }
            public ServiceControllerStatus Status { get { sc.Refresh(); return sc.Status; } }
            public void Start() { sc.Start(); }
            public void Continue() { sc.Continue(); }
            public void WaitForStatus(ServiceControllerStatus status, TimeSpan timeout) { sc.WaitForStatus(status, timeout); }
        }

        static string Describe(InvalidOperationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            return inner.Message.TrimEnd('.');
        }

        /// <summary>
        /// One line for the clean result: "stopped 3 services" plus what was skipped or failed and why. Which actions
        /// are named follows their outcome (ServiceOutcomes.ShownInSummary), never their text.
        /// </summary>
        public static string Summarise(IList<ServiceAction> actions)
        {
            int stopped = 0;
            var notes = new List<string>();
            foreach (ServiceAction a in actions)
            {
                ServiceOutcome o = ServiceOutcomes.Of(a);
                if (o == ServiceOutcome.Stopped) stopped++;
                else if (ServiceOutcomes.ShownInSummary(o)) notes.Add(a.Label + ": " + a.Message);
            }
            string head = stopped == 0 ? "no services needed stopping"
                : "stopped " + stopped + (stopped == 1 ? " service" : " services");
            return notes.Count == 0 ? head : head + "; " + string.Join("; ", notes.ToArray());
        }
    }

    /// <summary>
    /// Stops one service and nothing else. ServiceController.Stop() first stops every running dependent
    /// (.NET Framework reference source, System.ServiceProcess/ServiceController.cs), which would reach services
    /// outside the catalog; ControlService refuses with ERROR_DEPENDENT_SERVICES_RUNNING instead
    /// (learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-controlservice).
    /// </summary>
    internal static class NativeService
    {
        public const int ErrorDependentServicesRunning = 1051;
        public const int ErrorServiceNotActive = 1062;
        const uint SC_MANAGER_CONNECT = 0x0001, SERVICE_STOP = 0x0020, SERVICE_CONTROL_STOP = 1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SERVICE_STATUS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool CloseServiceHandle(IntPtr h);

        /// <summary>Sends the stop control. Returns 0 when it was accepted, otherwise the Win32 error code.</summary>
        public static int StopOnly(string name)
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            try
            {
                IntPtr svc = OpenServiceW(scm, name, SERVICE_STOP);
                if (svc == IntPtr.Zero) return System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                try
                {
                    SERVICE_STATUS status;
                    return ControlService(svc, SERVICE_CONTROL_STOP, out status) ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }
    }
}
