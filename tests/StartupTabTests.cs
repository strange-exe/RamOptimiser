// Tests for the Startup tab's own decisions (package 6A): packaged and RunOnce rows shown read-only (E24), the
// publisher line and the "look closer" marker (E26), typed outcomes shown as an error or a note under the row (E13),
// the journal sentence in a service's detail and the tidy after a load (E12). The decisions are pure AppText
// functions; the WPF wiring in AppShell.cs and MainWindow.xaml is checked from its source text, never run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class StartupTabTests
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
            AppDetailTests();
            LookCloserTests();
            LockTests();
            CountTests();
            KeyAndSettingsLinkTests();
            RowNoteTests();
            ServiceDetailTests();
            NoLegacySettersTests();
            WiringTests();
            ReviewFixTests();
            return failed;
        }

        // ---------------------------------------------------------------- review fixes (6A integration)

        static void ReviewFixTests()
        {
            // A host (rundll32, wscript, powershell) running another file: its own signature says nothing about
            // that file, so the row is marked whatever the host's signature, Windows' own hosts included.
            var host = App(StartupSource.UserRun, "Updater");
            host.ExePath = @"C:\Windows\System32\rundll32.exe";
            host.Signature = SignatureState.Unsigned;
            host.SystemOwned = true;
            host.RunsFile = true;
            Check("look closer: a Windows host running another file", AppText.LookCloser(host, Win) == AppText.LookCloserText);
            host.Signature = SignatureState.Signed;
            host.Signer = "Microsoft Windows";
            Check("look closer: a signed host running another file", AppText.LookCloser(host, Win) == AppText.LookCloserText);
            host.Signature = SignatureState.NotChecked;
            Check("look closer: an unchecked host is not flagged", AppText.LookCloser(host, Win) == null);
            host.RunsFile = false;
            host.Signature = SignatureState.Unsigned;
            Check("look closer: a Windows file running nothing else is still fine", AppText.LookCloser(host, Win) == null);

            // A locked row's name carries the marker too, so it is never visual only.
            Check("locked name: marker", AppText.StartupLockedName("Updater", "Needs admin", true, false, AppText.LookCloserText)
                == "Updater: Needs admin, starts with Windows. Look closer: see who signed it",
                AppText.StartupLockedName("Updater", "Needs admin", true, false, AppText.LookCloserText));
            Check("locked name: plain matches LockedRowName", AppText.StartupLockedName("Tool", "Left on", false, false, null)
                == AppText.LockedRowName("Tool", "Left on", false));
            // A run-once row never "starts with Windows": the summary does not count it either.
            Check("locked name: run once", AppText.StartupLockedName("Setup", "Runs once", true, true, null)
                == "Setup: Runs once at the next sign-in", AppText.StartupLockedName("Setup", "Runs once", true, true, null));
            Check("locked name: run once with marker", AppText.StartupLockedName("Setup", "Runs once", true, true, AppText.LookCloserText)
                == "Setup: Runs once at the next sign-in. Look closer: see who signed it");

            // A packaged row that a policy decides: Settings can't change it, so the lock says who decides.
            var pol = App(StartupSource.Packaged, "Teams");
            pol.PolicySet = true;
            Check("lock: policy-set packaged row", AppText.AppLock(pol, true) == "Set by policy", AppText.AppLock(pol, true));
            Check("settings link: hidden when every packaged row is policy-set", !AppText.ShowsStartupSettings(true, new[] { pol }));
            var free = App(StartupSource.Packaged, "Phone");
            Check("settings link: shown with one packaged row Settings can change", AppText.ShowsStartupSettings(true, new[] { pol, free }));

            // A run-once row has nothing to turn off: no verdict from the list, no "If off" sentence.
            var once = App(StartupSource.UserRunOnce, "Discord");
            once.Known = StartupKnowledge.FindApp("Discord.exe");
            Check("verdict: run-once row has no list verdict", AppText.AppVerdict(once) == Verdict.Unknown);
            Check("verdict: run-once words", AppText.AppVerdictText(once) == "Runs once: nothing to turn off", AppText.AppVerdictText(once));
            string d = AppText.AppDetail(once, Win);
            Check("app detail: run-once row has no If off", d.StartsWith(once.Known.Purpose, StringComparison.Ordinal) && !d.Contains("If off"), d);
            var run = App(StartupSource.UserRun, "Discord");
            run.Known = once.Known;
            Check("verdict: a Run row keeps the list verdict", AppText.AppVerdict(run) == once.Known.Verdict);
            Check("verdict: a Run row keeps today's words", AppText.AppVerdictText(run) == AppText.VerdictText(once.Known.Verdict, false));
            Check("verdict: unknown app words", AppText.AppVerdictText(App(StartupSource.UserRun, "X")) == AppText.VerdictText(Verdict.Unknown, false));
            Check("verdict: no row", AppText.AppVerdict(null) == Verdict.Unknown);
            Check("verdict words: a Run row uses VerdictText", AppText.AppVerdictWords(run) == null);
            Check("verdict words: run-once", AppText.AppVerdictWords(once) == AppText.RunOnceVerdictText);
            // A policy-set packaged row can't be turned off by the user anywhere: no list verdict either.
            pol.Known = once.Known;
            Check("verdict: policy-set row has no list verdict", AppText.AppVerdict(pol) == Verdict.Unknown);
            Check("verdict: policy-set words", AppText.AppVerdictText(pol) == "Set by policy: nothing to turn off", AppText.AppVerdictText(pol));
            free.Known = once.Known;
            Check("verdict: a packaged row Settings can change keeps the list verdict", AppText.AppVerdict(free) == once.Known.Verdict
                && AppText.AppVerdictWords(free) == null);

            // A company that ends in a full stop ("Discord Inc.") never doubles it, inside the detail or at its end.
            var inc = App(StartupSource.UserRunOnce, "Setup");
            inc.ClaimedCompany = "Discord Inc.";
            d = AppText.AppDetail(inc, Win);
            Check("app detail: no doubled full stop inside", d == "From Discord Inc. Runs once at the next sign-in, then Windows removes it", d);
            inc.Source = StartupSource.UserRun;
            inc.Signature = SignatureState.Signed;
            inc.Signer = "Discord Inc.";
            d = AppText.AppDetail(inc, Win);
            Check("detail line: ends with one full stop", AppText.DetailLine(d) == "Signed by Discord Inc.", AppText.DetailLine(d));
            Check("detail line: adds the full stop", AppText.DetailLine("Not signed") == "Not signed.");
            Check("detail line: empty", AppText.DetailLine(null) == "" && AppText.DetailLine("") == "");

            // E12: the tidy runs only after a successful read and never holds up the list (its lock is the one a
            // Power Clean holds while it stops services).
            string shellPath = SrcFile("App", "AppShell.cs");
            if (!File.Exists(shellPath)) { Check("review wiring: source found", false, shellPath); return; }
            string shell = File.ReadAllText(shellPath);
            string query = Method(shell, "void QueryStartup()");
            int post = query.IndexOf("Post(() =>", StringComparison.Ordinal);
            int tidy = query.IndexOf("StartupServices.TidyJournal()", StringComparison.Ordinal);
            Check("review wiring: tidy only after services were read", query.Contains("if (elevated && services != null)"), query);
            Check("review wiring: tidy after the list is handed over", post > 0 && tidy > post, post + " / " + tidy);
            string items = Method(shell, "List<StartupItem> CurrentItems(");
            Check("review wiring: app rows use AppVerdict", items.Contains("Verdict = AppText.AppVerdict(a)"), items);
            string row = Method(shell, "UIElement StartupRow(");
            Check("review wiring: locked rows use StartupLockedName with the marker",
                row.Contains("AppText.StartupLockedName(it.Label, it.LockText, it.On, it.RunsOnce, it.Marker)"), row);
            Check("review wiring: run-once verdict words", row.Contains("it.VerdictWords") && items.Contains("VerdictWords = AppText.AppVerdictWords(a)"), row);
            Check("review wiring: the detail line ends through DetailLine", row.Contains("SmallText(AppText.DetailLine(it.Detail))"), row);
        }

        const string Win = @"C:\Windows";

        static StartupApp App(StartupSource source, string name)
        {
            return new StartupApp { Source = source, Name = name };
        }

        // ---------------------------------------------------------------- E26 / E24: the row detail

        static void AppDetailTests()
        {
            // The claim alone no longer reads as a publisher: an unchecked row keeps today's "From ...".
            var a = App(StartupSource.MachineRun, "X");
            a.ClaimedCompany = "Contoso";
            string d = AppText.AppDetail(a, Win);
            Check("app detail: an unchecked claim reads as today", d == "From Contoso. Starts for every user", d);

            a = App(StartupSource.UserRun, "X");
            a.ExePath = @"C:\Users\x\x.exe";
            a.ClaimedCompany = "Microsoft Corporation";
            a.Signature = SignatureState.Unsigned;
            d = AppText.AppDetail(a, Win);
            Check("app detail: an unsigned row with a claim says the file says", d == "Not signed; the file says Microsoft Corporation", d);

            a.Signature = SignatureState.Signed;
            a.Signer = "Contoso Ltd";
            d = AppText.AppDetail(a, Win);
            Check("app detail: a mismatched signer is named with the claim",
                d == "Signed by Contoso Ltd, though the file says Microsoft Corporation", d);

            a.ClaimedCompany = "Contoso";
            d = AppText.AppDetail(a, Win);
            Check("app detail: a matching signer", d == "Signed by Contoso Ltd", d);

            // A row the built-in list knows still says what it is for and which file runs, then who signed it.
            a = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", ExePath = @"C:\Users\x\Discord.exe",
                Known = StartupKnowledge.FindApp("Discord.exe"), Signature = SignatureState.Signed, Signer = "Discord Inc." };
            d = AppText.AppDetail(a, Win);
            Check("app detail: known row ends with the publisher line", d.StartsWith("Opens the Discord", StringComparison.Ordinal)
                && d.Contains(". Runs C:\\Users\\x\\Discord.exe. Signed by Discord Inc."), d);
            Check("app detail: no exclamation marks", !d.Contains("!"), d);

            // The real Windows folder overload keeps working (AppShell calls it).
            a = App(StartupSource.UserRun, "Y");
            Check("app detail: one-argument form", AppText.AppDetail(a) == "Publisher unknown", AppText.AppDetail(a));

            // Packaged and RunOnce rows add the sentences that say why they have no switch.
            var p = App(StartupSource.Packaged, "WhatsApp");
            p.Packaged = PackagedState.Enabled;
            d = AppText.AppDetail(p, Win);
            Check("app detail: packaged says where to change it", d == "Publisher unknown. Store app. Turn it on or off in Settings, Apps, Startup", d);
            p.PolicySet = true;
            d = AppText.AppDetail(p, Win);
            Check("app detail: policy says who decides", d == "Publisher unknown. Your organisation controls whether it starts", d);

            var once = App(StartupSource.MachineRunOnce, "Setup");
            once.ClaimedCompany = "Contoso";
            d = AppText.AppDetail(once, Win);
            Check("app detail: run-once row", d == "From Contoso. Starts for every user. Runs once at the next sign-in, then Windows removes it", d);
            once.Source = StartupSource.UserRunOnce;
            d = AppText.AppDetail(once, Win);
            Check("app detail: per-user run-once row", d == "From Contoso. Runs once at the next sign-in, then Windows removes it", d);
        }

        // ---------------------------------------------------------------- E26: look closer

        static void LookCloserTests()
        {
            var a = App(StartupSource.UserRun, "X");
            a.ExePath = @"C:\Users\x\x.exe";
            Check("look closer: not checked is not flagged", AppText.LookCloser(a, Win) == null);
            a.Signature = SignatureState.Signed;
            a.Signer = "Contoso Ltd";
            Check("look closer: signed without a claim is fine", AppText.LookCloser(a, Win) == null);
            a.ClaimedCompany = "Contoso";
            Check("look closer: signed by the same company is fine", AppText.LookCloser(a, Win) == null);
            a.ClaimedCompany = "Microsoft Corporation";
            Check("look closer: a signer that differs from the claim", AppText.LookCloser(a, Win) == AppText.LookCloserText);
            a.Signature = SignatureState.Invalid;
            Check("look closer: invalid", AppText.LookCloser(a, Win) == AppText.LookCloserText);
            a.Signature = SignatureState.Distrusted;
            Check("look closer: blocked by Windows", AppText.LookCloser(a, Win) == AppText.LookCloserText);
            a.Signature = SignatureState.Unsigned;
            Check("look closer: unsigned outside Windows", AppText.LookCloser(a, Win) == AppText.LookCloserText);

            // Windows' own catalog-signed files carry no embedded signature: "not checked", not a warning.
            var w = App(StartupSource.MachineRun, "SecurityHealth");
            w.ExePath = @"C:\Windows\System32\SecurityHealthSystray.exe";
            w.Signature = SignatureState.Unsigned;
            w.SystemOwned = true;
            Check("look closer: a Windows file is not flagged", AppText.LookCloser(w, Win) == null);
            w.SystemOwned = false;
            Check("look closer: a user-written file in the Windows folder is", AppText.LookCloser(w, Win) == AppText.LookCloserText);

            Check("look closer: no row", AppText.LookCloser(null, Win) == null);
            Check("look closer: words are plain", AppText.LookCloserText == "Look closer");

            // The switch name says it too, so the marker is never only visual.
            Check("switch name: plain", AppText.StartupSwitchName("Steam", false, null) == "Steam, starts with Windows");
            Check("switch name: off by app", AppText.StartupSwitchName("Fax", true, null) == "Fax, starts with Windows. Turned off by this app");
            Check("switch name: look closer", AppText.StartupSwitchName("Updater", false, AppText.LookCloserText)
                == "Updater, starts with Windows. Look closer: see who signed it", AppText.StartupSwitchName("Updater", false, AppText.LookCloserText));
        }

        // ---------------------------------------------------------------- E24: read-only rows

        static void LockTests()
        {
            var keep = App(StartupSource.MachineRun, "SecurityHealth");
            keep.Known = StartupKnowledge.FindApp("securityhealthsystray.exe");
            Check("lock: keep is left on", AppText.AppLock(keep, true) == "Left on");
            Check("lock: keep wins over needs admin", AppText.AppLock(keep, false) == "Left on");

            var machine = App(StartupSource.MachineRun, "Tool");
            Check("lock: every-user row needs admin", AppText.AppLock(machine, false) == "Needs admin");
            Check("lock: every-user row elevated has a switch", AppText.AppLock(machine, true) == null);
            Check("lock: own row has a switch", AppText.AppLock(App(StartupSource.UserRun, "Tool"), false) == null);

            var p = App(StartupSource.Packaged, "Teams");
            Check("lock: packaged is in Settings", AppText.AppLock(p, true) == "In Settings" && AppText.AppLock(p, false) == "In Settings");
            p.Known = keep.Known;
            Check("lock: packaged stays In Settings even when kept", AppText.AppLock(p, true) == "In Settings");
            foreach (StartupSource s in new[] { StartupSource.UserRunOnce, StartupSource.MachineRunOnce, StartupSource.MachineRunOnce32 })
            {
                Check("lock: run once " + s, AppText.AppLock(App(s, "Setup"), true) == "Runs once");
                Check("lock: run once not elevated " + s, AppText.AppLock(App(s, "Setup"), false) == "Runs once");
            }
            // Every row without a reversible switch gets a lock, so no switch can reach SetEnabled's refusal.
            foreach (StartupSource s in (StartupSource[])Enum.GetValues(typeof(StartupSource)))
            {
                var r = App(s, "Any");
                Check("lock: a row without a switch is locked " + s, r.CanToggle || AppText.AppLock(r, true) != null);
            }
            Check("lock: no row", AppText.AppLock(null, true) == null);

            Check("lock glyph: shield for kept", AppText.AppLockGlyph(keep, false) == "IconShield");
            Check("lock glyph: lock for needs admin", AppText.AppLockGlyph(machine, false) == "IconLock");
            Check("lock glyph: none with a switch", AppText.AppLockGlyph(machine, true) == null);
            Check("lock glyph: none for a source lock", AppText.AppLockGlyph(p, false) == null
                && AppText.AppLockGlyph(App(StartupSource.MachineRunOnce, "Setup"), false) == null);
        }

        static void CountTests()
        {
            var safe = StartupKnowledge.FindApp("Discord.exe");
            var run = App(StartupSource.UserRun, "Discord"); run.Enabled = true; run.Known = safe;
            var off = App(StartupSource.UserRun, "Off"); off.Enabled = false; off.Known = safe;
            var once = App(StartupSource.UserRunOnce, "Setup"); once.Enabled = true; once.Known = safe;
            var pkg = App(StartupSource.Packaged, "Teams"); pkg.Enabled = true; pkg.Known = safe;
            var pkgOff = App(StartupSource.Packaged, "Phone"); pkgOff.Enabled = false;
            var list = new List<StartupApp> { run, off, once, pkg, pkgOff };
            int on, safeOn;
            AppText.CountStartupApps(list, out on, out safeOn);
            Check("summary: run-once rows are not counted, packaged ones are", on == 2, on.ToString());
            Check("summary: safe to turn off counts only rows with a switch here", safeOn == 1, safeOn.ToString());
            AppText.CountStartupApps(null, out on, out safeOn);
            Check("summary: no list", on == 0 && safeOn == 0);
        }

        static void KeyAndSettingsLinkTests()
        {
            // A Run value and a packaged task may share a name: each row keeps its own key (focus, errors, flow).
            string k1 = AppText.StartupAppKey(App(StartupSource.UserRun, "Teams"));
            string k2 = AppText.StartupAppKey(App(StartupSource.Packaged, "Teams"));
            string k3 = AppText.StartupAppKey(App(StartupSource.MachineRun, "Teams"));
            Check("key: per source", k1 != k2 && k1 != k3 && k2 != k3, k1 + " " + k2 + " " + k3);
            Check("key: app prefix", k1.StartsWith("app|", StringComparison.Ordinal) && !k1.EndsWith("|sw", StringComparison.Ordinal));

            var run = App(StartupSource.UserRun, "A");
            var pkg = App(StartupSource.Packaged, "B");
            Check("settings link: shown with a packaged row in the apps list", AppText.ShowsStartupSettings(true, new[] { run, pkg }));
            Check("settings link: hidden without packaged rows", !AppText.ShowsStartupSettings(true, new[] { run }));
            Check("settings link: hidden on the services list", !AppText.ShowsStartupSettings(false, new[] { run, pkg }));
            Check("settings link: hidden before a load", !AppText.ShowsStartupSettings(true, null));
            Check("settings link: fixed uri", AppText.StartupSettingsUri == "ms-settings:startupapps");
        }

        // ---------------------------------------------------------------- E13: a note or an error under the row

        static void RowNoteTests()
        {
            foreach (StartupChangeKind k in (StartupChangeKind[])Enum.GetValues(typeof(StartupChangeKind)))
            {
                foreach (bool on in new[] { false, true })
                {
                    RowNote n = AppText.ServiceChangeNote(new StartupChange { Kind = k, Detail = "The service did not respond." }, "Fax", on);
                    if (!StartupChanges.IsOk(k))
                    {
                        Check("row note: refusal is an error " + k, n != null && n.IsError
                            && n.Title == AppText.ChangeFailedTitle("Fax", on)
                            && n.Text == AppText.ChangeFailedText(AppText.StartupChangeReason(k, "The service did not respond."), on),
                            n == null ? "null" : n.Title + " / " + n.Text);
                    }
                    else if (AppText.StartupChangeNote(k) != null)
                    {
                        Check("row note: partial change is a note " + k, n != null && !n.IsError && n.Title == "Fax"
                            && n.Text == AppText.StartupChangeNote(k), n == null ? "null" : n.Title + " / " + n.Text);
                    }
                    else Check("row note: a change that went as asked has none " + k, n == null);
                }
            }
            RowNote failed = AppText.ServiceChangeNote(new StartupChange { Kind = StartupChangeKind.Failed, Detail = "The service did not respond." }, "Fax", false);
            Check("row note: failure text", failed.Text == "The service did not respond. It is still on.", failed.Text);
            RowNote refused = AppText.ServiceChangeNote(new StartupChange { Kind = StartupChangeKind.RefusedNotAtStartup }, "Fax", false);
            Check("row note: today's refusal words", refused.Title == "Couldn't turn off Fax" && refused.Text == "Does not start with Windows. It is still on.",
                refused.Title + " / " + refused.Text);
            RowNote dependents = AppText.ServiceChangeNote(new StartupChange { Kind = StartupChangeKind.TurnedOffStillRunningDependents }, "Fax", false);
            Check("row note: dependents note", dependents.Text == "Won't start with Windows. Other services need it, so it keeps running until you restart.");
            Check("row note: no change, no note", AppText.ServiceChangeNote(null, "Fax", false) == null);

            // How each kind is drawn: never colour alone (glyph plus words), and the dismiss button says which.
            Check("row note: error look", failed.Glyph == "IconError" && failed.Stroke == "Danger" && failed.BarStyle == "InfoBar"
                && failed.DismissName == "Dismiss error");
            Check("row note: note look", dependents.Glyph == "IconWarn" && dependents.Stroke == "Caution" && dependents.BarStyle == "NoticeBar"
                && dependents.DismissName == "Dismiss note");
            Check("row note: spoken text", dependents.Spoken == "Fax. Won't start with Windows. Other services need it, so it keeps running until you restart.",
                dependents.Spoken);

            RowNote app = AppText.AppChangeError("Steam", true, AppText.NeedsAdmin);
            Check("row note: app change error", app.IsError && app.Title == "Couldn't turn on Steam"
                && app.Text == "Needs administrator rights. It is still off.", app.Title + " / " + app.Text);
        }

        // ---------------------------------------------------------------- E12: the journal sentence

        static void ServiceDetailTests()
        {
            var svc = new StartupService { Name = "ArmouryCrateService", Mode = StartMode.Automatic, Running = true, SharedWith = 1,
                MemoryBytes = 74L * 1024 * 1024, Known = StartupKnowledge.FindService("ArmouryCrateService"), Journal = JournalState.TurnedBackOn };
            string d = AppText.ServiceDetail(svc);
            Check("service detail: turned back on says so after the start label",
                d.Contains("Starts with Windows. Windows or an updater turned it back on after this app turned it off. Uses 74.0 MB"), d);
            Check("service detail: no exclamation", !d.Contains("!"), d);

            svc.Mode = StartMode.Disabled;
            svc.Journal = JournalState.OffElsewhere;
            d = AppText.ServiceDetail(svc);
            Check("service detail: off elsewhere", d.Contains("Disabled. Turned off by someone else after this app changed it, so the app leaves it alone"), d);

            svc.Mode = StartMode.Manual;
            svc.Journal = JournalState.OffByApp;
            d = AppText.ServiceDetail(svc);
            Check("service detail: off by app unchanged", d.Contains("Turned off by this app; starts only when something asks for it")
                && !d.Contains("updater") && !d.Contains("someone else"), d);
            svc.Journal = JournalState.Untouched;
            d = AppText.ServiceDetail(svc);
            Check("service detail: untouched has no journal sentence", !d.Contains("this app") && d.Contains("Starts only when something asks for it"), d);
        }

        // ---------------------------------------------------------------- E13 / E12 carry-over: shims removed

        static void NoLegacySettersTests()
        {
            foreach (string name in new[] { "Stopped", "Started", "Failed" })
            {
                PropertyInfo p = typeof(ServiceAction).GetProperty(name);
                Check("legacy: ServiceAction." + name + " is read-only", p != null && !p.CanWrite);
            }
            PropertyInfo byApp = typeof(StartupService).GetProperty("DisabledByApp");
            Check("legacy: StartupService.DisabledByApp is read-only", byApp != null && !byApp.CanWrite);
            PropertyInfo ok = typeof(StartupChange).GetProperty("Ok");
            Check("legacy: StartupChange.Ok is read-only", ok != null && !ok.CanWrite);
            PropertyInfo message = typeof(StartupChange).GetProperty("Message");
            Check("legacy: StartupChange.Message is read-only", message != null && !message.CanWrite);

            // No logic branches on English text: an action with only a reason is still pending.
            Check("legacy: the old not-running text is not an outcome",
                ServiceOutcomes.Of(new ServiceAction { Message = "not running" }) == ServiceOutcome.Pending);
            Check("legacy: a reason without an outcome is not a skip",
                ServiceOutcomes.Of(new ServiceAction { Message = "needed by X" }) == ServiceOutcome.Pending);
            Check("legacy: the outcome is what counts",
                ServiceOutcomes.Of(new ServiceAction { Outcome = ServiceOutcome.SkippedCannotStop, Message = "not running" }) == ServiceOutcome.SkippedCannotStop);
            Check("legacy: no action is pending", ServiceOutcomes.Of(null) == ServiceOutcome.Pending);
        }

        // ---------------------------------------------------------------- the WPF wiring (source text)

        static string SrcFile(params string[] parts)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..");
            path = Path.Combine(path, "src");
            foreach (string p in parts) path = Path.Combine(path, p);
            return Path.GetFullPath(path);
        }

        /// <summary>The text of one method: from its signature to the next line that closes a member at this indent.</summary>
        static string Method(string code, string signature)
        {
            int at = code.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = code.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            return end < 0 ? code.Substring(at) : code.Substring(at, end - at);
        }

        static void WiringTests()
        {
            string shellPath = SrcFile("App", "AppShell.cs"), xamlPath = SrcFile("App", "Xaml", "MainWindow.xaml");
            if (!File.Exists(shellPath) || !File.Exists(xamlPath)) { Check("wiring: sources found", false, shellPath); return; }
            string shell = File.ReadAllText(shellPath), xaml = File.ReadAllText(xamlPath);

            // E24: the tab lists the extra sources; E12: an elevated load tidies the journal after it was read.
            string query = Method(shell, "void QueryStartup()");
            Check("wiring: the tab reads packaged and run-once rows", query.Contains("StartupApps.Query(true)"), query);
            int read = query.IndexOf("StartupServices.Query(out journalProblem)", StringComparison.Ordinal);
            int tidy = query.IndexOf("StartupServices.TidyJournal()", StringComparison.Ordinal);
            Check("wiring: tidy runs after the services were read", read > 0 && tidy > read, read + " / " + tidy);
            // A refused ServiceStartup key reads as empty; its reason must reach error.log, not be dropped.
            int logged = query.IndexOf("journal was not used: \" + journalProblem", StringComparison.Ordinal);
            Check("wiring: a refused service journal is logged", read > 0 && logged > read, read + " / " + logged);
            Check("wiring: tidy only when elevated", Regex.IsMatch(query, @"if \(elevated && services != null\)[\s\S]{0,160}StartupServices\.TidyJournal\(\)"), query);

            // E13: the catch builds a typed failure; the row note decides error or note.
            string change = Method(shell, "void SetServiceStartup(");
            Check("wiring: a thrown change is Kind = Failed", change.Contains("Kind = StartupChangeKind.Failed") && !change.Contains("Ok = false"), change);
            Check("wiring: the result goes through ServiceChangeNote", change.Contains("AppText.ServiceChangeNote(r, it.Label, on)"), change);
            Check("wiring: row notes are typed", shell.Contains("Dictionary<string, RowNote> rowErrors"));
            string bar = Method(shell, "UIElement ErrorBar(");
            Check("wiring: the bar draws the note's own look", bar.Contains("note.Glyph") && bar.Contains("note.Stroke") && bar.Contains("note.BarStyle")
                && bar.Contains("note.DismissName"), bar);
            string app = Method(shell, "void SetAppEnabled(");
            Check("wiring: an app change error is a RowNote", app.Contains("AppText.AppChangeError("), app);

            // E24 / E26: the rows use the pure decisions.
            string items = Method(shell, "List<StartupItem> CurrentItems(");
            Check("wiring: app rows use AppLock and the row key", items.Contains("AppText.AppLock(a, elevated)") && items.Contains("AppText.StartupAppKey(a)"), items);
            Check("wiring: app rows carry the look-closer marker", items.Contains("AppText.LookCloser(a)"), items);
            Check("wiring: no switch for a row that can't toggle", items.Contains("a.CanToggle ? ") || items.Contains("if (!a.CanToggle)"), items);
            string build = Method(shell, "void BuildStartupList()");
            Check("wiring: the summary counts through CountStartupApps", build.Contains("AppText.CountStartupApps(startupApps, out appsOn, out safeOn)"), build);
            Check("wiring: the settings link follows ShowsStartupSettings", build.Contains("AppText.ShowsStartupSettings(apps, startupApps)"), build);
            Check("wiring: the switch name comes from StartupSwitchName", shell.Contains("AppText.StartupSwitchName(it.Label, it.ByApp, it.Marker)"));
            Check("wiring: the settings button opens the fixed uri", shell.Contains("ProcessStartInfo(AppText.StartupSettingsUri) { UseShellExecute = true }"));
            Check("wiring: xaml has the settings button", Regex.IsMatch(xaml, "<Button x:Name=\"OpenStartupSettings\"[^>]*AutomationProperties.Name=\"Open Startup settings\""));
            Check("wiring: xaml activity rows are a named group", Regex.IsMatch(xaml, "<app:GroupPanel x:Name=\"ActivityRows\"[^>]*AutomationProperties.Name=\"Recent activity\""));
        }
    }
}
