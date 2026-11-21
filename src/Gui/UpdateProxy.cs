// The update check's proxy (audit run-6, owner go-ahead 2026-11-22). The app usually runs elevated, so the request
// takes only the user's manual proxy ("Use a proxy server"), never automatic detection (WPAD) or a setup script (PAC):
// those come from the network or a script, and the system default proxy would run them for this process. The proxy
// gets no credentials, so nothing logs on to it automatically. With no manual proxy the request goes direct; a network
// that only offers a PAC script then shows "Couldn't check", and the user can still open the download page.
//   WinHttpGetIEProxyConfigForCurrentUser, strings freed with GlobalFree:
//   https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpgetieproxyconfigforcurrentuser
//   Proxy and bypass list syntax ("[<scheme>=][<scheme>://]<server>[:<port>]", "<local>"):
//   https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ns-winhttp-winhttp_proxy_info
//   A WebProxy made from an address runs no script: https://learn.microsoft.com/en-us/dotnet/api/system.net.webproxy
using System;
using System.Net;
using System.Runtime.InteropServices;

namespace RamOptimiser.Gui
{
    public static class UpdateProxy
    {
        [StructLayout(LayoutKind.Sequential)]
        struct IeProxyConfig
        {
            public bool AutoDetect;
            public IntPtr AutoConfigUrl;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        [DllImport("winhttp.dll", SetLastError = true)]
        static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref IeProxyConfig config);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr mem);

        static readonly char[] Separators = { ';', ' ', '\t', '\r', '\n' };

        /// <summary>The proxy for target from the user's manual settings, or null to go direct. Never throws.</summary>
        internal static IWebProxy ForRequest(Uri target)
        {
            string list, bypass;
            if (!ReadManual(out list, out bypass)) return null;
            Uri address = Choose(list, bypass, target);
            if (address == null) return null;
            return new WebProxy(address) { Credentials = null, UseDefaultCredentials = false, BypassProxyOnLocal = false };
        }

        static bool ReadManual(out string list, out string bypass)
        {
            list = bypass = null;
            var c = new IeProxyConfig();
            try
            {
                if (!WinHttpGetIEProxyConfigForCurrentUser(ref c)) return false;
                list = c.Proxy == IntPtr.Zero ? null : Marshal.PtrToStringUni(c.Proxy);
                bypass = c.ProxyBypass == IntPtr.Zero ? null : Marshal.PtrToStringUni(c.ProxyBypass);
                return true;
            }
            catch (Exception) { return false; }
            finally
            {
                if (c.AutoConfigUrl != IntPtr.Zero) GlobalFree(c.AutoConfigUrl);
                if (c.Proxy != IntPtr.Zero) GlobalFree(c.Proxy);
                if (c.ProxyBypass != IntPtr.Zero) GlobalFree(c.ProxyBypass);
            }
        }

        /// <summary>
        /// The http proxy a manual proxy list names for target: its "scheme=" entry, else the first plain entry. Null
        /// (direct) when there is none, when that entry is not a plain http host and port, or when the bypass list
        /// covers the target's host.
        /// </summary>
        public static Uri Choose(string list, string bypass, Uri target)
        {
            if (string.IsNullOrEmpty(list) || target == null) return null;
            string specific = null, plain = null;
            foreach (string raw in list.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq < 0) { if (plain == null) plain = raw; continue; }
                if (specific == null && string.Equals(raw.Substring(0, eq), target.Scheme, StringComparison.OrdinalIgnoreCase))
                    specific = raw.Substring(eq + 1);
            }
            string server = specific ?? plain;
            if (string.IsNullOrEmpty(server) || Bypassed(bypass, target.Host)) return null;
            const string http = "http://";
            if (server.StartsWith(http, StringComparison.OrdinalIgnoreCase)) server = server.Substring(http.Length);
            if (server.Length == 0 || server.IndexOfAny(new[] { '/', '\\', '@', '?', '#' }) >= 0 || server.Contains("://")) return null;
            Uri address;
            if (!Uri.TryCreate(http + server, UriKind.Absolute, out address) || address.HostNameType == UriHostNameType.Unknown) return null;
            int colon = server.LastIndexOf(':');
            if (colon >= 0 && server.IndexOf(']') < colon)
            {
                int port;
                if (!int.TryParse(server.Substring(colon + 1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) return null;
            }
            return new Uri(http + address.Authority + "/");
        }

        /// <summary>Whether a bypass list entry ("&lt;local&gt;" or a host wildcard, scheme and port allowed) covers host.</summary>
        static bool Bypassed(string bypass, string host)
        {
            if (string.IsNullOrEmpty(bypass)) return false;
            foreach (string raw in bypass.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(raw, "<local>", StringComparison.OrdinalIgnoreCase)) { if (host.IndexOf('.') < 0) return true; continue; }
                string entry = raw;
                int scheme = entry.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0) entry = entry.Substring(scheme + 3);
                int port = entry.LastIndexOf(':');
                if (port >= 0 && entry.IndexOf(']') < port) entry = entry.Substring(0, port);
                if (Glob(entry.ToLowerInvariant(), host.ToLowerInvariant())) return true;
            }
            return false;
        }

        /// <summary>'*' matches any run of characters, everything else itself. Linear-time two-pointer match, no regex.</summary>
        public static bool Glob(string pattern, string text)
        {
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
                else if (p < pattern.Length && pattern[p] == text[t]) { p++; t++; }
                else if (star >= 0) { p = star + 1; t = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }
}
