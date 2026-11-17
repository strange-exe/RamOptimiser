using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace RamOptimiser.Gui
{
    /// <summary>The outcome of a Task Scheduler change (COM call or schtasks.exe run).</summary>
    public sealed class SchtasksResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
        public bool TimedOut;
        public bool Success { get { return !TimedOut && ExitCode == 0; } }
    }

    /// <summary>
    /// Start-with-Windows through Task Scheduler: a Run key cannot start an app elevated, a task with
    /// RunLevel HighestAvailable can. The task is registered from a Task Scheduler 1.2 XML definition, which
    /// also lifts the 72-hour run limit and the battery rules that the /SC ONLOGON form applies.
    ///
    /// The definition is handed to the Task Scheduler 2.0 COM API as a string (ITaskFolder.RegisterTask), so
    /// no file in the user-writable TEMP folder is ever read by the elevated side. The task only starts at
    /// logon: AllowStartOnDemand is false, so no other process can start the elevated app through it.
    /// Creating or deleting the task needs an elevated caller.
    /// </summary>
    public static class StartupTask
    {
        public const string TaskName = "RamOptimiser";
        public const string TrayArgument = "--tray";
        public const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        public const string RunLevel = "HighestAvailable";
        /// <summary>Longest program path accepted (the Unicode limit for a Windows path).</summary>
        public const int MaxExePathLength = 32767;
        /// <summary>
        /// Task priority 5 runs the program in the normal priority class; the schema default (7) would
        /// start the app below normal priority, which makes its window sluggish when the PC is busy.
        /// </summary>
        public const int TaskPriority = 5;
        public const int TimeoutMilliseconds = 10000;

        /// <summary>TASK_CREATION.TASK_CREATE_OR_UPDATE (TASK_CREATE 2 | TASK_UPDATE 4).</summary>
        public const int TASK_CREATE_OR_UPDATE = 6;
        /// <summary>TASK_LOGON_TYPE.TASK_LOGON_INTERACTIVE_TOKEN: runs in the user's own session, no password.</summary>
        public const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

        /// <summary>The task definition. userId is DOMAIN\user (see CurrentUserId).</summary>
        public static string BuildTaskXml(string exePath, string userId)
        {
            ValidateExePath(exePath);
            if (string.IsNullOrEmpty(userId)) throw new ArgumentException("A user is required.", "userId");

            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false };
            // A StringWriter reports UTF-16, so the declaration says encoding="utf-16", which matches the
            // string RegisterTask receives (a BSTR is UTF-16).
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (XmlWriter w = XmlWriter.Create(sw, settings))
            {
                w.WriteStartDocument();
                w.WriteStartElement("Task", TaskNamespace);
                w.WriteAttributeString("version", "1.2");

                w.WriteStartElement("RegistrationInfo", TaskNamespace);
                w.WriteElementString("Description", TaskNamespace, "Starts RAM Optimiser in the notification area when you sign in.");
                w.WriteEndElement();

                w.WriteStartElement("Triggers", TaskNamespace);
                w.WriteStartElement("LogonTrigger", TaskNamespace);
                w.WriteElementString("Enabled", TaskNamespace, "true");
                w.WriteElementString("UserId", TaskNamespace, userId);
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("Principals", TaskNamespace);
                w.WriteStartElement("Principal", TaskNamespace);
                w.WriteAttributeString("id", "Author");
                w.WriteElementString("UserId", TaskNamespace, userId);
                w.WriteElementString("LogonType", TaskNamespace, "InteractiveToken");
                w.WriteElementString("RunLevel", TaskNamespace, RunLevel);
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("Settings", TaskNamespace);
                w.WriteElementString("MultipleInstancesPolicy", TaskNamespace, "IgnoreNew");
                w.WriteElementString("DisallowStartIfOnBatteries", TaskNamespace, "false");
                w.WriteElementString("StopIfGoingOnBatteries", TaskNamespace, "false");
                // Logon only: schtasks /Run (or any other caller) may not start the elevated app on demand.
                w.WriteElementString("AllowStartOnDemand", TaskNamespace, "false");
                w.WriteElementString("ExecutionTimeLimit", TaskNamespace, "PT0S");
                w.WriteElementString("Priority", TaskNamespace, TaskPriority.ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("Enabled", TaskNamespace, "true");
                w.WriteEndElement();

                w.WriteStartElement("Actions", TaskNamespace);
                w.WriteAttributeString("Context", "Author");
                w.WriteStartElement("Exec", TaskNamespace);
                w.WriteElementString("Command", TaskNamespace, exePath);
                w.WriteElementString("Arguments", TaskNamespace, TrayArgument);
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteEndElement();
                w.WriteEndDocument();
            }
            return sb.ToString();
        }

        /// <summary>
        /// The arguments of ITaskFolder.RegisterTask(path, xmlText, flags, userId, password, logonType, sddl):
        /// create or update, the user and principal from the XML, no password, no security descriptor.
        /// </summary>
        internal static object[] BuildRegisterArgs(string xml)
        {
            if (xml == null) throw new ArgumentNullException("xml");
            return new object[] { TaskName, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null };
        }

        public static string BuildDeleteArgs()
        {
            return "/Delete /TN \"" + TaskName + "\" /F";
        }

        /// <summary>
        /// True only when a registered task definition runs exePath with exactly --tray at HighestAvailable,
        /// as its only action. Anything else (another program, other arguments, a lower run level, a DTD,
        /// malformed XML) reads as "not our task". exePath must itself pass the path checks.
        /// </summary>
        public static bool MatchesRegisteredTask(string xml, string exePath)
        {
            if (string.IsNullOrEmpty(xml) || !IsValidExePath(exePath)) return false;
            var doc = new XmlDocument { XmlResolver = null };
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var sr = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(sr, readerSettings))
                    doc.Load(reader);
            }
            catch (XmlException) { return false; }

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", TaskNamespace);
            if (doc.SelectSingleNode("/t:Task", ns) == null) return false;

            XmlNodeList actions = doc.SelectNodes("/t:Task/t:Actions/*", ns);
            XmlNodeList execs = doc.SelectNodes("/t:Task/t:Actions/t:Exec", ns);
            if (actions.Count != 1 || execs.Count != 1) return false;
            string command = Text(execs[0].SelectSingleNode("t:Command", ns));
            string arguments = Text(execs[0].SelectSingleNode("t:Arguments", ns));
            if (command == null || !string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (arguments != TrayArgument) return false;

            XmlNodeList principals = doc.SelectNodes("/t:Task/t:Principals/t:Principal", ns);
            if (principals.Count != 1) return false;
            return Text(principals[0].SelectSingleNode("t:RunLevel", ns)) == RunLevel;
        }

        static string Text(XmlNode node) { return node == null ? null : node.InnerText.Trim(); }

        /// <summary>DOMAIN\user of the process token, the form LogonTrigger and Principal expect.</summary>
        public static string CurrentUserId()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return identity.Name;
        }

        public static bool IsValidExePath(string exePath)
        {
            string reason;
            return CheckExePath(exePath, out reason);
        }

        /// <summary>Throws ArgumentException naming the problem. Whether the file exists is the caller's check.</summary>
        public static void ValidateExePath(string exePath)
        {
            string reason;
            if (!CheckExePath(exePath, out reason)) throw new ArgumentException(reason, "exePath");
        }

        static bool CheckExePath(string p, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(p)) { reason = "The program path is empty."; return false; }
            if (p.IndexOf('"') >= 0) { reason = "The program path must not contain a double quote."; return false; }
            if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { reason = "The program path contains invalid characters."; return false; }
            // Task Scheduler expands %VARIABLES% in the command, which would make the path mean something else.
            if (p.IndexOf('%') >= 0) { reason = "The program path must not contain a percent sign."; return false; }
            if (p.Length >= 2 && IsSeparator(p[0]) && IsSeparator(p[1]))
            {
                reason = "The program must be on a local drive, not a network share or device path.";
                return false;
            }
            if (!IsFullyQualified(p)) { reason = "The program path must be absolute (for example C:\\Apps\\RamOptimiser.exe)."; return false; }
            if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { reason = "The program path must end with .exe."; return false; }
            if (p.Length > MaxExePathLength) { reason = "The program path is too long."; return false; }
            return true;
        }

        /// <summary>Drive-absolute only (C:\x). Rejects C:x and \x, which are relative.</summary>
        internal static bool IsFullyQualified(string p)
        {
            return p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && IsSeparator(p[2]);
        }

        static bool IsAsciiLetter(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }
        static bool IsSeparator(char c) { return c == '\\' || c == '/'; }

        // ---- Execution (touches the real Task Scheduler: never called from tests) ----

        /// <summary>Registers (or replaces) the logon task through the Task Scheduler COM API.</summary>
        public static SchtasksResult Enable(string exePath)
        {
            string xml = BuildTaskXml(exePath, CurrentUserId());
            try
            {
                WithRootFolder(folder => Release(Call(folder, "RegisterTask", BuildRegisterArgs(xml))));
                return new SchtasksResult { ExitCode = 0, StdOut = "", StdErr = "" };
            }
            catch (Exception ex) { return Failure(ex); }
        }

        /// <summary>A failed COM call as a result: the HRESULT as exit code, the message as error text.</summary>
        static SchtasksResult Failure(Exception ex)
        {
            Exception inner = Unwrap(ex);
            int code = Marshal.GetHRForException(inner);
            return new SchtasksResult { ExitCode = code == 0 ? -1 : code, StdOut = "", StdErr = inner.Message };
        }

        public static SchtasksResult Disable()
        {
            return Run(BuildDeleteArgs());
        }

        /// <summary>
        /// True when the registered task is ours for this program: it runs the running exe with --tray at
        /// HighestAvailable. A missing task, another definition or a failure to read it reads as false.
        /// </summary>
        public static bool IsEnabled()
        {
            SchtasksResult ignored;
            return IsEnabled(out ignored);
        }

        public static bool IsEnabled(out SchtasksResult result)
        {
            Assembly entry = Assembly.GetEntryAssembly();
            return IsEnabled(entry == null ? null : entry.Location, out result);
        }

        public static bool IsEnabled(string exePath, out SchtasksResult result)
        {
            string xml;
            result = ReadRegisteredXml(out xml);
            if (!result.Success) return false;
            if (MatchesRegisteredTask(xml, exePath)) return true;
            result = new SchtasksResult { ExitCode = 0, StdOut = "", StdErr = "The registered task does not start this program." };
            return false;
        }

        /// <summary>
        /// Reads the task definition through COM (IRegisteredTask.Xml). schtasks /Query /XML writes it to a
        /// pipe in the OEM code page, which loses non-ASCII characters in a program path; COM returns the
        /// exact UTF-16 text.
        /// </summary>
        static SchtasksResult ReadRegisteredXml(out string xml)
        {
            string text = null;
            try
            {
                WithRootFolder(folder =>
                {
                    object task = Call(folder, "GetTask", TaskName);
                    try { text = Get(task, "Xml") as string; }
                    finally { Release(task); }
                });
                xml = text;
                return new SchtasksResult { ExitCode = 0, StdOut = "", StdErr = "" };
            }
            catch (Exception ex)
            {
                xml = null; // missing task (0x80070002), no access, or no Task Scheduler: not enabled
                return Failure(ex);
            }
        }

        /// <summary>Connects to the local Task Scheduler and runs body on the root folder, releasing both.</summary>
        static void WithRootFolder(Action<object> body)
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service", true);
            object service = Activator.CreateInstance(type);
            try
            {
                // Connect(serverName, user, domain, password): all omitted = this computer, this user.
                Call(service, "Connect", Missing.Value, Missing.Value, Missing.Value, Missing.Value);
                object folder = Call(service, "GetFolder", "\\");
                try { body(folder); }
                finally { Release(folder); }
            }
            finally { Release(service); }
        }

        static object Call(object target, string method, params object[] args)
        {
            return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);
        }

        static object Get(object target, string property)
        {
            return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
        }

        static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }

        /// <summary>Late-bound calls wrap the COM error in a TargetInvocationException.</summary>
        static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        static string SchtasksPath
        {
            // GetSystemDirectory, not %WINDIR% from the environment, which the caller's parent can change.
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); }
        }

        static SchtasksResult Run(string args)
        {
            Encoding oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo(SchtasksPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem
            };
            var result = new SchtasksResult { ExitCode = -1, StdOut = "", StdErr = "" };
            Process p;
            try { p = Process.Start(psi); }
            catch (Win32Exception ex) { result.StdErr = ex.Message; return result; }
            using (p)
            {
                // Read both pipes concurrently so a full buffer can never block the child.
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(TimeoutMilliseconds))
                {
                    result.TimedOut = true;
                    try { p.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    result.StdErr = "schtasks.exe did not finish within " + (TimeoutMilliseconds / 1000) + " seconds.";
                    return result;
                }
                p.WaitForExit(); // flushes the redirected streams
                result.ExitCode = p.ExitCode;
                if (stdout.Wait(TimeoutMilliseconds)) result.StdOut = stdout.Result;
                if (stderr.Wait(TimeoutMilliseconds)) result.StdErr = stderr.Result;
                return result;
            }
        }
    }
}
