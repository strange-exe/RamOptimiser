using System;
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
    /// taskbar colour or the size changes, and every replaced icon is disposed. Never shows balloons.
    /// Lives on the UI thread: WPF's dispatcher pumps the messages NotifyIcon needs.
    /// </summary>
    internal sealed class TrayController : IDisposable
    {
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem openItem, cleanItem, autoItem, restartItem, exitItem;
        readonly Font boldFont;
        Icon appIcon;          // static icon at the current small-icon size
        Icon percentIcon;      // last rendered percent icon (owned)
        int shownPercent = -1, shownSize = -1;
        bool shownLight, showingPercent;
        bool disposed;

        public event EventHandler OpenRequested, CleanRequested, AutoCleanToggled, RestartRequested, ExitRequested;

        public TrayController()
        {
            menu = new ContextMenuStrip();
            openItem = new ToolStripMenuItem("Open");
            boldFont = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Font = boldFont;
            cleanItem = new ToolStripMenuItem("Clean memory");
            autoItem = new ToolStripMenuItem("Auto-clean") { CheckOnClick = false };
            restartItem = new ToolStripMenuItem(TrayMenuState.RestartText) { Visible = false };
            exitItem = new ToolStripMenuItem("Exit");
            openItem.Click += delegate { Raise(OpenRequested); };
            cleanItem.Click += delegate { Raise(CleanRequested); };
            autoItem.Click += delegate { Raise(AutoCleanToggled); };
            restartItem.Click += delegate { Raise(RestartRequested); };
            exitItem.Click += delegate { Raise(ExitRequested); };
            menu.Items.AddRange(new ToolStripItem[] { openItem, cleanItem, autoItem, restartItem, new ToolStripSeparator(), exitItem });

            shownSize = SmallIconSize();
            appIcon = LoadAppIcon(shownSize);
            icon = new NotifyIcon { Icon = appIcon, Text = AppText.AppName, ContextMenuStrip = menu };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Raise(OpenRequested); };
            icon.Visible = true;
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
            if (disposed) return;
            cleanItem.Enabled = state.CleanEnabled;
            cleanItem.Text = state.CleanText;
            autoItem.Checked = state.AutoChecked;
            autoItem.Text = state.AutoText;
            restartItem.Visible = state.ShowRestart;
        }

        /// <summary>snapshot null = status unavailable (static icon, plain tooltip).</summary>
        public void Update(MemorySnapshot snapshot, bool showPercent, bool taskbarIsLight)
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

            string tip = AppText.TrayTooltip(snapshot);
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
