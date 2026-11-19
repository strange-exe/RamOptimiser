using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;
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
        static SingleInstance lockInstance;   // the lock this copy runs with, once it goes on to start up
        static int fatalShown;

        [STAThread]
        public static int Main(string[] args)
        {
            RamOptimiser.Native.RestrictDllSearchToSystem32();
            ComSecurity.Apply(); // before WPF or anything else marshals: COM security is set once per process
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            LaunchOptions options = LaunchOptions.Parse(args);

            // A name held by another owner or object type is not a reason to exit: run without single-instance.
            SingleInstance instance = null;
            try { instance = new SingleInstance(); }
            catch (SingleInstanceNameTakenException ex)
            {
                ErrorLog.TryWrite(ErrorLog.DefaultPath, NameHeldLine, ex);
            }
            catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "single-instance lock unavailable", ex); }

            // Started by Restart as administrator: the copy that asked still holds the lock until it has seen the
            // launch succeed. Wait (at most 10 s) for it to exit, then take the lock instead of handing over to it.
            if (instance != null && !instance.IsFirstInstance && options.HandoffFrom > 0 && options.HandoffFrom != CurrentPid())
            {
                instance.Dispose();
                instance = null;
                try { instance = TakeOverFrom(options.HandoffFrom); }
                catch (SingleInstanceNameTakenException ex)
                {
                    ErrorLog.TryWrite(ErrorLog.DefaultPath, NameHeldLine, ex);
                }
                catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "single-instance lock unavailable", ex); }
            }

            if (instance != null && !instance.IsFirstInstance)
            {
                // Let the running copy bring its window to the front (this launch holds the foreground right).
                AppNative.AllowSetForegroundWindow(AppNative.ASFW_ANY);
                // A copy that is exiting (restoring the services Power Clean stopped) keeps the lock but no longer
                // answers: wait for it, bounded, and start as the first instance once it lets go. Nothing is relaunched.
                SecondLaunchOutcome outcome;
                SingleInstance taken;
                try
                {
                    outcome = SecondLaunch.Run(instance, () => new SingleInstance(), SecondLaunch.AckWait, SecondLaunch.ReleaseWait, SecondLaunch.RetryEvery, out taken);
                }
                catch (Exception ex)
                {
                    ErrorLog.TryWrite(ErrorLog.DefaultPath, "waiting for the running copy to close failed; this launch exits", ex);
                    return 0;
                }
                if (outcome == SecondLaunchOutcome.NameTaken)
                {
                    // Squatted while waiting: as at the first try, run without single-instance rather than exit.
                    ErrorLog.TryWrite(ErrorLog.DefaultPath, NameHeldLine, null);
                    instance = null;
                }
                else
                {
                    if (outcome != SecondLaunchOutcome.BecameFirst)
                    {
                        if (outcome == SecondLaunchOutcome.StillHeld) ErrorLog.TryWrite(ErrorLog.DefaultPath, SecondLaunch.StillHeldLine, null);
                        return 0;
                    }
                    instance = taken;
                }
            }

            lockInstance = instance;
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

        const string NameHeldLine = "single-instance name held by another program; running without single-instance";

        static readonly TimeSpan HandoffWait = TimeSpan.FromSeconds(10);

        static int CurrentPid()
        {
            using (Process self = Process.GetCurrentProcess()) return self.Id;
        }

        /// <summary>
        /// Waits for process pid to exit (bounded) and takes the lock. A pid that is already gone retries at once;
        /// one whose handle cannot be opened waits the full 10 s. A reused pid only delays start-up.
        /// </summary>
        static SingleInstance TakeOverFrom(int pid)
        {
            Process parent = null;
            try { parent = Process.GetProcessById(pid); }
            catch (ArgumentException) { }          // already exited
            catch (InvalidOperationException) { }
            try
            {
                using (WaitHandle released = ExitHandle(parent))
                    return SingleInstance.AcquireAfter(() => new SingleInstance(), released, HandoffWait);
            }
            finally { if (parent != null) parent.Dispose(); }
        }

        static WaitHandle ExitHandle(Process parent)
        {
            if (parent == null) return new ManualResetEvent(true);
            try { return new ProcessExitHandle(parent.Handle); }
            catch (InvalidOperationException) { return new ManualResetEvent(true); }  // exited meanwhile
            catch (System.ComponentModel.Win32Exception) { return new ManualResetEvent(false); } // cannot wait on it: full timeout
        }

        /// <summary>Lets WaitOne wait on a process handle without owning it (the Process object closes it).</summary>
        sealed class ProcessExitHandle : WaitHandle
        {
            public ProcessExitHandle(IntPtr handle)
            {
                SafeWaitHandle = new SafeWaitHandle(handle, false);
            }
        }

        static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            FailOnUiThread("unhandled error on the UI thread", e.Exception);
        }

        /// <summary>
        /// A fault on the UI thread: log it, tell the user, then Exit, which starts the Power Clean restore. The
        /// dispatcher handler uses it, and so does TrayController for its WinForms handlers, which WPF's
        /// DispatcherUnhandledException never sees.
        /// </summary>
        internal static void FailOnUiThread(string context, Exception ex)
        {
            Fatal(context, ex);
            // Exit rethrows a teardown fault once its restore has started; escaping from here would hand it to
            // WinForms' dialog or the AppDomain handler (FaultRoute.Quietly), so it is logged instead.
            FaultRoute.Quietly(() =>
            {
                if (shell != null) shell.Exit();
                else if (Application.Current != null) Application.Current.Shutdown(1);
            }, e => ErrorLog.TryWrite(ErrorLog.DefaultPath, "exit after an error failed", e));
        }

        static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // The process ends after this handler; remove the tray icon so no ghost is left behind.
            Fatal("unhandled error", e.ExceptionObject as Exception);
            if (shell != null) shell.EmergencyCleanup();
        }

        /// <summary>
        /// Stops answering launches, logs, then tells the user in plain words. Shown once, however many errors follow.
        /// Every fatal path (FailOnUiThread, OnDomainUnhandledException, a failed start-up) comes through here.
        /// </summary>
        static void Fatal(string context, Exception ex)
        {
            // The handler is still subscribed while the box is up (or none was ever added, when start-up failed), so
            // a launch would be answered by a copy about to close and exit with it. It waits for the lock instead.
            StopAnswering();
            bool logged = ErrorLog.TryWrite(ErrorLog.DefaultPath, context, ex);
            if (Interlocked.Exchange(ref fatalShown, 1) != 0) return;
            string text = "RAM Optimiser ran into a problem and has to close.\n\n"
                + (ex == null ? "" : ex.Message + "\n\n")
                + (logged ? "Details were saved to " + ErrorLog.DefaultPath : "The details could not be saved to a log file.");
            try { MessageBox.Show(text, AppText.AppName, MessageBoxButton.OK, MessageBoxImage.Error); }
            catch (Exception) { }
        }

        /// <summary>Marks this copy's lock as closing (SingleInstance.BeginClosing); nothing when it has none.</summary>
        static void StopAnswering()
        {
            SingleInstance running = lockInstance;
            if (running != null) running.BeginClosing();
        }
    }
}
