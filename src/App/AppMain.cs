using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RamOptimiser.Gui;

// Without this attribute the runtime treats a csc-built exe as targeting .NET Framework 4.0 and turns on
// compatibility quirks: no per-monitor DPI scaling in WPF and the legacy accessibility behaviour
// (no AutomationProperties.LiveSetting announcements, older focus handling).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("RAM Optimiser")]
[assembly: System.Reflection.AssemblyProduct("RAM Optimiser")]

namespace RamOptimiser.App
{
    public static class AppMain
    {
        static AppShell shell;
        static int fatalShown;

        [STAThread]
        public static int Main(string[] args)
        {
            RamOptimiser.Native.RestrictDllSearchToSystem32();
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            LaunchOptions options = LaunchOptions.Parse(args);

            // A name held by another owner or object type is not a reason to exit: run without single-instance.
            SingleInstance instance = null;
            try { instance = new SingleInstance(); }
            catch (SingleInstanceNameTakenException ex)
            {
                ErrorLog.TryWrite(ErrorLog.DefaultPath, "single-instance name held by another program; running without single-instance", ex);
            }
            catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "single-instance lock unavailable", ex); }

            if (instance != null && !instance.IsFirstInstance)
            {
                // Let the running copy bring its window to the front (this launch holds the foreground right).
                AppNative.AllowSetForegroundWindow(AppNative.ASFW_ANY);
                instance.SignalFirstInstance();
                instance.Dispose();
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            try
            {
                shell = new AppShell(app, instance, options);
                shell.Start();
            }
            catch (Exception ex)
            {
                // The tray icon and the auto-clean timer may already exist: remove them before the error box, whose
                // message loop would otherwise run their clicks and work against a shell that failed to start.
                if (shell != null) shell.EmergencyCleanup();
                Fatal("start-up failed", ex);
                return 1;
            }
            return app.Run();
        }

        static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Fatal("unhandled error on the UI thread", e.Exception);
            if (shell != null) shell.Exit();
            else Application.Current.Shutdown(1);
        }

        static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // The process ends after this handler; remove the tray icon so no ghost is left behind.
            Fatal("unhandled error", e.ExceptionObject as Exception);
            if (shell != null) shell.EmergencyCleanup();
        }

        /// <summary>Logs, then tells the user in plain words. Shown once, however many errors follow.</summary>
        static void Fatal(string context, Exception ex)
        {
            bool logged = ErrorLog.TryWrite(ErrorLog.DefaultPath, context, ex);
            if (Interlocked.Exchange(ref fatalShown, 1) != 0) return;
            string text = "RAM Optimiser ran into a problem and has to close.\n\n"
                + (ex == null ? "" : ex.Message + "\n\n")
                + (logged ? "Details were saved to " + ErrorLog.DefaultPath : "The details could not be saved to a log file.");
            try { MessageBox.Show(text, AppText.AppName, MessageBoxButton.OK, MessageBoxImage.Error); }
            catch (Exception) { }
        }
    }
}
