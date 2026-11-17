using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.ServiceProcess;

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

    public sealed class ServiceAction
    {
        public string Name;
        public string Label;
        public bool Stopped;
        public bool Started;
        /// <summary>A stop or start was tried and did not finish; a planned skip is not a failure.</summary>
        public bool Failed;
        public string Message;   // why it was skipped or failed; null when it worked
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
                if (s.OtherSession) a.Message = "belongs to another signed-in user";
                else if (!s.Running) a.Message = "not running";
                else if (!s.CanStop) a.Message = "does not accept a stop right now";
                else if (s.RunningDependents.Count > 0) a.Message = "needed by " + string.Join(", ", s.RunningDependents.ToArray());
                plan.Add(a);
            }
            return plan;
        }

        /// <summary>
        /// Held by every action that stops, starts or reconfigures a service (Power Clean, Restore, the Startup
        /// menu), so two of them never act on the same service at once. Taken only on background threads.
        /// </summary>
        internal static readonly object Operation = new object();

        /// <summary>Stops every planned service that has no skip reason. Needs administrator rights.</summary>
        public static List<ServiceAction> Stop(ICollection<string> chosen)
        {
            lock (Operation)
            {
                List<ServiceAction> plan = Plan(Query(), chosen);
                foreach (ServiceAction a in plan)
                {
                    if (a.Message != null) continue;
                    int err = NativeService.StopOnly(a.Name);
                    if (err == NativeService.ErrorDependentServicesRunning) { a.Message = "needed by a service that just started"; continue; }
                    if (err == NativeService.ErrorServiceNotActive) { a.Message = "not running"; continue; }
                    if (err != 0) { a.Failed = true; a.Message = new System.ComponentModel.Win32Exception(err).Message.TrimEnd('.'); continue; }
                    // The stop was accepted: from here Restore must be able to start it again, even if the wait times out.
                    lock (gate) if (!stoppedThisSession.Contains(a.Name)) stoppedThisSession.Add(a.Name);
                    try
                    {
                        using (var sc = new ServiceController(a.Name)) sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                        a.Stopped = true;
                    }
                    catch (System.ServiceProcess.TimeoutException) { a.Failed = true; a.Message = "still stopping after " + StopTimeout.TotalSeconds + " s"; }
                    catch (InvalidOperationException ex) { a.Failed = true; a.Message = Describe(ex); }
                }
                return plan;
            }
        }

        /// <summary>Starts again every service this process stopped. Ones already running count as restored.</summary>
        public static List<ServiceAction> Restore()
        {
            lock (Operation) return RestoreLocked();
        }

        static List<ServiceAction> RestoreLocked()
        {
            var done = new List<ServiceAction>();
            foreach (string name in StoppedThisSession())
            {
                ServiceRule rule = ServiceCatalog.Find(name);
                var a = new ServiceAction { Name = name, Label = rule != null ? rule.Label : name };
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, StopTimeout);
                    }
                    a.Started = true;
                    lock (gate) stoppedThisSession.Remove(name);
                }
                catch (System.ServiceProcess.TimeoutException) { a.Failed = true; a.Message = "still starting after " + StopTimeout.TotalSeconds + " s"; }
                catch (InvalidOperationException ex) { a.Failed = true; a.Message = Describe(ex); }
                done.Add(a);
            }
            return done;
        }

        static string Describe(InvalidOperationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            return inner.Message.TrimEnd('.');
        }

        /// <summary>One line for the clean result: "stopped 3 services" plus what was skipped and why.</summary>
        public static string Summarise(IList<ServiceAction> actions)
        {
            int stopped = 0;
            var notes = new List<string>();
            foreach (ServiceAction a in actions)
            {
                if (a.Stopped) stopped++;
                else if (a.Message != null && a.Message != "not running") notes.Add(a.Label + ": " + a.Message);
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
