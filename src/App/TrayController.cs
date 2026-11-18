using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    /// <summary>
    /// The notification-area icon. Shows the load as digits (TrayIconRenderer, transparent background, digits
    /// in the taskbar's text colour) or the static app icon. The icon is redrawn only when the percent, the
    /// taskbar colour or the size changes, and every replaced icon is disposed. Shows a balloon only when the user
    /// turned on problem notices in Settings (owner decision 2026-11-17: opt-in, off by default; AppShell decides when,
    /// through Notices and NoticeGate); clicking it opens the window on the Memory tab.
    /// Lives on the UI thread: WPF's dispatcher pumps the messages NotifyIcon needs.
    /// Menu: Open, Clean memory, the last clean's result (hidden before the first clean; enabled, so keyboard and
    /// Narrator users reach it, and it opens the Memory tab), Auto-clean (ticked when on, "(paused until 15:41)" while
    /// paused), then either Pause auto-clean for 1 hour or Resume auto-clean (both hidden, never greyed, when
    /// auto-clean is off or needs admin), Restart as administrator (not elevated), Settings not saved (a failed save),
    /// and Exit. The tooltip has a second line for what auto-clean or the last clean needs the user to know (P7).
    /// </summary>
    internal sealed class TrayController : IDisposable
    {
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem openItem, cleanItem, statusItem, autoItem, pauseItem, resumeItem, restartItem, unsavedItem, exitItem;
        readonly Font boldFont;
        Icon appIcon;          // static icon at the current small-icon size
        Icon percentIcon;      // last rendered percent icon (owned)
        int shownPercent = -1, shownSize = -1;
        bool shownLight, showingPercent;
        bool disposed;

        public event EventHandler OpenRequested, CleanRequested, StatusClicked, AutoCleanToggled, PauseRequested, ResumeRequested, RestartRequested, ExitRequested;
        /// <summary>The user clicked a problem notice.</summary>
        public event EventHandler NoticeClicked;
        /// <summary>A left click on the icon: AppShell opens or closes the flyout (7A). The right click keeps the menu.</summary>
        public event EventHandler FlyoutRequested;

        public TrayController()
        {
            menu = new ContextMenuStrip();
            openItem = new ToolStripMenuItem("Open");
            boldFont = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Font = boldFont;
            cleanItem = new ToolStripMenuItem("Clean memory");
            statusItem = new ToolStripMenuItem("") { Visible = false };
            autoItem = new ToolStripMenuItem("Auto-clean") { CheckOnClick = false };
            pauseItem = new ToolStripMenuItem(AppText.PauseMenuText) { Visible = false };
            resumeItem = new ToolStripMenuItem(AppText.ResumeMenuText) { Visible = false };
            restartItem = new ToolStripMenuItem(TrayMenuState.RestartText) { Visible = false };
            // Shown only while settings.ini could not be written; the Settings tab gives the reason.
            unsavedItem = new ToolStripMenuItem(AppText.SettingsNotSaved) { Enabled = false, Visible = false };
            exitItem = new ToolStripMenuItem("Exit");
            openItem.Click += delegate { Raise(OpenRequested); };
            cleanItem.Click += delegate { Raise(CleanRequested); };
            statusItem.Click += delegate { Raise(StatusClicked); };
            autoItem.Click += delegate { Raise(AutoCleanToggled); };
            pauseItem.Click += delegate { Raise(PauseRequested); };
            resumeItem.Click += delegate { Raise(ResumeRequested); };
            restartItem.Click += delegate { Raise(RestartRequested); };
            exitItem.Click += delegate { Raise(ExitRequested); };
            menu.Items.AddRange(new ToolStripItem[]
            {
                openItem, cleanItem, statusItem, autoItem, pauseItem, resumeItem, restartItem, unsavedItem, new ToolStripSeparator(), exitItem
            });

            shownSize = SmallIconSize();
            appIcon = LoadAppIcon(shownSize);
            icon = new NotifyIcon { Icon = appIcon, Text = AppText.AppName, ContextMenuStrip = menu };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Raise(FlyoutRequested); };
            icon.BalloonTipClicked += delegate { Raise(NoticeClicked); };
            icon.Visible = true;
        }

        /// <summary>
        /// A Windows notification from the tray icon (R6), with the warning icon. The timeout is ignored by current
        /// Windows (display time follows the accessibility settings). Plain text only; never throws.
        /// </summary>
        public bool ShowNotice(string title, string text)
        {
            if (disposed || string.IsNullOrEmpty(text)) return false;
            try
            {
                icon.ShowBalloonTip(10000, title ?? "", text, ToolTipIcon.Warning);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The Clean memory item shows the global shortcut on its right ("Ctrl+Shift+M"), or nothing when null.</summary>
        public void SetCleanShortcut(string keys)
        {
            if (disposed) return;
            cleanItem.ShortcutKeyDisplayString = keys ?? "";
            cleanItem.ShowShortcutKeys = !string.IsNullOrEmpty(keys);
        }

        void Raise(EventHandler h)
        {
            if (h != null && !disposed) h(this, EventArgs.Empty);
        }

        static int SmallIconSize()
        {
            int s = SystemInformation.SmallIconSize.Width;
            return Math.Max(TrayIconRenderer.MinSize, Math.Min(TrayIconRenderer.MaxSize, s));
        }

        public static Icon LoadAppIcon(int size)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RamOptimiser.AppIcon.ico"))
            {
                if (s == null) return (Icon)SystemIcons.Application.Clone();
                return new Icon(s, size, size);
            }
        }

        /// <summary>Words, not only a grey item: "Clean memory (needs admin)", "Auto-clean (paused: needs admin)".</summary>
        public void SetMenuState(TrayMenuState state)
        {
            SetMenuState(state, false);
        }

        /// <param name="settingsUnsaved">True while the last settings save failed: a disabled "Settings not saved" item shows.</param>
        public void SetMenuState(TrayMenuState state, bool settingsUnsaved)
        {
            SetMenuState(state, settingsUnsaved, false, false);
        }

        /// <param name="canPause">Auto-clean is on, elevated and not paused: "Pause auto-clean for 1 hour" shows.</param>
        /// <param name="paused">The user paused it: "Resume auto-clean" shows instead.</param>
        public void SetMenuState(TrayMenuState state, bool settingsUnsaved, bool canPause, bool paused)
        {
            if (disposed) return;
            cleanItem.Enabled = state.CleanEnabled;
            cleanItem.Text = state.CleanText;
            autoItem.Checked = state.AutoChecked;
            autoItem.Text = state.AutoText;
            pauseItem.Visible = canPause && !paused;
            resumeItem.Visible = paused;
            restartItem.Visible = state.ShowRestart;
            unsavedItem.Visible = settingsUnsaved;
        }

        /// <summary>The last clean's result (TrayStatus.MenuLine), or null to hide the item (before the first clean).</summary>
        public void SetStatusLine(string text)
        {
            if (disposed) return;
            statusItem.Visible = !string.IsNullOrEmpty(text);
            // An ampersand would mark a mnemonic and vanish from the text: double it so it shows.
            string shown = text == null ? "" : text.Replace("&", "&&");
            if (statusItem.Text != shown) statusItem.Text = shown;
        }

        /// <summary>snapshot null = status unavailable (static icon, plain tooltip).</summary>
        public void Update(MemorySnapshot snapshot, bool showPercent, bool taskbarIsLight)
        {
            Update(snapshot, showPercent, taskbarIsLight, null);
        }

        /// <param name="tipLines">The tooltip's second line in its forms, longest first (TrayStatus.TooltipLines), or
        /// null or empty for the one-line tooltip.</param>
        public void Update(MemorySnapshot snapshot, bool showPercent, bool taskbarIsLight, IList<string> tipLines)
        {
            if (disposed) return;
            int size = SmallIconSize();
            if (size != shownSize)
            {
                Icon oldApp = appIcon;
                appIcon = LoadAppIcon(size);
                if (!showingPercent) icon.Icon = appIcon;
                oldApp.Dispose();
            }

            if (!showPercent || snapshot == null)
            {
                if (showingPercent || size != shownSize) icon.Icon = appIcon;
                showingPercent = false;
                DisposePercentIcon();
            }
            else
            {
                int percent = (int)Math.Min(100, snapshot.LoadPercent);
                if (!showingPercent || percent != shownPercent || taskbarIsLight != shownLight || size != shownSize)
                {
                    Color text = Color.FromArgb(ThemeResolver.TrayTextArgb(taskbarIsLight));
                    Icon next = TrayIconRenderer.Render(percent, Color.Transparent, text, size);
                    icon.Icon = next;
                    DisposePercentIcon();
                    percentIcon = next;
                    shownPercent = percent;
                    shownLight = taskbarIsLight;
                    showingPercent = true;
                }
            }
            shownSize = size;

            string tip = AppText.TrayTooltip(snapshot, tipLines);
            if (icon.Text != tip) icon.Text = tip;
        }

        void DisposePercentIcon()
        {
            if (percentIcon != null) { percentIcon.Dispose(); percentIcon = null; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            icon.Visible = false;   // removes the icon now, so no ghost is left in the notification area
            icon.Dispose();
            menu.Dispose();
            boldFont.Dispose();
            DisposePercentIcon();
            if (appIcon != null) appIcon.Dispose();
        }
    }
}
