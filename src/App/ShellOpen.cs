// Opens a page as the signed-in user from an elevated copy (owner decision 2026-11-19: "Check; open page via
// Explorer"). Starting the browser from here would run it as administrator, so the running Explorer is asked to
// do it: its desktop window's shell view hands back the Shell.Application object living in Explorer's process, and
// that object's ShellExecute runs there, unelevated. This is the route Raymond Chen gives in The Old New Thing,
// "How can I launch an unelevated process from my elevated process and vice versa?":
// IShellWindows.FindWindowSW(CSIDL_DESKTOP, SWC_DESKTOP, SWFO_NEEDDISPATCH) -> IServiceProvider.QueryService(
// SID_STopLevelBrowser, IShellBrowser) -> QueryActiveShellView -> GetItemObject(SVGIO_BACKGROUND, IDispatch) ->
// IShellFolderViewDual.Application -> IShellDispatch2.ShellExecute(url, "", "", "open", SW_SHOWNORMAL).
// https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nf-exdisp-ishellwindows-findwindowsw
// Any failure is reported, never retried another way: the caller shows the link to copy instead.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace RamOptimiser.App
{
    internal static class ShellOpen
    {
        /// <summary>How long Explorer gets before the open counts as failed (a hung Explorer would never answer).</summary>
        static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_BACKGROUND = 0, SW_SHOWNORMAL = 1;
        static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        static readonly Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        /// <summary>
        /// Asks Explorer to open url, on a thread of its own (COM wants an STA, and the UI must not wait on Explorer).
        /// done(true) once Explorer took it, done(false) on any failure or after Limit; called once, on a worker thread.
        /// </summary>
        public static void OpenUnelevated(string url, Action<bool> done)
        {
            int reported = 0;
            Action<bool> report = ok => { if (Interlocked.Exchange(ref reported, 1) == 0) done(ok); };
            var worker = new Thread(() =>
            {
                bool ok = false;
                try { ok = Open(url); }
                catch (Exception) { ok = false; }
                report(ok);
            }) { IsBackground = true, Name = "open-unelevated" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            ThreadPool.QueueUserWorkItem(delegate { if (!worker.Join(Limit)) report(false); });
        }

        static bool Open(string url)
        {
            object shellWindows = null, desktop = null, browser = null, background = null, application = null;
            IShellView view = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(CLSID_ShellWindows, true);
                shellWindows = Activator.CreateInstance(type);
                object loc = CSIDL_DESKTOP, root = null;
                int hwnd;
                int hr = ((IShellWindows)shellWindows).FindWindowSW(ref loc, ref root, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH, out desktop);
                if (hr != 0 || desktop == null) return false; // S_FALSE: no desktop window (Explorer is not running)
                var provider = desktop as IComServiceProvider;
                if (provider == null) return false;
                Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
                if (provider.QueryService(ref sid, ref iid, out browser) != 0 || browser == null) return false;
                if (((IShellBrowser)browser).QueryActiveShellView(out view) != 0 || view == null) return false;
                Guid dispatch = IID_IDispatch;
                if (view.GetItemObject(SVGIO_BACKGROUND, ref dispatch, out background) != 0 || background == null) return false;
                // IShellFolderViewDual.Application and IShellDispatch2.ShellExecute, late-bound through IDispatch.
                application = background.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, background, null);
                if (application == null) return false;
                application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application,
                    new object[] { url, "", "", "open", SW_SHOWNORMAL });
                return true;
            }
            finally
            {
                Release(application);
                Release(background);
                Release(view);
                Release(browser);
                Release(desktop);
                Release(shellWindows);
            }
        }

        static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ COM declarations (vtable order matters)

        /// <summary>exdisp.idl; dual, so the IDispatch slots come first. Only FindWindowSW is called.</summary>
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object Item([In, MarshalAs(UnmanagedType.Struct)] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object _NewEnum();
            void Register([In, MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
            void RegisterPending(int lThreadId, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc,
                [In, MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot, int swClass, out int plCookie);
            void Revoke(int lCookie);
            void OnNavigate(int lCookie, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc);
            void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);
            [PreserveSig]
            int FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
                int swClass, out int phwnd, int swfwOptions, [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut);
            void OnCreated(int lCookie, [In, MarshalAs(UnmanagedType.IUnknown)] object punk);
            void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool fAttach);
        }

        /// <summary>servprov.h IServiceProvider (not System.IServiceProvider).</summary>
        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
        }

        /// <summary>shobjidl_core.h; IOleWindow's two methods first. Only QueryActiveShellView is called.</summary>
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
            [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
            [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
            [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
            [PreserveSig] int EnableModelessSB(int fEnable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
            [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
            [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
            [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
            [PreserveSig] int QueryActiveShellView(out IShellView ppshv);
        }

        /// <summary>shobjidl_core.h; IOleWindow's two methods first. Only GetItemObject is called.</summary>
        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellView
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
            [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
            [PreserveSig] int EnableModeless(int fEnable);
            [PreserveSig] int UIActivate(uint uState);
            [PreserveSig] int Refresh();
            [PreserveSig] int CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
            [PreserveSig] int DestroyViewWindow();
            [PreserveSig] int GetCurrentInfo(IntPtr pfs);
            [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
            [PreserveSig] int SaveViewState();
            [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
            [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        }
    }
}
