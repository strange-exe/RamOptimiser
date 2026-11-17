using System;
using System.Collections.Generic;
using System.Threading;

namespace RamOptimiser.Gui
{
    public enum CleanReason { Manual, Threshold, Interval }

    /// <summary>Where a failure came from: reading memory, the clean itself, or anything else (an event handler).</summary>
    public enum ServiceErrorSource { Sample, Clean, Other }

    /// <summary>One clean as the service asks for it: the areas, why, its time limit and the id its log record carries.</summary>
    public sealed class CleanJob
    {
        public Areas Areas;
        public CleanReason Reason;
        /// <summary>The automatic deadline for threshold and interval cleans; null for a manual clean (no limit).</summary>
        public TimeSpan? Deadline;
        public string Id;
    }

    /// <summary>
    /// Samples memory on a thread-pool timer and cleans when the settings say so. Never touches the UI thread:
    /// every event is raised on a worker thread, so a GUI must marshal to its dispatcher. At most one clean
    /// runs at a time, whoever asks for it. Exceptions from the sample or clean functions, or from event
    /// handlers, are reported through Error and never stop the timer. The user can pause automatic cleans for up to
    /// an hour (Pause, Resume); manual cleans always run. Automatic cleans run under a time limit, manual ones under
    /// none. This session's cleans are kept in History, with the load five minutes later (Rebound).
    /// </summary>
    public sealed class AutoCleanService : IDisposable
    {
        readonly Func<MemorySnapshot> sample;
        readonly Func<CleanJob, CleanReport> clean;
        readonly TimeSpan autoDeadline;
        readonly Func<DateTime> clock;
        readonly Func<TimeSpan> awakeClock;     // null: no sleep-gap detection
        readonly Func<Conditions> conditions;   // null: presence and power always Unknown
        readonly GateOptions options;
        readonly object gate = new object();
        readonly object idle = new object();   // pulsed when a clean ends (WaitUntilIdle)

        volatile AppSettings settings = new AppSettings();
        Timer timer;
        int timerGeneration;     // bumped by Stop/Start so a callback from an old timer does nothing
        DateTime? lastClean;     // when the last clean (any reason) started; guarded by gate
        DateTime intervalFrom;   // last clean, or Start() before the first; moved to now if the clock goes back; guarded by gate
        int cleaning;            // 1 while a clean runs (Interlocked)
        int cleanAreas;          // the running clean's Areas, 0 before it has read them and after it ends (Volatile)
        int ticking;             // 1 while a tick runs (Interlocked)
        int futileStreak;        // automatic cleans in a row that left load at or above the threshold; guarded by gate
        DateTime? pauseUntil;    // automatic cleans wait until then; never more than PauseLength after the clock; guarded by gate
        bool disposed;
        AutoCleanGate hold = AutoCleanGate.Initial; // sleep, wake grace, presence and power; guarded by gate
        DateTime lastTickWall;   // both clocks at the previous tick, for the sleep-gap check; guarded by gate
        TimeSpan lastTickAwake;
        bool haveLastTick;       // false until a tick after Start, so the first tick never measures a gap; guarded by gate
        HoldReason shownReason;  // the reason HoldChanged last carried; guarded by gate
        volatile bool awakeBroken; // the awake clock failed once: gap detection is off for the service's life
        readonly CleanHistory history = new CleanHistory(); // this session's cleans; guarded by gate

        /// <summary>Each clean in a row that leaves load at or above the threshold doubles the wait, up to this many times.</summary>
        public const int MaxBackoffSteps = AutoCleanPolicy.MaxBackoffSteps;

        /// <summary>
        /// The one pause the app offers, and the longest any pause can last: a pause restored from settings.ini (which
        /// the user can edit) or stretched by a clock set back is cut to this much from the clock's time.
        /// </summary>
        public static readonly TimeSpan PauseLength = TimeSpan.FromHours(1);

        /// <summary>
        /// Automatic cleans in a row that did not bring load under the threshold (0 to MaxBackoffSteps). A manual clean
        /// can reset it (load fell under the threshold) but never adds to it: the user chose that clean.
        /// </summary>
        public int FutileStreak { get { lock (gate) return futileStreak; } }

        public event Action<MemorySnapshot> Sampled;
        public event Action<CleanReason> CleanStarted;
        /// <summary>Always follows CleanStarted. The report is null when the clean threw (Error was raised first).</summary>
        public event Action<CleanReport, CleanReason> CleanCompleted;
        public event Action<Exception> Error;
        /// <summary>
        /// Raised with Error for every failure, tagged with its source: a manual clean runs beside the sampling
        /// timer, so a sampling failure can arrive while a clean runs and must not be taken for the clean's.
        /// </summary>
        public event Action<Exception, ServiceErrorSource> Failed;
        /// <summary>Why automatic cleans wait changed (raised on the tick's worker thread, once per change).</summary>
        public event Action<HoldReason> HoldChanged;
        /// <summary>
        /// The first sample about five minutes after a clean (CleanHistory.ReboundDelay): the entry, with LoadLater set,
        /// and that sample. Raised on the tick's worker thread, after Sampled, at most once per clean.
        /// </summary>
        public event Action<CleanHistoryEntry, MemorySnapshot> Rebound;

        /// <summary>No sleep-gap detection, presence or power: nothing is ever held except by the suspend and resume calls.</summary>
        /// <param name="clock">Use () => DateTime.UtcNow in the app: local time jumps at DST changes.</param>
        public AutoCleanService(Func<MemorySnapshot> sample, Func<Areas, CleanReport> clean, Func<DateTime> clock)
            : this(sample, clean, clock, null, null, null)
        {
        }

        /// <param name="clock">Use () => DateTime.UtcNow in the app: local time jumps at DST changes.</param>
        /// <param name="awakeClock">Time spent awake (SystemConditions.AwakeTime), or null for no sleep-gap detection.
        /// If it throws once, it is not called again.</param>
        /// <param name="conditions">Presence and power now (SystemConditions.Read), or null for Unknown. A throw counts as Unknown.</param>
        /// <param name="options">Hold timings and policy, or null for the defaults.</param>
        public AutoCleanService(Func<MemorySnapshot> sample, Func<Areas, CleanReport> clean, Func<DateTime> clock,
            Func<TimeSpan> awakeClock, Func<Conditions> conditions, GateOptions options)
            : this(sample, AreasOnly(clean), clock, awakeClock, conditions, options, Cleaner.AutoCleanDeadline)
        {
        }

        /// <summary>
        /// As above, with the clean given the whole job: its areas, reason, id and time limit. Threshold and interval
        /// cleans get autoDeadline (Cleaner.AutoCleanDeadline in the app), so an automatic clean can't run on and on;
        /// a manual clean gets none: the user asked for it and the app offers no way to stop it (owner decision).
        /// </summary>
        public AutoCleanService(Func<MemorySnapshot> sample, Func<CleanJob, CleanReport> clean, Func<DateTime> clock,
            Func<TimeSpan> awakeClock, Func<Conditions> conditions, GateOptions options, TimeSpan autoDeadline)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            if (clean == null) throw new ArgumentNullException("clean");
            if (clock == null) throw new ArgumentNullException("clock");
            if (autoDeadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("autoDeadline");
            this.sample = sample;
            this.clean = clean;
            this.autoDeadline = autoDeadline;
            this.clock = clock;
            this.awakeClock = awakeClock;
            this.conditions = conditions;
            this.options = options ?? new GateOptions();
            intervalFrom = clock();
        }

        /// <summary>The old clean function, which takes the areas alone; null stays null so the constructor refuses it.</summary>
        static Func<CleanJob, CleanReport> AreasOnly(Func<Areas, CleanReport> clean)
        {
            if (clean == null) return null;
            return job => clean(job.Areas);
        }

        /// <summary>This session's cleans, newest first (copies, at most CleanHistory.Capacity).</summary>
        public List<CleanHistoryEntry> History { get { lock (gate) return history.Snapshot(); } }

        /// <summary>Why automatic cleans wait, as of the last tick (the value HoldChanged last carried).</summary>
        public HoldReason Hold { get { lock (gate) return shownReason; } }

        /// <summary>
        /// The PC is about to sleep (PowerModes.Suspend): hold automatic cleans until it wakes. Thread-safe and
        /// never throws, because it runs on the SystemEvents thread, where an exception ends the process.
        /// </summary>
        public void NotifySuspend()
        {
            try
            {
                TimeSpan awake = AwakeOrWall(clock());
                lock (gate) hold = AutoCleanGate.OnSuspend(hold, awake).Gate;
            }
            catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Other); }
        }

        /// <summary>
        /// The PC woke (PowerModes.Resume): automatic cleans wait out the grace, and the schedule restarts from
        /// now. Thread-safe and never throws.
        /// </summary>
        public void NotifyResume()
        {
            try
            {
                DateTime now = clock();
                lock (gate)
                {
                    GateStep step = AutoCleanGate.OnResume(hold, now, options);
                    hold = step.Gate;
                    if (step.Reanchor) intervalFrom = now;
                }
            }
            catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Other); }
        }

        /// <summary>The awake clock, or false when there is none or it has failed (then never called again).</summary>
        bool TryAwake(out TimeSpan awake)
        {
            awake = TimeSpan.Zero;
            if (awakeClock == null || awakeBroken) return false;
            try
            {
                awake = awakeClock();
                return true;
            }
            catch (Exception)
            {
                awakeBroken = true; // no Error: it would repeat every tick, and the gate works without it
                return false;
            }
        }

        /// <summary>
        /// The awake clock, or the wall clock standing in for it: then the suspend timeout counts wall time and
        /// no sleep gap can show.
        /// </summary>
        TimeSpan AwakeOrWall(DateTime now)
        {
            TimeSpan awake;
            return TryAwake(out awake) ? awake : TimeSpan.FromTicks(now.Ticks);
        }

        Conditions ReadConditions()
        {
            if (conditions == null) return Conditions.Unknown;
            try
            {
                Conditions c = conditions();
                return c ?? Conditions.Unknown;
            }
            catch (Exception) { return Conditions.Unknown; } // Unknown holds nothing; no Error every tick
        }

        public bool IsCleaning { get { return Volatile.Read(ref cleaning) == 1; } }

        /// <summary>
        /// A clean runs whose areas include services (Power Clean), so it may still stop some. In the moment
        /// before the clean has recorded its snapshot's areas, the current settings' areas stand in for them.
        /// </summary>
        public bool CleanMayStopServices
        {
            get
            {
                if (!IsCleaning) return false;
                Areas areas = (Areas)Volatile.Read(ref cleanAreas) | settings.CleanAreas;
                return (areas & Areas.Services) != 0;
            }
        }

        /// <summary>
        /// Waits up to timeout for a clean in flight to finish. True when none is running. Works after Dispose,
        /// which leaves a running clean to finish: Exit uses it so the restore never starts before a Power Clean's stops.
        /// </summary>
        public bool WaitUntilIdle(TimeSpan timeout)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            lock (idle)
            {
                while (IsCleaning)
                {
                    TimeSpan left = timeout - watch.Elapsed;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(idle, left);
                }
                return true;
            }
        }

        /// <summary>Clears the running flag and wakes WaitUntilIdle.</summary>
        void EndClean()
        {
            lock (idle)
            {
                Volatile.Write(ref cleanAreas, 0);
                Interlocked.Exchange(ref cleaning, 0);
                Monitor.PulseAll(idle);
            }
        }

        public DateTime? LastClean { get { lock (gate) return lastClean; } }

        /// <summary>
        /// Takes a copy, so later changes to the caller's object have no effect until the next Update. A clean
        /// already decided keeps the settings it started with. A new policy (PolicyChanged) clears the backoff, so
        /// it starts from the normal cooldown; other changes, such as the theme, keep it. Turning auto-clean off
        /// ends a pause, so turning it on again never finds an old one. The pause itself is not a setting here:
        /// Pause, Resume and RestorePause set it.
        /// </summary>
        public void Update(AppSettings newSettings)
        {
            if (newSettings == null) throw new ArgumentNullException("newSettings");
            AppSettings next = newSettings.Normalized();
            lock (gate)
            {
                if (PolicyChanged(settings, next)) futileStreak = 0;
                if (!next.AutoCleanEnabled) pauseUntil = null;
                settings = next;
            }
        }

        /// <summary>
        /// Holds automatic cleans for length (at most PauseLength), replacing any pause. Manual cleans still run.
        /// Returns false, changing nothing, while auto-clean is off: there is nothing to pause.
        /// </summary>
        public bool Pause(TimeSpan length)
        {
            if (length <= TimeSpan.Zero || length > PauseLength) throw new ArgumentOutOfRangeException("length");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AutoCleanService");
                if (!settings.AutoCleanEnabled) return false;
                pauseUntil = clock() + length;
                return true;
            }
        }

        /// <summary>
        /// Takes up a pause saved by an earlier run, cut to PauseLength from now. False, changing nothing, when there
        /// is none, it has ended, or auto-clean is off.
        /// </summary>
        public bool RestorePause(DateTime? untilUtc)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AutoCleanService");
                if (!settings.AutoCleanEnabled) return false;
                DateTime? until = ClampPause(untilUtc, clock());
                if (until == null) return false;
                pauseUntil = until;
                return true;
            }
        }

        /// <summary>Ends a pause now. The schedule restarts from now, so no overdue interval clean fires at once.</summary>
        public void Resume()
        {
            lock (gate)
            {
                if (pauseUntil == null) return;
                pauseUntil = null;
                intervalFrom = clock();
            }
        }

        /// <summary>A pause's end as it may stand at now: null when there is none or it has ended, else at most PauseLength on. Pure.</summary>
        public static DateTime? ClampPause(DateTime? untilUtc, DateTime now)
        {
            if (!untilUtc.HasValue || untilUtc.Value <= now) return null;
            DateTime latest = now + PauseLength;
            return untilUtc.Value > latest ? latest : untilUtc.Value;
        }

        /// <summary>
        /// A pause's end after the clock jumped back from lastTickUtc to nowUtc: the time that was left at the last
        /// tick, never a fresh hour (at most PauseLength); null when none was left. Pure.
        /// </summary>
        public static DateTime? PauseAfterClockBack(DateTime untilUtc, DateTime lastTickUtc, DateTime nowUtc)
        {
            TimeSpan left = untilUtc - lastTickUtc;
            if (left <= TimeSpan.Zero) return null;
            return nowUtc + (left > PauseLength ? PauseLength : left);
        }

        /// <summary>Everything the window and tray show about auto-clean, read together under the lock.</summary>
        public AutoCleanStatus GetStatus()
        {
            lock (gate)
            {
                DateTime now = clock();
                DateTime? until = ClampPause(pauseUntil, now);
                return new AutoCleanStatus
                {
                    Paused = until.HasValue,
                    PausedUntilUtc = until,
                    FutileStreak = futileStreak,
                    LastCleanUtc = lastClean,
                    NextThresholdCleanUtc = lastClean.HasValue ? lastClean.Value + ThresholdCooldown(settings, futileStreak) : (DateTime?)null,
                    Hold = shownReason
                };
            }
        }

        /// <summary>True when the settings that decide a threshold clean differ: threshold, cooldown or the master switch. Pure.</summary>
        internal static bool PolicyChanged(AppSettings a, AppSettings b)
        {
            if (a == null || b == null) return !ReferenceEquals(a, b);
            return a.ThresholdPercent != b.ThresholdPercent || a.CooldownSeconds != b.CooldownSeconds
                || a.AutoCleanEnabled != b.AutoCleanEnabled;
        }

        /// <summary>Starts (or restarts) sampling; the first sample is taken at once.</summary>
        public void Start(TimeSpan sampleInterval)
        {
            if (sampleInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("sampleInterval");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AutoCleanService");
                if (timer != null) timer.Dispose();
                if (lastClean == null) intervalFrom = clock();
                haveLastTick = false; // the time stopped is not a sleep
                int generation = ++timerGeneration;
                timer = new Timer(OnTimer, generation, TimeSpan.Zero, sampleInterval);
            }
        }

        /// <summary>Stops sampling. A tick or clean already running finishes; its events may still arrive.</summary>
        public void Stop()
        {
            lock (gate) StopLocked();
        }

        void StopLocked()
        {
            timerGeneration++;
            if (timer != null) { timer.Dispose(); timer = null; }
        }

        /// <summary>Stops and marks disposed in one step, so a Start racing Dispose cannot leave a live timer.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                StopLocked();
            }
        }

        /// <summary>
        /// Starts a manual clean on a worker thread, with the settings in force now. Returns false (and does
        /// nothing) when a clean is already running.
        /// </summary>
        public bool CleanNow()
        {
            lock (gate) if (disposed) throw new ObjectDisposedException("AutoCleanService");
            AppSettings s = settings;
            if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) return false;
            Volatile.Write(ref cleanAreas, (int)s.CleanAreas); // CleanMayStopServices sees the click's areas at once
            try
            {
                ThreadPool.QueueUserWorkItem(delegate { RunCleanHoldingGuard(CleanReason.Manual, s); });
            }
            catch
            {
                EndClean();
                throw;
            }
            return true;
        }

        /// <summary>
        /// The whole auto-clean decision, free of timers and threads. intervalAnchor is the time of the last
        /// clean, or when monitoring started if there has been none.
        /// </summary>
        public static CleanReason? Decide(AppSettings s, uint loadPercent, DateTime? lastClean, DateTime intervalAnchor,
            DateTime now, bool cleanRunning)
        {
            return Decide(s, loadPercent, lastClean, intervalAnchor, now, cleanRunning, 0);
        }

        /// <summary>
        /// As above, with the threshold cooldown doubled once per futile clean in a row: when cleaning does not get
        /// load under the threshold, the memory is really in use, and cleaning again soon only makes programs read
        /// their pages back from disk.
        /// </summary>
        public static CleanReason? Decide(AppSettings s, uint loadPercent, DateTime? lastClean, DateTime intervalAnchor,
            DateTime now, bool cleanRunning, int futileStreak)
        {
            return Decide(s, loadPercent, lastClean, intervalAnchor, now, cleanRunning, futileStreak, GateHold.None);
        }

        /// <summary>
        /// As above, honouring a hold (AutoCleanGate): a hold on the threshold skips the high-load clean, a hold
        /// on the interval skips the scheduled one. A null hold holds nothing.
        /// </summary>
        public static CleanReason? Decide(AppSettings s, uint loadPercent, DateTime? lastClean, DateTime intervalAnchor,
            DateTime now, bool cleanRunning, int futileStreak, GateHold hold)
        {
            return Decide(s, loadPercent, lastClean, intervalAnchor, now, cleanRunning, futileStreak, hold, false);
        }

        /// <summary>As above; while the user's pause runs, no automatic clean of either kind.</summary>
        public static CleanReason? Decide(AppSettings s, uint loadPercent, DateTime? lastClean, DateTime intervalAnchor,
            DateTime now, bool cleanRunning, int futileStreak, GateHold hold, bool paused)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (hold == null) hold = GateHold.None;
            // One master switch: AutoCleanEnabled gates both the threshold clean and the interval clean.
            if (!s.AutoCleanEnabled || cleanRunning) return null;
            if (paused) return null;
            // A clock set backwards would otherwise block every clean until it caught up again.
            if (lastClean.HasValue && lastClean.Value > now) lastClean = null;
            if (intervalAnchor > now) intervalAnchor = now;

            if (!hold.Threshold
                && AutoCleanPolicy.ShouldClean(loadPercent, s.ThresholdPercent, lastClean, now, ThresholdCooldown(s, futileStreak)))
                return CleanReason.Threshold;
            if (!hold.Interval && s.IntervalMinutes > 0 && now - intervalAnchor >= TimeSpan.FromMinutes(s.IntervalMinutes))
                return CleanReason.Interval;
            return null;
        }

        /// <summary>The settings' cooldown, doubled per futile clean in a row (at most 2^MaxBackoffSteps times). Pure.</summary>
        public static TimeSpan ThresholdCooldown(AppSettings s, int futileStreak)
        {
            return AutoCleanPolicy.ThresholdCooldown(TimeSpan.FromSeconds(s.CooldownSeconds), futileStreak);
        }

        /// <summary>The streak after a clean: one more when load stayed at or above the threshold, else back to 0. Pure.</summary>
        public static int NextStreak(int streak, uint loadAfter, int thresholdPercent)
        {
            return AutoCleanPolicy.NextStreak(streak, loadAfter, thresholdPercent);
        }

        void OnTimer(object state)
        {
            // Unhandled exceptions on a timer thread end the process: nothing may escape this method.
            try
            {
                if ((int)state != Volatile.Read(ref timerGeneration)) return;
                if (Interlocked.CompareExchange(ref ticking, 1, 0) != 0) return; // previous tick still busy
                try { RunTick(); }
                finally { Interlocked.Exchange(ref ticking, 0); }
            }
            catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Other); }
        }

        /// <summary>One sample-and-decide pass, run synchronously (the timer calls this; so do tests).</summary>
        internal void RunTick()
        {
            MemorySnapshot snap;
            try { snap = sample(); }
            catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Sample); return; }
            if (snap == null) { RaiseError(new InvalidOperationException("The sample function returned no snapshot."), ServiceErrorSource.Sample); return; }

            AppSettings s = settings;
            DateTime now = clock();
            lock (gate)
            {
                // Before Sampled, so a handler reading GetStatus sees this sample's streak and pause.
                // Pressure eased by itself: the next time load climbs, the normal cooldown applies again.
                if (snap.LoadPercent < s.ThresholdPercent) futileStreak = 0;
                if (pauseUntil.HasValue)
                {
                    // Clock set back since the last tick: keep the time that was left, so the pause never restarts.
                    if (haveLastTick && now < lastTickWall) pauseUntil = PauseAfterClockBack(pauseUntil.Value, lastTickWall, now);
                    else if (pauseUntil.Value > now + PauseLength) pauseUntil = now + PauseLength; // set back before any tick
                    if (pauseUntil == null || now >= pauseUntil.Value)
                    {
                        // The pause ended: the schedule restarts now, so no overdue interval clean fires at once.
                        pauseUntil = null;
                        intervalFrom = now;
                    }
                }
            }
            Raise(Sampled, snap);
            RaiseRebounds(now, snap);

            TimeSpan awakeNow;
            bool haveAwake = TryAwake(out awakeNow);
            if (!haveAwake) awakeNow = TimeSpan.FromTicks(now.Ticks);
            Conditions c = ReadConditions();
            DateTime? last;
            DateTime anchor;
            int streak;
            bool paused;
            GateHold h;
            bool holdChanged = false;
            lock (gate)
            {
                TimeSpan wallSince = haveLastTick ? now - lastTickWall : TimeSpan.Zero;
                // Without a working awake clock both deltas are the wall's, so no sleep gap can show.
                TimeSpan awakeSince = haveLastTick && haveAwake ? awakeNow - lastTickAwake : wallSince;
                GateStep step = AutoCleanGate.OnTick(hold, now, wallSince, awakeSince, awakeNow, c.Presence, c.Power, options, !haveAwake);
                hold = step.Gate;
                h = step.Hold;
                // A wake, or a hold that blocked the schedule ending: the schedule restarts now, so no overdue
                // clean fires the moment the user comes back.
                if (step.Reanchor) intervalFrom = now;
                lastTickWall = now;
                lastTickAwake = awakeNow;
                haveLastTick = true;
                if (h.Reason != shownReason) { shownReason = h.Reason; holdChanged = true; }

                last = lastClean;
                // A clock set back would otherwise hold interval cleans until it caught up with the anchor.
                if (intervalFrom > now) intervalFrom = now;
                anchor = intervalFrom;
                streak = futileStreak;
                // A Pause or Resume from the UI thread since the check above counts too.
                paused = pauseUntil.HasValue && now < pauseUntil.Value;
            }

            CleanReason? reason = Decide(s, snap.LoadPercent, last, anchor, now, IsCleaning, streak, h, paused);
            if (holdChanged) Raise(HoldChanged, h.Reason); // before a clean, which may take a while
            if (reason == null) return;
            if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) return; // a manual clean got in first
            RunCleanHoldingGuard(reason.Value, s);
        }

        /// <summary>
        /// Runs one clean against s, the settings it was decided under: the areas and the futility threshold come
        /// from s, never from the settings field, which a save may change while the clean runs.
        /// </summary>
        void RunCleanHoldingGuard(CleanReason reason, AppSettings s)
        {
            try
            {
                Areas areas = s.CleanAreas;
                Volatile.Write(ref cleanAreas, (int)areas);
                // Recorded before cleaning so a clean that keeps failing (e.g. not elevated) still waits the
                // cooldown instead of retrying on every tick.
                lock (gate) { lastClean = clock(); intervalFrom = lastClean.Value; }
                Raise(CleanStarted, reason);
                var job = new CleanJob
                {
                    Areas = areas,
                    Reason = reason,
                    Deadline = reason == CleanReason.Manual ? (TimeSpan?)null : autoDeadline,
                    Id = CleanLog.NewId()
                };
                CleanReport report = null;
                string error = null;
                try { report = clean(job); }
                catch (Exception ex)
                {
                    error = ex.Message;
                    RaiseError(ex, ServiceErrorSource.Clean);
                }
                lock (gate)
                {
                    // A busy skip ran nothing (before equals after): counting it as futile would wrongly double the wait.
                    if (report != null && report.After != null && report.Stopped != CleanStop.Busy)
                        futileStreak = StreakAfter(reason, futileStreak, report.After.LoadPercent, s.ThresholdPercent);
                    history.Add(new CleanHistoryEntry { Id = job.Id, EndUtc = clock(), Reason = reason, Report = report, Error = error });
                }
                var completed = CleanCompleted;
                if (completed != null)
                {
                    try { completed(report, reason); }
                    catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Other); }
                }
            }
            catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Clean); }
            finally { EndClean(); }
        }

        /// <summary>
        /// The streak after a clean. An automatic clean counts (NextStreak); a manual one can only reset it, when it
        /// brought load under the threshold: the user chose to clean, which says nothing about whether automatic
        /// cleans help, so it never lengthens their wait. Pure.
        /// </summary>
        public static int StreakAfter(CleanReason reason, int streak, uint loadAfter, int thresholdPercent)
        {
            if (reason != CleanReason.Manual) return NextStreak(streak, loadAfter, thresholdPercent);
            return loadAfter < thresholdPercent ? 0 : streak;
        }

        /// <summary>A manual clean run on the calling thread, as CleanNow runs it on a worker. False when one is running.</summary>
        internal bool RunManualCleanForTests()
        {
            AppSettings s = settings;
            if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) return false;
            RunCleanHoldingGuard(CleanReason.Manual, s);
            return true;
        }

        /// <summary>Records this sample as the rebound of a clean about five minutes old, and raises Rebound for it.</summary>
        void RaiseRebounds(DateTime now, MemorySnapshot snap)
        {
            List<CleanHistoryEntry> due;
            lock (gate) due = history.DueRebound(now, snap.LoadPercent);
            var handler = Rebound;
            if (handler == null) return;
            foreach (CleanHistoryEntry e in due)
            {
                try { handler(e, snap); }
                catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Other); }
            }
        }

        void Raise<T>(Action<T> handler, T arg)
        {
            if (handler == null) return;
            try { handler(arg); }
            catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Other); }
        }

        void RaiseError(Exception ex, ServiceErrorSource source)
        {
            var handler = Error;
            if (handler != null)
            {
                try { handler(ex); }
                catch { } // an Error handler that throws has nowhere left to report to
            }
            var tagged = Failed;
            if (tagged == null) return;
            try { tagged(ex, source); }
            catch { }
        }
    }

    /// <summary>A snapshot of what the window and tray show about auto-clean (AutoCleanService.GetStatus).</summary>
    public sealed class AutoCleanStatus
    {
        /// <summary>The user paused automatic cleans, until PausedUntilUtc. Manual cleans still run.</summary>
        public bool Paused;
        public DateTime? PausedUntilUtc;
        /// <summary>Automatic cleans in a row that left load at or above the threshold; above 0 the threshold wait is longer.</summary>
        public int FutileStreak;
        public DateTime? LastCleanUtc;
        /// <summary>The earliest a clean for high load can run again (last clean plus the current cooldown); null before any clean.</summary>
        public DateTime? NextThresholdCleanUtc;
        /// <summary>Why automatic cleans wait for the system (sleep, full screen, lock, battery), as of the last tick.</summary>
        public HoldReason Hold;
    }
}
