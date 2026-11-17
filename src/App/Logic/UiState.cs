// The window's state machines and small decisions, free of WPF so the tests compile them: the Startup tab's
// load and apply phases, the Memory tab's view state, the tray menu's words, the threshold's pending save and
// where a service failure belongs.
using System;
using System.Collections.Generic;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    /// <summary>NotLoaded until the tab is first opened; Applying covers a service change and the reload after it.</summary>
    public enum StartupPhase { NotLoaded, Loading, Ready, Applying }

    /// <summary>
    /// The Startup tab's one state: loading the lists, ready, or applying a change (and reading the lists again
    /// after it). Switches work only when ready, so a second toggle can never show a state that was not applied.
    /// </summary>
    public sealed class StartupFlow
    {
        StartupPhase phase = StartupPhase.NotLoaded;

        public StartupPhase Phase { get { return phase; } }
        /// <summary>The row being changed (its key), and the state asked for; null when nothing applies.</summary>
        public string ApplyingKey { get; private set; }
        public string ApplyingLabel { get; private set; }
        public bool ApplyingOn { get; private set; }

        public bool SwitchesEnabled { get { return phase == StartupPhase.Ready; } }

        public bool TryStartLoad()
        {
            if (phase == StartupPhase.Loading || phase == StartupPhase.Applying) return false;
            phase = StartupPhase.Loading;
            return true;
        }

        public bool TryStartApply(string key, string label, bool on)
        {
            if (phase != StartupPhase.Ready) return false;
            phase = StartupPhase.Applying;
            ApplyingKey = key;
            ApplyingLabel = label;
            ApplyingOn = on;
            return true;
        }

        /// <summary>The lists were read: after the first load, or after the reload that ends a change.</summary>
        public void Loaded()
        {
            phase = StartupPhase.Ready;
            ApplyingKey = null;
            ApplyingLabel = null;
        }

        /// <summary>The row being changed shows the state asked for; every other row shows what was read.</summary>
        public bool ShowsOn(string key, bool readState)
        {
            return phase == StartupPhase.Applying && key == ApplyingKey ? ApplyingOn : readState;
        }

        /// <summary>Replaces the summary while a change applies, so a search or filter rebuild never hides it.</summary>
        public string BusyLine
        {
            get
            {
                if (phase != StartupPhase.Applying) return null;
                return (ApplyingOn ? "Turning on " : "Turning off ") + ApplyingLabel + "... Other switches wait until this finishes.";
            }
        }
    }

    /// <summary>
    /// Row errors already read out. The list is rebuilt on every search keystroke; an error still showing is
    /// announced once, and again only when its text changes or after it was cleared.
    /// </summary>
    public sealed class AnnouncedErrors
    {
        readonly Dictionary<string, string> said = new Dictionary<string, string>();

        public bool ShouldAnnounce(string key, string text)
        {
            string previous;
            if (said.TryGetValue(key, out previous) && previous == text) return false;
            said[key] = text;
            return true;
        }

        public void Forget(string key)
        {
            said.Remove(key);
        }
    }

    /// <summary>Initial: nothing read yet. FailedNoData: reading failed before any figure was shown.</summary>
    public enum MemoryViewState { Initial, Live, LiveWithError, FailedNoData }

    /// <summary>The Memory tab's one state, moved by each sample and each sampling failure.</summary>
    public sealed class MemoryView
    {
        public MemoryViewState State { get; private set; }
        /// <summary>Local time of the last good sample.</summary>
        public DateTime? LastRead { get; private set; }

        public bool HasData { get { return LastRead.HasValue; } }

        public void Sampled(DateTime localNow)
        {
            State = MemoryViewState.Live;
            LastRead = localNow;
        }

        public void SampleFailed()
        {
            State = HasData ? MemoryViewState.LiveWithError : MemoryViewState.FailedNoData;
        }
    }

    /// <summary>The tray menu's words: when auto-clean is paused and why Clean can't run, as text, not a grey item alone.</summary>
    public sealed class TrayMenuState
    {
        public const string RestartText = "Restart as administrator";

        public string CleanText, AutoText;
        public bool CleanEnabled, AutoChecked, ShowRestart;

        public static TrayMenuState For(bool elevated, bool cleaning, bool autoOn)
        {
            return For(elevated, cleaning, autoOn, true);
        }

        /// <param name="memoryReadable">False while memory status has never been read (the window's Clean waits too).</param>
        public static TrayMenuState For(bool elevated, bool cleaning, bool autoOn, bool memoryReadable)
        {
            return new TrayMenuState
            {
                CleanEnabled = elevated && !cleaning && memoryReadable,
                CleanText = cleaning ? "Cleaning..." : !elevated ? "Clean memory (needs admin)"
                    : memoryReadable ? "Clean memory" : "Clean memory (memory status unavailable)",
                // The tick is the setting; the words say it waits for administrator rights.
                AutoChecked = autoOn,
                AutoText = autoOn && !elevated ? "Auto-clean (paused: needs admin)" : "Auto-clean",
                ShowRestart = !elevated
            };
        }
    }

    /// <summary>A value waiting for its debounced save: flushed once on exit or restart, never dropped.</summary>
    public sealed class PendingSave
    {
        bool pending;

        public bool Pending { get { return pending; } }

        public void Changed() { pending = true; }

        public void Cancel() { pending = false; }

        /// <summary>True once per change: the caller saves now.</summary>
        public bool Take()
        {
            bool was = pending;
            pending = false;
            return was;
        }
    }

    public static class ServiceErrorRoute
    {
        /// <summary>
        /// True when a failure belongs to the clean's result line. A clean's own failure always does; a sampling
        /// failure never does (it goes to the Memory tab's banner); anything else counts against a running clean.
        /// </summary>
        public static bool IsCleanFailure(ServiceErrorSource source, bool cleaning)
        {
            if (source == ServiceErrorSource.Clean) return true;
            if (source == ServiceErrorSource.Sample) return false;
            return cleaning;
        }
    }
}
