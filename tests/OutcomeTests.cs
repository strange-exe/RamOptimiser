// Tests for typed outcomes (E13) and the service-startup journal states (E12), engine side: ServiceOutcome on
// every Power Clean and Restore action, StartupChangeKind on every Startup-tab change, the journal state
// machine and the pure tidy decision, plus their wording in AppText.Outcomes.cs.
// Nothing here touches a real service or the registry: the stop, wait and mode functions are fakes.
using System;
using System.Collections.Generic;
using System.Threading;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class OutcomeTests
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
            ServiceOutcomeTables();
            SummaryTests();
            PlanTests();
            StopPlannedTests();
            StartupChangeTests();
            StartupWordingTests();
            JournalTests();
            TidyTests();
            return failed;
        }

        static ServiceOutcome[] AllServiceOutcomes()
        {
            return (ServiceOutcome[])Enum.GetValues(typeof(ServiceOutcome));
        }

        static StartupChangeKind[] AllKinds()
        {
            return (StartupChangeKind[])Enum.GetValues(typeof(StartupChangeKind));
        }

        // ---------------------------------------------------------------- service outcomes

        static void ServiceOutcomeTables()
        {
            var failures = new HashSet<ServiceOutcome> { ServiceOutcome.StopFailed, ServiceOutcome.StopTimedOut,
                ServiceOutcome.StartFailed, ServiceOutcome.StartTimedOut, ServiceOutcome.StillStopping };
            var skips = new HashSet<ServiceOutcome> { ServiceOutcome.SkippedOtherSession, ServiceOutcome.SkippedNotRunning,
                ServiceOutcome.SkippedCannotStop, ServiceOutcome.SkippedDependents, ServiceOutcome.SkippedDependentStarted,
                ServiceOutcome.SkippedCancelled, ServiceOutcome.SkippedNotInstalled };
            foreach (ServiceOutcome o in AllServiceOutcomes())
            {
                Check("outcome: IsFailure " + o, ServiceOutcomes.IsFailure(o) == failures.Contains(o));
                Check("outcome: IsSkip " + o, ServiceOutcomes.IsSkip(o) == skips.Contains(o));
                bool shown = failures.Contains(o) || (skips.Contains(o) && o != ServiceOutcome.SkippedNotRunning);
                Check("outcome: ShownInSummary " + o, ServiceOutcomes.ShownInSummary(o) == shown);
            }
            Check("outcome: the default is pending", default(ServiceOutcome) == ServiceOutcome.Pending);
            Check("outcome: every value is a failure, a skip or a clean result",
                AllServiceOutcomes().Length == failures.Count + skips.Count + 3);

            var a = new ServiceAction { Outcome = ServiceOutcome.Stopped };
            Check("action: stopped derives from the outcome", a.Stopped && !a.Started && !a.Failed);
            a = new ServiceAction { Outcome = ServiceOutcome.Started };
            Check("action: started derives from the outcome", a.Started && !a.Stopped && !a.Failed);
            a = new ServiceAction { Outcome = ServiceOutcome.StillStopping };
            Check("action: failed derives from the outcome", a.Failed && !a.Started && !a.Stopped);
            a = new ServiceAction { Outcome = ServiceOutcome.SkippedCannotStop };
            Check("action: a skip is not a failure", !a.Failed && !a.Stopped && !a.Started);
        }

        static void SummaryTests()
        {
            // The text "not running" means nothing: only the outcome decides what the summary shows.
            string s = ServiceTrimmer.Summarise(new List<ServiceAction> {
                new ServiceAction { Label = "A", Outcome = ServiceOutcome.SkippedNotRunning, Message = "anything" },
                new ServiceAction { Label = "B", Outcome = ServiceOutcome.SkippedCannotStop, Message = "not running" } });
            Check("summary: skip is filtered by outcome, not by text", s == "no services needed stopping; B: not running", s);

            s = ServiceTrimmer.Summarise(new List<ServiceAction> {
                new ServiceAction { Label = "A", Outcome = ServiceOutcome.Stopped },
                new ServiceAction { Label = "B", Outcome = ServiceOutcome.SkippedNotRunning, Message = "not running" },
                new ServiceAction { Label = "C", Outcome = ServiceOutcome.SkippedDependents, Message = "needed by X" } });
            Check("summary: words unchanged with outcomes", s == "stopped 1 service; C: needed by X", s);

            s = ServiceTrimmer.Summarise(new List<ServiceAction> {
                new ServiceAction { Label = "A", Outcome = ServiceOutcome.Stopped },
                new ServiceAction { Label = "B", Outcome = ServiceOutcome.Stopped },
                new ServiceAction { Label = "C", Outcome = ServiceOutcome.StopTimedOut, Message = "still stopping after 10 s" } });
            Check("summary: failures are listed", s == "stopped 2 services; C: still stopping after 10 s", s);

            s = ServiceTrimmer.Summarise(new List<ServiceAction> { new ServiceAction { Label = "A", Outcome = ServiceOutcome.Pending } });
            Check("summary: a pending action says nothing", s == "no services needed stopping", s);
        }

        static ServiceState State(string name, bool running, bool canStop, bool other, params string[] dependents)
        {
            return new ServiceState { Name = name, DisplayName = name, Rule = ServiceCatalog.Find(name), Running = running,
                CanStop = canStop, OtherSession = other, RunningDependents = new List<string>(dependents) };
        }

        static void PlanTests()
        {
            var states = new List<ServiceState>
            {
                State("Spooler", true, true, false),
                State("Fax", false, false, false),
                State("SysMain", true, false, false),
                State("WSearch", true, true, false, "Dep1", "Dep2"),
                State("CDPUserSvc_1a2b3", true, true, true),
            };
            List<ServiceAction> plan = ServiceTrimmer.Plan(states, new[] { "Spooler", "Fax", "SysMain", "WSearch", "CDPUserSvc" });
            Check("plan: one action per chosen service", plan.Count == 5, plan.Count.ToString());
            if (plan.Count != 5) return;
            Check("plan: a stoppable service is pending", plan[0].Outcome == ServiceOutcome.Pending && plan[0].Message == null, plan[0].Outcome.ToString());
            Check("plan: not running", plan[1].Outcome == ServiceOutcome.SkippedNotRunning && plan[1].Message == "not running", plan[1].Outcome.ToString());
            Check("plan: does not accept a stop", plan[2].Outcome == ServiceOutcome.SkippedCannotStop
                && plan[2].Message == "does not accept a stop right now", plan[2].Outcome.ToString());
            Check("plan: needed by others", plan[3].Outcome == ServiceOutcome.SkippedDependents && plan[3].Message == "needed by Dep1, Dep2",
                plan[3].Outcome + " " + plan[3].Message);
            Check("plan: another user's instance", plan[4].Outcome == ServiceOutcome.SkippedOtherSession
                && plan[4].Message == "belongs to another signed-in user", plan[4].Outcome.ToString());
        }

        static List<ServiceAction> One()
        {
            return new List<ServiceAction> { new ServiceAction { Name = "A", Label = "Alpha" } };
        }

        static void StopPlannedTests()
        {
            var none = new List<string>();
            List<ServiceAction> plan = One();
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => true, CancellationToken.None, none.Add);
            Check("stop: stopped", plan[0].Outcome == ServiceOutcome.Stopped && plan[0].Message == null, plan[0].Outcome.ToString());

            plan = One();
            ServiceTrimmer.StopPlanned(plan, n => ServiceTrimmer.JournalWriteFailed, (n, t) => true, CancellationToken.None, none.Add);
            Check("stop: journal not saved is a stop failure", plan[0].Outcome == ServiceOutcome.StopFailed
                && plan[0].Message == "left running: the list of stopped services could not be saved", plan[0].Outcome.ToString());

            plan = One();
            ServiceTrimmer.StopPlanned(plan, n => NativeService.ErrorDependentServicesRunning, (n, t) => true, CancellationToken.None, none.Add);
            Check("stop: a dependent that just started", plan[0].Outcome == ServiceOutcome.SkippedDependentStarted
                && plan[0].Message == "needed by a service that just started", plan[0].Outcome.ToString());

            plan = One();
            ServiceTrimmer.StopPlanned(plan, n => NativeService.ErrorServiceNotActive, (n, t) => true, CancellationToken.None, none.Add);
            Check("stop: stopped meanwhile is not running", plan[0].Outcome == ServiceOutcome.SkippedNotRunning && plan[0].Message == "not running",
                plan[0].Outcome.ToString());

            plan = One();
            ServiceTrimmer.StopPlanned(plan, n => 5, (n, t) => true, CancellationToken.None, none.Add);
            Check("stop: refused", plan[0].Outcome == ServiceOutcome.StopFailed && !string.IsNullOrEmpty(plan[0].Message), plan[0].Outcome.ToString());

            plan = One();
            TimeSpan fake = TimeSpan.Zero;
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => { fake += t; return false; }, CancellationToken.None, none.Add, () => fake);
            Check("stop: timed out", plan[0].Outcome == ServiceOutcome.StopTimedOut && plan[0].Message == "still stopping after 10 s",
                plan[0].Outcome + " " + plan[0].Message);

            plan = One();
            var cts = new CancellationTokenSource();
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => { cts.Cancel(); return false; }, cts.Token, none.Add);
            Check("stop: a cancelled wait is still stopping", plan[0].Outcome == ServiceOutcome.StillStopping
                && plan[0].Message == "still stopping; clean was stopped", plan[0].Outcome.ToString());

            plan = One();
            cts = new CancellationTokenSource();
            cts.Cancel();
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => true, cts.Token, none.Add);
            Check("stop: not sent after a cancel is a skip", plan[0].Outcome == ServiceOutcome.SkippedCancelled
                && plan[0].Message == "left running: clean was stopped" && !plan[0].Failed, plan[0].Outcome.ToString());

            plan = One();
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => { throw new InvalidOperationException("gone"); }, CancellationToken.None, none.Add);
            Check("stop: a wait that throws is a stop failure", plan[0].Outcome == ServiceOutcome.StopFailed && plan[0].Message == "gone",
                plan[0].Outcome.ToString());

            // A planned skip keeps its outcome and is never sent a stop.
            plan = new List<ServiceAction> { new ServiceAction { Name = "A", Label = "Alpha", Outcome = ServiceOutcome.SkippedCannotStop, Message = "x" } };
            int calls = 0;
            ServiceTrimmer.StopPlanned(plan, n => { calls++; return 0; }, (n, t) => true, CancellationToken.None, none.Add);
            Check("stop: a typed skip is left alone", calls == 0 && plan[0].Outcome == ServiceOutcome.SkippedCannotStop);
        }

        // The legacy flag setters and the text fallback were removed in package 6A; StartupTabTests checks they are gone.

        // ---------------------------------------------------------------- startup changes

        static void StartupChangeTests()
        {
            var notOk = new HashSet<StartupChangeKind> { StartupChangeKind.RefusedNotListed, StartupChangeKind.RefusedNotAtStartup,
                StartupChangeKind.RefusedNotChangedByApp, StartupChangeKind.RefusedChangedElsewhere, StartupChangeKind.RefusedProtected,
                StartupChangeKind.RefusedNeedsAdmin, StartupChangeKind.Failed };
            foreach (StartupChangeKind k in AllKinds())
            {
                Check("change: IsOk " + k, StartupChanges.IsOk(k) == !notOk.Contains(k));
                Check("change: Ok follows the kind " + k, new StartupChange { Kind = k }.Ok == !notOk.Contains(k));
            }
            Check("change: 16 kinds (15 plus the delay not restored)", AllKinds().Length == 16, AllKinds().Length.ToString());

            // AppShell's catch builds { Kind = Failed, Detail }; that is a failure carrying that detail.
            var thrown = new StartupChange { Kind = StartupChangeKind.Failed, Detail = "boom" };
            Check("change: a thrown change is Failed", !thrown.Ok && thrown.Message == "boom", thrown.Kind + " " + thrown.Message);

            // The display-only Message keeps the refusal words StartupChangeReason gives the error bar.
            foreach (StartupChangeKind k in notOk)
            {
                var c = new StartupChange { Kind = k, Detail = k == StartupChangeKind.Failed ? "The service did not respond" : null };
                string want = AppText.StartupChangeReason(k, c.Detail);
                if (k == StartupChangeKind.Failed)
                {
                    Check("change: a failure's Message is the Windows text", c.Message == c.Detail, c.Message);
                    Check("change: the error bar reads a failure as its reason", AppText.Plain(c.Message, "unknown error") == want,
                        AppText.Plain(c.Message, "unknown error") + " / " + want);
                }
                else Check("change: Message matches the reason for " + k, c.Message == want, c.Message + " / " + want);
            }

            Check("change: denied when elevated is protected", StartupServices.DeniedKind(true) == StartupChangeKind.RefusedProtected);
            Check("change: denied when not elevated needs admin", StartupServices.DeniedKind(false) == StartupChangeKind.RefusedNeedsAdmin);

            Check("reenable: clean", StartupServices.ReenableKind("auto-delayed", true, true) == StartupChangeKind.TurnedOn);
            Check("reenable: delay not restored", StartupServices.ReenableKind("auto-delayed", false, true) == StartupChangeKind.TurnedOnDelayNotRestored);
            Check("reenable: plain auto ignores the delay call", StartupServices.ReenableKind("auto", false, true) == StartupChangeKind.TurnedOn);
            Check("reenable: start failed", StartupServices.ReenableKind("auto", true, false) == StartupChangeKind.TurnedOnStartFailed);
            // Not running now is what the user sees, so a failed start is reported even when the delay was lost too.
            Check("reenable: both, the failed start wins", StartupServices.ReenableKind("auto-delayed", false, false) == StartupChangeKind.TurnedOnStartFailed);
        }

        static void StartupWordingTests()
        {
            Check("reason: not listed", AppText.StartupChangeReason(StartupChangeKind.RefusedNotListed, null)
                == "not in the list of services that are safe to change");
            Check("reason: not at startup", AppText.StartupChangeReason(StartupChangeKind.RefusedNotAtStartup, null) == "does not start with Windows");
            Check("reason: not changed by app", AppText.StartupChangeReason(StartupChangeKind.RefusedNotChangedByApp, null) == "was not changed by this app");
            Check("reason: changed elsewhere", AppText.StartupChangeReason(StartupChangeKind.RefusedChangedElsewhere, null)
                == "its start type was changed after this app turned it off, so it was left as it is");
            Check("reason: protected", AppText.StartupChangeReason(StartupChangeKind.RefusedProtected, null)
                == "Windows protects this service, so its startup can't be changed");
            Check("reason: needs admin", AppText.StartupChangeReason(StartupChangeKind.RefusedNeedsAdmin, null) == AppText.NeedsAdmin);
            Check("reason: failed carries the Windows text", AppText.StartupChangeReason(StartupChangeKind.Failed, "The service did not respond.")
                == "the service did not respond");
            Check("reason: failed without text", AppText.StartupChangeReason(StartupChangeKind.Failed, null) == "unknown error");
            Check("reason: none for a change that worked", AppText.StartupChangeReason(StartupChangeKind.TurnedOn, null) == null);
            Check("reason: feeds the error text", AppText.ChangeFailedText(AppText.StartupChangeReason(StartupChangeKind.RefusedNotAtStartup, null), false)
                == "Does not start with Windows. It is still on.");

            var clean = new[] { StartupChangeKind.TurnedOff, StartupChangeKind.TurnedOffWasNotRunning, StartupChangeKind.TurnedOn };
            foreach (StartupChangeKind k in clean) Check("note: none for " + k, AppText.StartupChangeNote(k) == null);
            foreach (StartupChangeKind k in AllKinds())
            {
                if (Array.IndexOf(clean, k) >= 0) continue;
                string n = AppText.StartupChangeNote(k);
                if (!StartupChanges.IsOk(k)) { Check("note: none for a refusal " + k, n == null); continue; }
                Check("note: plain sentence for " + k, n != null && n.EndsWith(".", StringComparison.Ordinal) && !n.Contains("!")
                    && char.IsUpper(n[0]), n);
            }
            Check("note: dependents", AppText.StartupChangeNote(StartupChangeKind.TurnedOffStillRunningDependents)
                == "Won't start with Windows. Other services need it, so it keeps running until you restart.");
            Check("note: stop failed", AppText.StartupChangeNote(StartupChangeKind.TurnedOffStopFailed)
                == "Won't start with Windows. It could not be stopped now, so it keeps running until you restart.");
            Check("note: still stopping", AppText.StartupChangeNote(StartupChangeKind.TurnedOffStillStopping)
                == "Won't start with Windows. It is still stopping.");
            Check("note: start failed", AppText.StartupChangeNote(StartupChangeKind.TurnedOnStartFailed)
                == "Starts with Windows again. Starting it now failed, so it starts at the next restart.");
            Check("note: already on", AppText.StartupChangeNote(StartupChangeKind.AlreadyOn)
                == "Already starts with Windows: something else turned it back on.");
            Check("note: delay not restored", AppText.StartupChangeNote(StartupChangeKind.TurnedOnDelayNotRestored)
                == "Starts with Windows again, but not delayed as before.");
        }

        // ---------------------------------------------------------------- journal

        static void JournalTests()
        {
            StartMode[] modes = (StartMode[])Enum.GetValues(typeof(StartMode));
            foreach (StartMode m in modes)
            {
                Check("journal: no record is untouched " + m, StartupServices.ClassifyJournal(false, true, true, m) == JournalState.Untouched);
                Check("journal: no record, unlisted, is still untouched " + m, StartupServices.ClassifyJournal(false, false, false, m) == JournalState.Untouched);
                Check("journal: unlisted is orphan " + m, StartupServices.ClassifyJournal(true, false, true, m) == JournalState.Orphan);
                Check("journal: uninstalled is orphan " + m, StartupServices.ClassifyJournal(true, true, false, m) == JournalState.Orphan);
            }
            Check("journal: manual is off by app", StartupServices.ClassifyJournal(true, true, true, StartMode.Manual) == JournalState.OffByApp);
            Check("journal: automatic is turned back on", StartupServices.ClassifyJournal(true, true, true, StartMode.Automatic) == JournalState.TurnedBackOn);
            Check("journal: delayed is turned back on", StartupServices.ClassifyJournal(true, true, true, StartMode.AutomaticDelayed) == JournalState.TurnedBackOn);
            Check("journal: disabled is off elsewhere", StartupServices.ClassifyJournal(true, true, true, StartMode.Disabled) == JournalState.OffElsewhere);
            Check("journal: unreadable is orphan", StartupServices.ClassifyJournal(true, true, true, StartMode.Other) == JournalState.Orphan);

            Check("forget: untouched kept", !StartupServices.ShouldForget(JournalState.Untouched));
            Check("forget: off by app kept", !StartupServices.ShouldForget(JournalState.OffByApp));
            Check("forget: turned back on", StartupServices.ShouldForget(JournalState.TurnedBackOn));
            Check("forget: off elsewhere", StartupServices.ShouldForget(JournalState.OffElsewhere));
            Check("forget: orphan", StartupServices.ShouldForget(JournalState.Orphan));

            foreach (StartMode m in modes)
            {
                Check("journal: still holds wraps the classifier " + m,
                    StartupServices.JournalStillHolds(true, m) == (m == StartMode.Manual) && !StartupServices.JournalStillHolds(false, m));
            }

            var svc = new StartupService { Journal = JournalState.OffByApp };
            Check("service: disabled by app derives from the journal", svc.DisabledByApp);
            svc.Journal = JournalState.TurnedBackOn;
            Check("service: turned back on is not disabled by app", !svc.DisabledByApp);

            Check("journal note: turned back on", AppText.JournalNote(JournalState.TurnedBackOn)
                == "Windows or an updater turned it back on after this app turned it off");
            Check("journal note: off elsewhere", AppText.JournalNote(JournalState.OffElsewhere)
                == "Turned off by someone else after this app changed it, so the app leaves it alone");
            foreach (JournalState s in new[] { JournalState.TurnedBackOn, JournalState.OffElsewhere })
            {
                string n = AppText.JournalNote(s);
                Check("journal note: joins the detail without its own stop " + s, n != null && !n.Contains("!") && !n.EndsWith(".", StringComparison.Ordinal)
                    && char.IsUpper(n[0]), n);
            }
            Check("journal note: none when untouched", AppText.JournalNote(JournalState.Untouched) == null);
            Check("journal note: none when off by app (the start label says it)", AppText.JournalNote(JournalState.OffByApp) == null);
            Check("journal note: none for an orphan (no row)", AppText.JournalNote(JournalState.Orphan) == null);
        }

        static KeyValuePair<string, string> V(string name, string data) { return new KeyValuePair<string, string>(name, data); }

        static void TidyTests()
        {
            var values = new List<KeyValuePair<string, string>>
            {
                V("Manual1", "auto"),
                V("Auto1", "auto"),
                V("Delayed1", "auto-delayed"),
                V("Disabled1", "auto"),
                V("Gone1", "auto"),
                V("Unlisted1", "auto"),
                V("Malformed1", "manual"),
                V("Malformed2", null),
                V("Throws1", "auto"),
                V("Manual2", "auto-delayed"),
            };
            var modes = new Dictionary<string, StartMode>
            {
                { "Manual1", StartMode.Manual }, { "Auto1", StartMode.Automatic }, { "Delayed1", StartMode.AutomaticDelayed },
                { "Disabled1", StartMode.Disabled }, { "Gone1", StartMode.Other }, { "Manual2", StartMode.Manual },
            };
            var asked = new List<string>();
            List<string> forget = StartupServices.NamesToForget(values, n => n != "Unlisted1" && !n.StartsWith("Malformed", StringComparison.Ordinal),
                n =>
                {
                    asked.Add(n);
                    if (n == "Throws1") throw new InvalidOperationException("access denied");
                    return modes[n];
                });
            string got = string.Join(",", forget.ToArray());
            Check("tidy: malformed data is forgotten", forget.Contains("Malformed1") && forget.Contains("Malformed2"), got);
            Check("tidy: an unlisted name is forgotten", forget.Contains("Unlisted1"), got);
            Check("tidy: manual is kept", !forget.Contains("Manual1") && !forget.Contains("Manual2"), got);
            Check("tidy: auto is forgotten", forget.Contains("Auto1") && forget.Contains("Delayed1"), got);
            Check("tidy: disabled is forgotten", forget.Contains("Disabled1"), got);
            Check("tidy: uninstalled is forgotten", forget.Contains("Gone1"), got);
            Check("tidy: a mode that throws skips only that name", !forget.Contains("Throws1") && forget.Contains("Manual2") == false
                && asked.Contains("Manual2"), got);
            Check("tidy: the exact list, in key order", got == "Auto1,Delayed1,Disabled1,Gone1,Unlisted1,Malformed1,Malformed2", got);
            Check("tidy: modes read only for listed, well-formed names", !asked.Contains("Unlisted1") && !asked.Contains("Malformed1")
                && !asked.Contains("Malformed2"), string.Join(",", asked.ToArray()));
            Check("tidy: nothing in, nothing out", StartupServices.NamesToForget(null, n => true, n => StartMode.Manual).Count == 0);

            // A name ServiceController refuses (ArgumentException, e.g. longer than 80 characters) keeps only that
            // record; the rest of the tidy goes on.
            string tooLong = "CDPUserSvc_" + new string('a', 80);
            var odd = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(tooLong, "auto"),
                new KeyValuePair<string, string>("Auto2", "auto")
            };
            List<string> after = StartupServices.NamesToForget(odd, n => true, n =>
            {
                if (n.Length > 80) throw new ArgumentException("name too long");
                return StartMode.Automatic;
            });
            Check("tidy: an argument error skips only that name", after.Count == 1 && after[0] == "Auto2", string.Join(",", after.ToArray()));
        }
    }
}
