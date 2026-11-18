// The global Clean memory shortcut (R7): parsing and checking a gesture, what a key press in the recorder means, the
// 60 s floor on shortcut cleans, and the registration state machine. Pure: no WPF and no RegisterHotKey here
// (HotkeyListener does that behind IHotkeyRegistrar), so the tests compile this file and drive it with a fake.
// Docs: RegisterHotKey, https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
// (MOD_* values, MOD_NOREPEAT, F12 reserved for the debugger, Windows-key shortcuts reserved for the OS);
// ERROR_HOTKEY_ALREADY_REGISTERED is 1409, https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-
using System;
using System.Collections.Generic;
using System.Globalization;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    /// <summary>
    /// A shortcut the app may register: two or three of Ctrl, Alt and Shift with one key (A to Z, 0 to 9, F1 to F11
    /// or F13 to F24). Never the Windows key (reserved for Windows), never F12 (reserved for the debugger), never a
    /// single modifier: settings.ini is user-writable and the elevated app must not take over Ctrl+C or a plain key.
    /// </summary>
    public sealed class HotkeyGesture
    {
        public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
        /// <summary>Longest text TryParse reads ("Ctrl+Alt+Shift+F24" is 18 characters).</summary>
        public const int MaxTextLength = 32;
        const uint VkF1 = 0x70, VkF11 = 0x7A, VkF12 = 0x7B, VkF13 = 0x7C, VkF24 = 0x87;

        public readonly uint Modifiers;
        /// <summary>The virtual-key code.</summary>
        public readonly uint Key;

        HotkeyGesture(uint modifiers, uint key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        /// <summary>What RegisterHotKey gets: the modifiers plus MOD_NOREPEAT, so a held key cleans once.</summary>
        public uint RegisterFlags { get { return Modifiers | ModNoRepeat; } }

        /// <summary>Ctrl+Alt without Shift on a letter or digit: AltGr on many layouts (German Ctrl+Alt+E types the euro sign).</summary>
        public bool IsAltGrRisk
        {
            get { return Modifiers == (ModControl | ModAlt) && (IsLetter(Key) || IsDigit(Key)); }
        }

        /// <summary>
        /// Null, "" or blank: true, with g null (no shortcut). Otherwise tokens split on '+', trimmed and case-insensitive:
        /// Ctrl or Control, Alt, Shift (each at most once, at least two) and exactly one key. Anything else is false.
        /// </summary>
        public static bool TryParse(string text, out HotkeyGesture g)
        {
            g = null;
            if (text == null) return true;
            if (text.Length > MaxTextLength) return false;
            string t = text.Trim();
            if (t.Length == 0) return true;
            uint mods = 0, key = 0;
            bool haveKey = false;
            foreach (string raw in t.Split('+'))
            {
                string token = raw.Trim().ToUpperInvariant();
                if (token.Length == 0) return false;
                uint mod = token == "CTRL" || token == "CONTROL" ? ModControl : token == "ALT" ? ModAlt : token == "SHIFT" ? ModShift : 0;
                if (mod != 0)
                {
                    if ((mods & mod) != 0) return false;
                    mods |= mod;
                    continue;
                }
                uint vk;
                if (haveKey || !TryKey(token, out vk)) return false;
                key = vk;
                haveKey = true;
            }
            if (!haveKey || ModifierCount(mods) < 2) return false;
            g = new HotkeyGesture(mods, key);
            return true;
        }

        /// <summary>The gesture for keys pressed in the recorder, or null when it is not one the app allows.</summary>
        public static HotkeyGesture FromKeys(bool ctrl, bool alt, bool shift, bool win, int vk)
        {
            if (win || vk <= 0 || !AllowedKey((uint)vk)) return null;
            uint mods = (ctrl ? ModControl : 0) | (alt ? ModAlt : 0) | (shift ? ModShift : 0);
            return ModifierCount(mods) < 2 ? null : new HotkeyGesture(mods, (uint)vk);
        }

        /// <summary>Canonical text, modifiers in the order Ctrl, Alt, Shift: "Ctrl+Shift+M", "Ctrl+Alt+F5".</summary>
        public string Format()
        {
            var parts = new List<string>();
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            parts.Add(KeyName(Key));
            return string.Join("+", parts.ToArray());
        }

        public override string ToString() { return Format(); }

        public override bool Equals(object obj)
        {
            var o = obj as HotkeyGesture;
            return o != null && o.Modifiers == Modifiers && o.Key == Key;
        }

        public override int GetHashCode() { return (int)((Modifiers << 16) ^ Key); }

        static int ModifierCount(uint mods)
        {
            return ((mods & ModControl) != 0 ? 1 : 0) + ((mods & ModAlt) != 0 ? 1 : 0) + ((mods & ModShift) != 0 ? 1 : 0);
        }

        static bool IsLetter(uint vk) { return vk >= 'A' && vk <= 'Z'; }
        static bool IsDigit(uint vk) { return vk >= '0' && vk <= '9'; }

        static bool AllowedKey(uint vk)
        {
            return IsLetter(vk) || IsDigit(vk) || (vk >= VkF1 && vk <= VkF11) || (vk >= VkF13 && vk <= VkF24);
        }

        /// <summary>An upper-case token: one ASCII letter or digit, or F1 to F24 without a leading zero (F12 refused).</summary>
        static bool TryKey(string token, out uint vk)
        {
            vk = 0;
            if (token.Length == 1)
            {
                char c = token[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
                return false;
            }
            if (token.Length > 3 || token[0] != 'F' || token[1] < '1' || token[1] > '9') return false;
            int n;
            if (!int.TryParse(token.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > 24) return false;
            vk = VkF1 + (uint)(n - 1);
            return vk != VkF12;
        }

        static string KeyName(uint vk)
        {
            if (IsLetter(vk) || IsDigit(vk)) return ((char)vk).ToString();
            return "F" + (vk - VkF1 + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What a key press in the shortcut box does.</summary>
    public enum HotkeyKeyAction
    {
        /// <summary>Not for the box: Tab moves focus on, Alt+F4 closes the window.</summary>
        PassThrough,
        /// <summary>Swallowed and nothing changes: a modifier on its own, while the user builds the combination.</summary>
        Ignore,
        /// <summary>Delete or Backspace: remove the shortcut.</summary>
        Remove,
        /// <summary>Esc: leave the shortcut as it was.</summary>
        Cancel,
        /// <summary>An allowed combination: try to register it.</summary>
        Record,
        /// <summary>A combination the app does not allow: say what is allowed.</summary>
        Invalid
    }

    /// <summary>The shortcut box's key handling, decided from the modifiers held and the virtual-key code. Pure.</summary>
    public static class HotkeyRecorder
    {
        const int VkBack = 0x08, VkTab = 0x09, VkEscape = 0x1B, VkSpace = 0x20, VkDelete = 0x2E, VkF4 = 0x73;

        /// <summary>Shift, Ctrl, Alt (either side), the Windows keys and the IME/lock keys that are not a shortcut key.</summary>
        static bool IsModifierKey(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        public static HotkeyKeyAction Decide(bool ctrl, bool alt, bool shift, bool win, int vk, out HotkeyGesture gesture)
        {
            gesture = null;
            if (vk <= 0 || IsModifierKey(vk)) return HotkeyKeyAction.Ignore;
            if (vk == VkTab) return HotkeyKeyAction.PassThrough;
            bool plain = !ctrl && !alt && !shift && !win;
            if (alt && !ctrl && !shift && !win && (vk == VkF4 || vk == VkSpace)) return HotkeyKeyAction.PassThrough;
            if (plain && (vk == VkDelete || vk == VkBack)) return HotkeyKeyAction.Remove;
            if (plain && vk == VkEscape) return HotkeyKeyAction.Cancel;
            gesture = HotkeyGesture.FromKeys(ctrl, alt, shift, win, vk);
            return gesture == null ? HotkeyKeyAction.Invalid : HotkeyKeyAction.Record;
        }
    }

    /// <summary>
    /// The floor on shortcut cleans. Any program can send keystrokes, so a press within a minute of the last clean
    /// is ignored: the same 60 s floor settings.ini's cooldown has (AppSettings.MinCooldownSeconds).
    /// </summary>
    public static class HotkeyGate
    {
        public static readonly TimeSpan Floor = TimeSpan.FromSeconds(AppSettings.MinCooldownSeconds);

        /// <summary>No clean yet, at least floor since the last, or the clock went back (last in the future): allowed.</summary>
        public static bool Allow(DateTime? lastCleanUtc, DateTime nowUtc, TimeSpan floor)
        {
            if (lastCleanUtc == null) return true;
            TimeSpan since = nowUtc - lastCleanUtc.Value;
            return since < TimeSpan.Zero || since >= floor;
        }

        /// <summary>The later of two times, either of which may be missing.</summary>
        public static DateTime? Latest(DateTime? a, DateTime? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return a.Value >= b.Value ? a : b;
        }

        /// <summary>
        /// The tray status item's line after a skipped press: shown for a minute, unless a clean ran after the skip.
        /// Null when there is nothing to say.
        /// </summary>
        public static string SkipLine(DateTime? skippedUtc, DateTime? lastCleanUtc, DateTime nowUtc)
        {
            if (skippedUtc == null) return null;
            if (lastCleanUtc.HasValue && lastCleanUtc.Value > skippedUtc.Value) return null;
            TimeSpan since = nowUtc - skippedUtc.Value;
            return since >= TimeSpan.Zero && since < Floor ? AppText.ShortcutSkipped : null;
        }
    }

    /// <summary>RegisterHotKey and UnregisterHotKey for one id; HotkeyListener in the app, a fake in the tests.</summary>
    public interface IHotkeyRegistrar
    {
        /// <summary>True when registered; else error is the Win32 error (1409 when another app has the keys).</summary>
        bool TryRegister(HotkeyGesture g, out int error);
        /// <summary>Idempotent.</summary>
        void Unregister();
    }

    /// <summary>
    /// None: no shortcut. Inactive: a shortcut is saved but the app is not elevated, so nothing is registered.
    /// Registered: Windows sends its presses. Taken: Windows refused the saved shortcut at start.
    /// </summary>
    public enum HotkeyState { None, Inactive, Registered, Taken }

    /// <summary>What a change in the recorder did: whether to save the setting, and the line to show (or null).</summary>
    public sealed class HotkeyResult
    {
        public bool Save;
        public string Message;
        public int Error;
    }

    /// <summary>
    /// The shortcut's registration (R7 state machine). A new shortcut replaces the old one only when Windows accepts
    /// it: on a refusal the previous one is registered again and nothing is saved, so the saved value is always one
    /// that registered when it was chosen. Without a registrar (not elevated) the setting still saves, inactive.
    /// </summary>
    public sealed class HotkeyBinding
    {
        public const int ErrorAlreadyRegistered = 1409;
        readonly IHotkeyRegistrar registrar;

        /// <param name="registrar">Null when the app is not elevated: nothing is ever registered.</param>
        public HotkeyBinding(IHotkeyRegistrar registrar)
        {
            this.registrar = registrar;
            State = HotkeyState.None;
        }

        public HotkeyState State { get; private set; }
        /// <summary>The saved shortcut, registered or not; null for none.</summary>
        public HotkeyGesture Current { get; private set; }
        /// <summary>The Win32 error behind Taken, else 0.</summary>
        public int Error { get; private set; }

        /// <summary>The shortcut Windows is sending, or null.</summary>
        public HotkeyGesture Active { get { return State == HotkeyState.Registered ? Current : null; } }

        /// <summary>The line the Settings row shows for the current state (Taken), or null.</summary>
        public string Message { get { return State == HotkeyState.Taken ? AppText.HotkeyError(Current, Error) : null; } }

        /// <summary>At start, and when settings change from outside the recorder (a reload or a reset).</summary>
        public void Start(HotkeyGesture saved)
        {
            if (registrar != null) registrar.Unregister();
            Current = saved;
            Error = 0;
            if (saved == null) { State = HotkeyState.None; return; }
            if (registrar == null) { State = HotkeyState.Inactive; return; }
            int error;
            if (registrar.TryRegister(saved, out error)) State = HotkeyState.Registered;
            else { State = HotkeyState.Taken; Error = error; }
        }

        /// <summary>A valid gesture from the recorder.</summary>
        public HotkeyResult Record(HotkeyGesture candidate)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            string warning = candidate.IsAltGrRisk ? AppText.HotkeyAltGrWarning : null;
            if (registrar == null)
            {
                Current = candidate;
                State = HotkeyState.Inactive;
                Error = 0;
                return new HotkeyResult { Save = true, Message = warning };
            }
            if (State == HotkeyState.Registered && candidate.Equals(Current)) return new HotkeyResult { Save = true, Message = warning };
            HotkeyGesture previous = State == HotkeyState.Registered ? Current : null;
            registrar.Unregister();
            int error;
            if (registrar.TryRegister(candidate, out error))
            {
                Current = candidate;
                State = HotkeyState.Registered;
                Error = 0;
                return new HotkeyResult { Save = true, Message = warning };
            }
            // Refused: the shortcut that worked before goes back, and the setting stays as it was.
            if (previous != null)
            {
                int again;
                if (!registrar.TryRegister(previous, out again)) { State = HotkeyState.Taken; Error = again; }
            }
            return new HotkeyResult { Save = false, Message = AppText.HotkeyError(candidate, error), Error = error };
        }

        public HotkeyResult Remove()
        {
            if (registrar != null) registrar.Unregister();
            Current = null;
            State = HotkeyState.None;
            Error = 0;
            return new HotkeyResult { Save = true };
        }

        /// <summary>On exit: unregisters, keeping Current. Safe to call twice.</summary>
        public void Stop()
        {
            if (registrar != null) registrar.Unregister();
            if (State == HotkeyState.Registered) State = Current == null ? HotkeyState.None : HotkeyState.Inactive;
        }
    }

    public static partial class AppText
    {
        public const string HotkeyInvalid = "Use two of Ctrl, Alt and Shift with a letter, digit or F key (not F12).";
        public const string HotkeyAltGrWarning = "On some keyboards Ctrl+Alt is AltGr, used to type characters like the euro sign. Ctrl+Shift avoids that.";
        public const string ShortcutSkipped = "Shortcut ignored: the last clean was less than a minute ago.";

        /// <summary>The Settings row's Small line; it says so when the shortcut cannot work because the app is not elevated.</summary>
        public static string HotkeyRowNote(bool elevated)
        {
            const string note = "Works anywhere in Windows while RAM Optimiser runs as administrator. Use two of Ctrl, Alt and Shift with a letter, digit or F key.";
            return elevated ? note : note + " Not active: RAM Optimiser is not running as administrator.";
        }

        /// <summary>1409: another app has it. Anything else: Windows refused, with the code.</summary>
        public static string HotkeyError(HotkeyGesture g, int error)
        {
            if (error == HotkeyBinding.ErrorAlreadyRegistered && g != null)
                return g.Format() + " is already used by another app. Choose another shortcut.";
            return "Windows did not accept this shortcut (error " + error.ToString(CultureInfo.InvariantCulture) + ").";
        }

        /// <summary>
        /// The Clean button's tooltip: the reason it is disabled when there is one (CleanTooltip), else its label with the
        /// active shortcut ("Clean memory (Ctrl+Shift+M)"), else null. Nothing while a clean runs.
        /// </summary>
        public static string CleanButtonTip(string label, bool elevated, bool cleaning, MemoryViewState state, HotkeyGesture active)
        {
            if (cleaning) return null;
            string reason = CleanTooltip(elevated, cleaning, state);
            if (reason != null) return reason;
            return active == null ? null : ShortcutHint(label, active.Format());
        }

        public static ActivityEvent ShortcutSkippedEvent()
        {
            return new ActivityEvent { Kind = "hotkey-skipped", Text = ShortcutSkipped };
        }

        /// <summary>Logged when the shortcut starts a clean; the clean's own record follows with reason "manual".</summary>
        public static ActivityEvent ShortcutCleanEvent(HotkeyGesture g)
        {
            return new ActivityEvent { Kind = "hotkey-clean", Text = "Clean started with the shortcut " + (g == null ? "" : g.Format()) + "." };
        }
    }
}
