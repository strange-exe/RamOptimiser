using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RamOptimiser.Gui
{
    /// <summary>The user's presence and the power source at one moment.</summary>
    public sealed class Conditions
    {
        public readonly Presence Presence;
        public readonly PowerSource Power;

        public static readonly Conditions Unknown = new Conditions(Presence.Unknown, PowerSource.Unknown);

        public Conditions(Presence presence, PowerSource power)
        {
            Presence = presence;
            Power = power;
        }
    }

    /// <summary>
    /// Reads what the auto-clean gate needs from Windows: awake time, presence (full screen, presentation,
    /// locked) and the power line. Read-only calls into System32 DLLs; nothing here changes system state.
    /// </summary>
    public static class SystemConditions
    {
        /// <summary>
        /// Time the system has spent awake since it started, excluding sleep and hibernation
        /// (QueryUnbiasedInterruptTime, in 100 ns units like TimeSpan ticks). Throws when Windows refuses.
        /// </summary>
        public static TimeSpan AwakeTime()
        {
            ulong ticks;
            if (!GuiNative.QueryUnbiasedInterruptTime(out ticks)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ticks > (ulong)long.MaxValue) throw new OverflowException("Awake time out of range.");
            return TimeSpan.FromTicks((long)ticks);
        }

        /// <summary>
        /// Presence and power now. Never throws: what cannot be read is Unknown, which holds nothing. The shell
        /// sends no message when a full-screen app starts or stops, so the caller polls this on every tick; the
        /// same poll covers the lock screen, so no session-switch subscription is needed.
        /// </summary>
        public static Conditions Read()
        {
            Presence presence = Presence.Unknown;
            PowerSource power = PowerSource.Unknown;
            try
            {
                int state;
                int hr = GuiNative.SHQueryUserNotificationState(out state);
                presence = MapPresence(hr, state);
            }
            catch (Exception) { }
            try
            {
                GuiNative.SYSTEM_POWER_STATUS status;
                if (GuiNative.GetSystemPowerStatus(out status)) power = MapPower(status.ACLineStatus, status.BatteryFlag);
            }
            catch (Exception) { }
            return new Conditions(presence, power);
        }

        /// <summary>
        /// A QUERY_USER_NOTIFICATION_STATE value as a presence. 1 (screen saver, locked, or a switched-away
        /// session) is Away; 2 (full screen or Presentation Settings), 3 (full-screen Direct3D) and 4
        /// (presentation mode) are Busy; 5, 6 and 7 are Present; a failed call or any other value is Unknown. Pure.
        /// </summary>
        internal static Presence MapPresence(int hr, int quns)
        {
            if (hr != 0) return Presence.Unknown;
            switch (quns)
            {
                case 1: return Presence.Away;
                case 2:
                case 3:
                case 4: return Presence.Busy;
                case 5:
                case 6:
                case 7: return Presence.Present;
                default: return Presence.Unknown;
            }
        }

        /// <summary>
        /// SYSTEM_POWER_STATUS as a power source. BatteryFlag 128 (no system battery) is mains whatever the
        /// line says; otherwise ACLineStatus 0 is battery, 1 is mains and anything else (255) is Unknown. Pure.
        /// </summary>
        internal static PowerSource MapPower(byte acLineStatus, byte batteryFlag)
        {
            if (batteryFlag != 255 && (batteryFlag & 128) != 0) return PowerSource.Mains;
            if (acLineStatus == 0) return PowerSource.Battery;
            if (acLineStatus == 1) return PowerSource.Mains;
            return PowerSource.Unknown;
        }
    }
}
