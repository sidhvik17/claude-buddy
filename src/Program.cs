using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Claude Buddy")]
[assembly: System.Reflection.AssemblyProduct("Claude Buddy")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
// Declaring a 4.6.2+ target switches System.IO to long-path-capable handling. Transcript
// paths under ~/.claude/projects can exceed MAX_PATH because the folder name encodes the
// whole working directory. (Setting the AppContext switches in Main is too late: the
// runtime reads them before Main runs.)
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace ClaudeBuddy
{
    internal static class Program
    {
        const string InstanceName = "Local\\ClaudeBuddy.Instance";
        const string QuitName = "Local\\ClaudeBuddy.Quit";

        [STAThread]
        static int Main(string[] args)
        {
            Options opts = new Options();
            string claudeDir = SessionScanner.DefaultClaudeDir();
            opts.SessionsDir = Path.Combine(claudeDir, "sessions");
            opts.ProjectsDir = Path.Combine(claudeDir, "projects");

            string command = null;
            string commandArg = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--dev") opts.Dev = true;
                else if (a == "--sessions-dir" && next != null) { opts.SessionsDir = next; i++; }
                else if (a == "--projects-dir" && next != null) { opts.ProjectsDir = next; i++; }
                else if (a == "--state" && next != null)
                {
                    BuddyState forced;
                    if (Enum.TryParse(next, true, out forced))
                    {
                        opts.HasForcedState = true;
                        opts.ForcedState = forced;
                    }
                    i++;
                }
                else if (a == "--quit") command = a;
                else if ((a == "--selftest" || a == "--render" || a == "--make-icon" || a == "--dump") && next != null)
                {
                    command = a;
                    commandArg = next;
                    i++;
                }
                else
                {
                    // Never fall through to a normal start: that would register autostart
                    // for whatever exe a mistyped command was run from.
                    Log.Write("unknown or incomplete argument: " + a);
                    return 2;
                }
            }

            try
            {
                if (command == "--selftest") return SelfTest.Run(commandArg);
                if (command == "--render") { Sprite.WriteSheet(commandArg, 6); return 0; }
                if (command == "--make-icon") { Sprite.WriteIco(commandArg); return 0; }
                if (command == "--dump") { Dump(opts, commandArg); return 0; }
                if (command == "--quit") { SignalQuit(opts.Dev); return 0; }
            }
            catch (Exception ex)
            {
                Log.Write(command + " failed: " + ex);
                return 2;
            }

            return RunBuddy(opts);
        }

        static int RunBuddy(Options opts)
        {
            string suffix = opts.Dev ? ".Dev" : "";
            bool isFirst;
            using (Mutex instance = new Mutex(true, InstanceName + suffix, out isFirst))
            {
                if (!isFirst) return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    Log.Write("unhandled UI exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log.Write("fatal: " + e.ExceptionObject);
                };

                Config.Load(opts.Dev);
                // Dev runs come from a build folder; never point Windows startup at one.
                if (!opts.Dev) AutoStart.Apply(Config.AutoStart);

                using (EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName + suffix))
                using (BuddyForm form = new BuddyForm(opts))
                {
                    Thread watcher = new Thread(delegate()
                    {
                        try
                        {
                            quit.WaitOne();
                            if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke((MethodInvoker)form.Close);
                        }
                        catch (Exception)
                        {
                            // Shutting down already.
                        }
                    });
                    watcher.IsBackground = true;
                    watcher.Start();

                    Log.Write("started (pid " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                        ", watching " + opts.SessionsDir + ")");
                    Application.Run(form);
                    Log.Write("stopped");
                }
                GC.KeepAlive(instance);
            }
            return 0;
        }

        // Asks a running buddy to exit cleanly (used by the install/uninstall scripts).
        static void SignalQuit(bool dev)
        {
            EventWaitHandle quit;
            if (EventWaitHandle.TryOpenExisting(QuitName + (dev ? ".Dev" : ""), out quit))
            {
                using (quit) quit.Set();
            }
        }

        // One scan of the real state files, written as text. For checking what the buddy sees.
        static void Dump(Options opts, string outFile)
        {
            SessionScanner scanner = new SessionScanner(opts.SessionsDir, opts.ProjectsDir);
            List<SessionInfo> sessions = scanner.Scan();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("sessionsDir: " + opts.SessionsDir);
            sb.AppendLine("state files: " + scanner.LastFileCount.ToString(CultureInfo.InvariantCulture) +
                (scanner.LastScanError != null ? "  (scan error: " + scanner.LastScanError + ")" : ""));
            sb.AppendLine("aggregate: " + SessionScanner.Aggregate(sessions));
            sb.AppendLine("usage limit: " + (scanner.LimitResetUtc > DateTime.UtcNow
                ? "resets " + scanner.LimitResetUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "none in force"));
            foreach (SessionInfo s in sessions)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-8} pid={1,-6} status={2,-8} host={3} name=\"{4}\" text=\"{5}\"",
                    s.State, s.Pid, s.Status, s.HostSessionId.Length > 0 ? "yes" : "no", s.DisplayName, s.StateText));
            }
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
