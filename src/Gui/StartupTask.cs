using System;
using System.Collections.Generic;
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

    /// <summary>One task read: the definition, or the HRESULT and message of the failed read.</summary>
    public sealed class TaskRead
    {
        readonly string xml, error;
        readonly int hresult;

        TaskRead(string xml, int hresult, string error)
        {
            this.xml = xml;
            this.hresult = hresult;
            this.error = error;
        }

        public static TaskRead Found(string xml) { return new TaskRead(xml ?? "", 0, null); }
        public static TaskRead Failed(int hresult, string error) { return new TaskRead(null, hresult, error); }

        public string Xml { get { return xml; } }
        public string Error { get { return error; } }
        public int HResult { get { return hresult; } }
        public bool Exists { get { return xml != null; } }
        /// <summary>No such task. Any other failure (access denied, no Task Scheduler) is not "missing".</summary>
        public bool Missing { get { return xml == null && hresult == StartupTask.HResultFileNotFound; } }
    }

    /// <summary>
    /// Absent: no task for this user. Ours: it starts this program as it should. Other: a task exists but
    /// starts another copy or differs. Unreadable: the task could not be read, so its state is unknown.
    /// </summary>
    public enum StartupTaskState { Absent, Ours, Other, Unreadable }

    public sealed class StartupTaskStatus
    {
        public StartupTaskState State;
        /// <summary>The program a stale task starts (Other), when it has exactly one.</summary>
        public string Command;
        /// <summary>Other only: the task starts this program, but with other arguments or a lower run level.</summary>
        public bool SameProgram;
        /// <summary>The task is this user's, under the old shared name "RamOptimiser".</summary>
        public bool Legacy;
        /// <summary>Unreadable only: why.</summary>
        public string Error;
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
        /// <summary>
        /// The one machine-wide name older versions used. A second account turning the option on replaced
        /// the first account's task; it is now read only to migrate this user's own task, never another's.
        /// </summary>
        public const string LegacyTaskName = "RamOptimiser";
        /// <summary>Each account's task is "RamOptimiser-&lt;SID&gt;", so two accounts never share one.</summary>
        public const string TaskNamePrefix = LegacyTaskName + "-";
        /// <summary>HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND): GetTask's answer for a task that does not exist.</summary>
        public const int HResultFileNotFound = unchecked((int)0x80070002);
        /// <summary>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED): GetTask's answer for a task this user may not read.</summary>
        public const int HResultAccessDenied = unchecked((int)0x80070005);
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
        internal static object[] BuildRegisterArgs(string taskName, string xml)
        {
            if (!IsOurTaskName(taskName)) throw new ArgumentException("Not a RAM Optimiser task name.", "taskName");
            if (xml == null) throw new ArgumentNullException("xml");
            return new object[] { taskName, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null };
        }

        /// <summary>schtasks arguments that delete one of our tasks; any other name is refused.</summary>
        public static string BuildDeleteArgs(string taskName)
        {
            if (!IsOurTaskName(taskName)) throw new ArgumentException("Not a RAM Optimiser task name.", "taskName");
            return "/Delete /TN \"" + taskName + "\" /F";
        }

        /// <summary>This account's task name. The SID is checked strictly, so the name is always safe to quote.</summary>
        public static string TaskNameFor(string sid)
        {
            if (!Elevation.IsWellFormedSid(sid)) throw new ArgumentException("Not a SID.", "sid");
            return TaskNamePrefix + sid;
        }

        static bool IsOurTaskName(string name)
        {
            if (name == LegacyTaskName) return true;
            return name != null && name.StartsWith(TaskNamePrefix, StringComparison.Ordinal)
                && Elevation.IsWellFormedSid(name.Substring(TaskNamePrefix.Length));
        }

        /// <summary>
        /// True only when a registered task definition runs exePath with exactly --tray at HighestAvailable,
        /// as its only action, for this user (the Principal UserId is userSid or userName). Anything else
        /// (another program or account, other arguments, a lower run level, a DTD, malformed XML) reads as
        /// "not our task". exePath must itself pass the path checks.
        /// </summary>
        public static bool MatchesRegisteredTask(string xml, string exePath, string userSid, string userName)
        {
            if (!IsValidExePath(exePath)) return false;
            XmlNamespaceManager ns;
            XmlDocument doc = LoadTask(xml, out ns);
            if (doc == null || !PrincipalIs(doc, ns, userSid, userName)) return false;

            XmlNodeList actions = doc.SelectNodes("/t:Task/t:Actions/*", ns);
            if (actions.Count != 1) return false;
            string command = ExecCommand(doc, ns);
            XmlNode exec = doc.SelectSingleNode("/t:Task/t:Actions/t:Exec", ns);
            string arguments = exec == null ? null : Text(exec.SelectSingleNode("t:Arguments", ns));
            if (command == null || !string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (arguments != TrayArgument) return false;

            XmlNodeList principals = doc.SelectNodes("/t:Task/t:Principals/t:Principal", ns);
            return Text(principals[0].SelectSingleNode("t:RunLevel", ns)) == RunLevel;
        }

        /// <summary>Parses a task definition with DTDs refused; null when it is not a Task Scheduler task.</summary>
        static XmlDocument LoadTask(string xml, out XmlNamespaceManager ns)
        {
            ns = null;
            if (string.IsNullOrEmpty(xml)) return null;
            var doc = new XmlDocument { XmlResolver = null };
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var sr = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(sr, readerSettings))
                    doc.Load(reader);
            }
            catch (XmlException) { return null; }

            ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", TaskNamespace);
            return doc.SelectSingleNode("/t:Task", ns) == null ? null : doc;
        }

        /// <summary>One principal whose UserId is this user's SID or DOMAIN\user (Task Scheduler stores either).</summary>
        static bool PrincipalIs(XmlDocument doc, XmlNamespaceManager ns, string userSid, string userName)
        {
            XmlNodeList principals = doc.SelectNodes("/t:Task/t:Principals/t:Principal", ns);
            if (principals.Count != 1) return false;
            string id = Text(principals[0].SelectSingleNode("t:UserId", ns));
            if (string.IsNullOrEmpty(id)) return false;
            return (!string.IsNullOrEmpty(userSid) && string.Equals(id, userSid, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(userName) && string.Equals(id, userName, StringComparison.OrdinalIgnoreCase));
        }

        static bool PrincipalIs(string xml, string userSid, string userName)
        {
            XmlNamespaceManager ns;
            XmlDocument doc = LoadTask(xml, out ns);
            return doc != null && PrincipalIs(doc, ns, userSid, userName);
        }

        /// <summary>The program of the task's only Exec action, or null.</summary>
        static string ExecCommand(XmlDocument doc, XmlNamespaceManager ns)
        {
            XmlNodeList execs = doc.SelectNodes("/t:Task/t:Actions/t:Exec", ns);
            return execs.Count == 1 ? Text(execs[0].SelectSingleNode("t:Command", ns)) : null;
        }

        static string Text(XmlNode node) { return node == null ? null : node.InnerText.Trim(); }

        /// <summary>
        /// What Settings shows. Reads the task named for this user and the old shared name: the old task
        /// counts only when its principal is this user (another account's task, including one this user is denied
        /// reading, is never shown or touched).
        /// A stale task, this user's or the old one, wins over an exact match because it still starts the
        /// other copy at sign-in.
        /// </summary>
        public static StartupTaskStatus Classify(TaskRead userTask, TaskRead legacyTask, string exePath, string userSid, string userName)
        {
            StartupTaskStatus mine = Judge(userTask, exePath, userSid, userName, false);
            StartupTaskStatus old = Judge(legacyTask, exePath, userSid, userName, true);
            if (mine.State == StartupTaskState.Other) return mine;
            if (old.State == StartupTaskState.Other) return old;
            if (mine.State != StartupTaskState.Absent) return mine;
            return old;
        }

        static StartupTaskStatus Judge(TaskRead read, string exePath, string userSid, string userName, bool legacy)
        {
            var status = new StartupTaskStatus { State = StartupTaskState.Absent };
            if (read == null || read.Missing) return status;
            // A task's owner can always read it, so an old-named task this user is denied belongs to another account:
            // never shown. This user's own task, and other failures (no Task Scheduler), stay unreadable.
            if (legacy && read.HResult == HResultAccessDenied) return status;
            status.Legacy = legacy;
            if (!read.Exists)
            {
                status.State = StartupTaskState.Unreadable;
                status.Error = read.Error;
                return status;
            }
            if (legacy && !PrincipalIs(read.Xml, userSid, userName)) return new StartupTaskStatus { State = StartupTaskState.Absent };
            if (MatchesRegisteredTask(read.Xml, exePath, userSid, userName))
            {
                status.State = StartupTaskState.Ours;
                return status;
            }
            status.State = StartupTaskState.Other;
            XmlNamespaceManager ns;
            XmlDocument doc = LoadTask(read.Xml, out ns);
            status.Command = doc == null ? null : ExecCommand(doc, ns);
            status.SameProgram = status.Command != null && IsValidExePath(exePath)
                && string.Equals(status.Command, exePath, StringComparison.OrdinalIgnoreCase);
            return status;
        }

        /// <summary>
        /// Task names to delete. Turning the option on removes only this user's old-named task (the new one
        /// replaces it); turning it off removes this user's task too. The old-named task of another account
        /// is never on the list, and neither is one that could not be read.
        /// </summary>
        public static List<string> TasksToRemove(TaskRead userTask, TaskRead legacyTask, string userSid, string userName, bool enabling)
        {
            var names = new List<string>();
            if (!enabling && userTask != null && userTask.Exists) names.Add(TaskNameFor(userSid));
            if (legacyTask != null && legacyTask.Exists && PrincipalIs(legacyTask.Xml, userSid, userName)) names.Add(LegacyTaskName);
            return names;
        }

        /// <summary>DOMAIN\user of the process token, the form LogonTrigger and Principal expect.</summary>
        public static string CurrentUserId()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return identity.Name;
        }

        /// <summary>The SID of the process token (S-1-5-21-...), which names this user's task.</summary>
        public static string CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return identity.User == null ? null : identity.User.Value;
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

        /// <summary>
        /// Registers (or replaces) this user's logon task through the Task Scheduler COM API, then removes this
        /// user's task under the old shared name, which would otherwise start the app a second time.
        /// </summary>
        public static SchtasksResult Enable(string exePath)
        {
            string sid = CurrentUserSid(), user = CurrentUserId();
            string name = TaskNameFor(sid);
            string xml = BuildTaskXml(exePath, user);
            TaskRead legacy = ReadTask(LegacyTaskName);
            try { WithRootFolder(folder => Release(Call(folder, "RegisterTask", BuildRegisterArgs(name, xml)))); }
            catch (Exception ex) { return Failure(ex); }
            return Delete(TasksToRemove(null, legacy, sid, user, true));
        }

        /// <summary>A failed COM call as a result: the HRESULT as exit code, the message as error text.</summary>
        static SchtasksResult Failure(Exception ex)
        {
            Exception inner = Unwrap(ex);
            int code = Marshal.GetHRForException(inner);
            return new SchtasksResult { ExitCode = code == 0 ? -1 : code, StdOut = "", StdErr = inner.Message };
        }

        /// <summary>Removes this user's task and this user's old-named task; another account's task is left alone.</summary>
        public static SchtasksResult Disable()
        {
            string sid = CurrentUserSid(), user = CurrentUserId();
            return Delete(TasksToRemove(ReadTask(TaskNameFor(sid)), ReadTask(LegacyTaskName), sid, user, false));
        }

        static SchtasksResult Delete(List<string> names)
        {
            foreach (string name in names)
            {
                SchtasksResult r = Run(BuildDeleteArgs(name));
                if (!r.Success) return r;
            }
            return new SchtasksResult { ExitCode = 0, StdOut = "", StdErr = "" };
        }

        /// <summary>This user's start-up task as Settings shows it, for the running program.</summary>
        public static StartupTaskStatus ReadStatus()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            string sid = CurrentUserSid(), user = CurrentUserId();
            return Classify(ReadTask(TaskNameFor(sid)), ReadTask(LegacyTaskName), entry == null ? null : entry.Location, sid, user);
        }

        /// <summary>
        /// Reads a task definition through COM (IRegisteredTask.Xml). schtasks /Query /XML writes it to a
        /// pipe in the OEM code page, which loses non-ASCII characters in a program path; COM returns the
        /// exact UTF-16 text. A missing task (0x80070002) and a refused read stay apart.
        /// </summary>
        static TaskRead ReadTask(string name)
        {
            string text = null;
            try
            {
                WithRootFolder(folder =>
                {
                    object task = Call(folder, "GetTask", name);
                    try { text = Get(task, "Xml") as string; }
                    finally { Release(task); }
                });
                return TaskRead.Found(text);
            }
            catch (Exception ex)
            {
                Exception inner = Unwrap(ex);
                return TaskRead.Failed(Marshal.GetHRForException(inner), inner.Message);
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
