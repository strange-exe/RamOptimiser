using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    internal static class AppNative
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_BORDER_COLOR = 34;     // Windows 11
        public const int DWMWA_CAPTION_COLOR = 35;    // Windows 11
        public const int DWMWA_TEXT_COLOR = 36;       // Windows 11
        public const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
        public const int ASFW_ANY = -1;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int processId);

        public const int GWL_STYLE = -16;
        public const long WS_MAXIMIZEBOX = 0x00010000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        /// <summary>The window has a fixed width, so maximizing would only make a tall strip: no Maximize button.</summary>
        public static void RemoveMaximizeBox(IntPtr hwnd)
        {
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_MAXIMIZEBOX));
        }
    }

    /// <summary>Loads the token and style dictionaries and swaps the tokens when the theme changes.</summary>
    internal sealed class ThemeManager
    {
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        static readonly string[] TokenKeys =
        {
            "Surface", "Card", "Hairline", "WindowBorder", "ControlBorder", "Text", "MutedText", "DisabledText",
            "Accent", "OnAccent", "TrackBackground", "TrackEdge", "SecondaryButton", "Ok", "Caution", "Danger", "DangerBackground",
            "InfoBackground", "InfoBorder", "FocusRing", "MapInUse", "MapModified", "MapStandby", "MapFree"
        };

        readonly Application app;
        readonly ResourceDictionary light, dark, styles;
        ResourceDictionary tokens;
        /// <summary>The high-contrast tokens and the system colours they were built from, reused while those hold.</summary>
        ResourceDictionary highContrast;
        string highContrastKey;

        public ResolvedTheme Current { get; private set; }

        public ThemeManager(Application app)
        {
            this.app = app;
            light = (ResourceDictionary)LoadXaml("Light.xaml");
            dark = (ResourceDictionary)LoadXaml("Dark.xaml");
            if (!TokensComplete(light) || !TokensComplete(dark)) throw new InvalidOperationException("A theme dictionary is missing a token.");
            tokens = light;
            app.Resources.MergedDictionaries.Add(tokens);
            styles = (ResourceDictionary)LoadXaml("Styles.xaml");
            app.Resources.MergedDictionaries.Add(styles);
        }

        public static object LoadXaml(string name)
        {
            string resource = "RamOptimiser.Xaml." + name;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException("Missing embedded resource " + resource + ".");
                return XamlReader.Load(s);
            }
        }

        public void Apply(ThemeMode mode)
        {
            Current = ThemeResolver.Resolve(mode, ReadPersonalize("AppsUseLightTheme"), SystemParameters.HighContrast);
            ResourceDictionary next = Current == ResolvedTheme.Dark ? dark
                : Current == ResolvedTheme.Light ? light : HighContrastTokens();
            if (ReferenceEquals(next, tokens)) return;
            int index = app.Resources.MergedDictionaries.IndexOf(tokens);
            if (index >= 0) app.Resources.MergedDictionaries[index] = next;
            else app.Resources.MergedDictionaries.Insert(0, next);
            tokens = next;
        }

        /// <summary>
        /// The same dictionary while the system colours are unchanged, so a broadcast that changes nothing does
        /// not swap the tokens and re-resolve every DynamicResource in the window.
        /// </summary>
        ResourceDictionary HighContrastTokens()
        {
            Color[] used =
            {
                SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.WindowFrameColor, SystemColors.GrayTextColor,
                SystemColors.HighlightColor, SystemColors.HighlightTextColor, SystemColors.ControlColor
            };
            string key = string.Join(",", Array.ConvertAll(used, c => c.ToString()));
            if (highContrast == null || key != highContrastKey)
            {
                highContrast = BuildHighContrast();
                highContrastKey = key;
            }
            return highContrast;
        }

        /// <summary>High contrast: every token maps to the user's system colour, never to our palette.</summary>
        static ResourceDictionary BuildHighContrast()
        {
            var d = new ResourceDictionary();
            d["Surface"] = SystemColors.WindowBrush;
            d["Card"] = SystemColors.WindowBrush;
            d["Hairline"] = SystemColors.WindowTextBrush;
            d["WindowBorder"] = SystemColors.WindowFrameBrush;
            d["ControlBorder"] = SystemColors.WindowTextBrush;
            d["Text"] = SystemColors.WindowTextBrush;
            d["MutedText"] = SystemColors.WindowTextBrush;
            d["DisabledText"] = SystemColors.GrayTextBrush;
            d["Accent"] = SystemColors.HighlightBrush;
            d["OnAccent"] = SystemColors.HighlightTextBrush;
            d["TrackBackground"] = SystemColors.WindowBrush;
            // The track fill matches the card here, so tracks and skeletons keep a visible edge (WCAG 1.4.11).
            d["TrackEdge"] = SystemColors.WindowTextBrush;
            d["SecondaryButton"] = SystemColors.ControlBrush;
            d["Ok"] = SystemColors.WindowTextBrush;
            d["Caution"] = SystemColors.WindowTextBrush;
            d["Danger"] = SystemColors.WindowTextBrush;
            d["DangerBackground"] = SystemColors.WindowBrush;
            d["InfoBackground"] = SystemColors.WindowBrush;
            d["InfoBorder"] = SystemColors.WindowTextBrush;
            d["FocusRing"] = SystemColors.WindowTextBrush;
            // Map parts stay apart in system colours.
            d["MapInUse"] = SystemColors.WindowTextBrush;
            d["MapModified"] = SystemColors.HighlightBrush;
            d["MapStandby"] = SystemColors.GrayTextBrush;
            d["MapFree"] = SystemColors.WindowBrush;
            return d;
        }

        public Color TokenColor(string key)
        {
            var brush = tokens[key] as SolidColorBrush;
            return brush == null ? Colors.Transparent : brush.Color;
        }

        public static bool TokensComplete(ResourceDictionary d)
        {
            foreach (string k in TokenKeys) if (!d.Contains(k)) return false;
            return true;
        }

        public bool TaskbarIsLight()
        {
            return ThemeResolver.TaskbarIsLight(ReadPersonalize("SystemUsesLightTheme"));
        }

        static int? ReadPersonalize(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false))
                {
                    if (key == null) return null;
                    object v = key.GetValue(name);
                    if (v is int) return (int)v;
                    return null;
                }
            }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// Dark title bar (attribute 20, or 19 on Windows 10 before 20H1). On Windows 11 the caption, its text
        /// and the window border also take the surface, text and window-border tokens; earlier versions refuse
        /// those attributes, which is harmless.
        /// </summary>
        public void ApplyWindowFrame(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int darkMode = Current == ResolvedTheme.Dark ? 1 : 0;
            if (AppNative.DwmSetWindowAttribute(hwnd, AppNative.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, 4) != 0)
                AppNative.DwmSetWindowAttribute(hwnd, AppNative.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, 4);

            bool custom = Current != ResolvedTheme.HighContrast;
            int caption = custom ? ColorRef(TokenColor("Surface")) : AppNative.DWMWA_COLOR_DEFAULT;
            int text = custom ? ColorRef(TokenColor("Text")) : AppNative.DWMWA_COLOR_DEFAULT;
            int border = custom ? ColorRef(TokenColor("WindowBorder")) : AppNative.DWMWA_COLOR_DEFAULT;
            AppNative.DwmSetWindowAttribute(hwnd, AppNative.DWMWA_CAPTION_COLOR, ref caption, 4);
            AppNative.DwmSetWindowAttribute(hwnd, AppNative.DWMWA_TEXT_COLOR, ref text, 4);
            AppNative.DwmSetWindowAttribute(hwnd, AppNative.DWMWA_BORDER_COLOR, ref border, 4);
        }

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }
    }
}
