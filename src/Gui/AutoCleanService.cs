using System;
using System.Threading;

namespace RamOptimiser.Gui
{
    public enum CleanReason { Manual, Threshold, Interval }

    /// <summary>Where a failure came from: reading memory, the clean itself, or anything else (an event handler).</summary>
    public enum ServiceErrorSource { Sample, Clean, Other }

    /// <summary>
    /// Samples memory on a thread-pool timer and cleans when the settings say so. Never touches the UI thread:
    /// every event is raised on a worker thread, so a GUI must marshal to its dispatcher. At most one clean
    /// runs at a time, whoever asks for it. Exceptions from the sample or clean functions, or from event
    /// handlers, are reported through Error and never stop the timer.
    /// </summary>
    public sealed class AutoCleanService : IDisposable
    {
        readonly Func<MemorySnapshot> sample;
        readonly Func<Areas, CleanReport> clean;
        readonly Func<DateTime> clock;
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
        int futileStreak;        // threshold cleans in a row that left load at or above the threshold; guarded by gate
        bool disposed;

        /// <summary>Each clean in a row that leaves load at or above the threshold doubles the wait, up to this many times.</summary>
        public const int MaxBackoffSteps = AutoCleanPolicy.MaxBackoffSteps;

        /// <summary>Cleans in a row that did not bring load under the threshold (0 to MaxBackoffSteps).</summary>
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

        /// <param name="clock">Use () => DateTime.UtcNow in the app: local time jumps at DST changes.</param>
        public AutoCleanService(Func<MemorySnapshot> sample, Func<Areas, CleanReport> clean, Func<DateTime> clock)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            if (clean == null) throw new ArgumentNullException("clean");
            if (clock == null) throw new ArgumentNullException("clock");
            this.sample = sample;
            this.clean = clean;
            this.clock = clock;
            intervalFrom = clock();
        }

        public bool IsCleaning { get { return Volatile.Read(ref cleaning) == 1; } }

        /// <summary>
        /// A clean runs whose areas include services (Power Clean), so it may still stop some. Before the clean has
        /// read its areas, the current settings' areas stand in for them.
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

        /// <summary>Takes a copy, so later changes to the caller's object have no effect until the next Update.</summary>
        public void Update(AppSettings newSettings)
        {
            if (newSettings == null) throw new ArgumentNullException("newSettings");
            settings = newSettings.Normalized();
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
        /// Starts a manual clean on a worker thread. Returns false (and does nothing) when a clean is already
        /// running.
        /// </summary>
        public bool CleanNow()
        {
            lock (gate) if (disposed) throw new ObjectDisposedException("AutoCleanService");
            if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) return false;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate { RunCleanHoldingGuard(CleanReason.Manual); });
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
            if (s == null) throw new ArgumentNullException("s");
            // One master switch: AutoCleanEnabled gates both the threshold clean and the interval clean.
            if (!s.AutoCleanEnabled || cleanRunning) return null;
            // A clock set backwards would otherwise block every clean until it caught up again.
            if (lastClean.HasValue && lastClean.Value > now) lastClean = null;
            if (intervalAnchor > now) intervalAnchor = now;

            if (AutoCleanPolicy.ShouldClean(loadPercent, s.ThresholdPercent, lastClean, now, ThresholdCooldown(s, futileStreak)))
                return CleanReason.Threshold;
            if (s.IntervalMinutes > 0 && now - intervalAnchor >= TimeSpan.FromMinutes(s.IntervalMinutes))
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
            Raise(Sampled, snap);

            AppSettings s = settings;
            DateTime now = clock();
            DateTime? last;
            DateTime anchor;
            int streak;
            lock (gate)
            {
                last = lastClean;
                // A clock set back would otherwise hold interval cleans until it caught up with the anchor.
                if (intervalFrom > now) intervalFrom = now;
                anchor = intervalFrom;
                // Pressure eased by itself: the next time load climbs, the normal cooldown applies again.
                if (snap.LoadPercent < s.ThresholdPercent) futileStreak = 0;
                streak = futileStreak;
            }

            CleanReason? reason = Decide(s, snap.LoadPercent, last, anchor, now, IsCleaning, streak);
            if (reason == null) return;
            if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) return; // a manual clean got in first
            RunCleanHoldingGuard(reason.Value);
        }

        void RunCleanHoldingGuard(CleanReason reason)
        {
            try
            {
                Areas areas = settings.CleanAreas;
                Volatile.Write(ref cleanAreas, (int)areas);
                // Recorded before cleaning so a clean that keeps failing (e.g. not elevated) still waits the
                // cooldown instead of retrying on every tick.
                lock (gate) { lastClean = clock(); intervalFrom = lastClean.Value; }
                Raise(CleanStarted, reason);
                CleanReport report = null;
                try { report = clean(areas); }
                catch (Exception ex) { RaiseError(ex, ServiceErrorSource.Clean); }
                if (report != null && report.After != null)
                {
                    int threshold = settings.ThresholdPercent;
                    lock (gate) futileStreak = NextStreak(futileStreak, report.After.LoadPercent, threshold);
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
}
