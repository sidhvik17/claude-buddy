using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeBuddy
{
    // `ClaudeBuddy.exe --selftest <report.txt>`: exercises the logic against a throwaway
    // fake ~/.claude tree. Exit code = number of failed checks.
    internal static class SelfTest
    {
        static readonly StringBuilder Report = new StringBuilder();
        static int failures;

        public static int Run(string reportPath)
        {
            string root = Path.Combine(Path.GetTempPath(), "claude-buddy-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                TestJson();
                TestEncoding();
                TestHotkey();
                TestScanner(root);
                TestLimit(root);
                TestSprite();
            }
            catch (Exception ex)
            {
                Check("no unexpected exception: " + ex, false);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            Report.AppendLine(failures == 0 ? "ALL PASSED" : failures.ToString(CultureInfo.InvariantCulture) + " FAILED");
            File.WriteAllText(reportPath, Report.ToString());
            return failures;
        }

        static void Check(string name, bool ok)
        {
            if (!ok) failures++;
            Report.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
        }

        static void TestJson()
        {
            Dictionary<string, object> d = MiniJson.ParseObject(
                "{\"a\":1,\"b\":\"x\\n\\u00e9\\\"q\\\"\",\"c\":[true,false,null,{\"d\":-2.5e3}],\"e\":{},\"f\":\"134353273632667976\", \"g\" : 1790853764911 }");
            Check("json: number", MiniJson.GetLong(d, "a") == 1);
            Check("json: escapes", MiniJson.GetString(d, "b") == "x\n\u00e9\"q\"");
            List<object> c = d["c"] as List<object>;
            Check("json: array", c != null && c.Count == 4 && (bool)c[0] && !(bool)c[1] && c[2] == null);
            Check("json: nested number", c != null && (double)((Dictionary<string, object>)c[3])["d"] == -2500.0);
            Check("json: numeric string as long", MiniJson.GetLong(d, "f") == 134353273632667976L);
            Check("json: large integer", MiniJson.GetLong(d, "g") == 1790853764911L);
            Check("json: missing key", MiniJson.GetString(d, "zz") == "" && MiniJson.GetLong(d, "zz") == 0 && !MiniJson.GetBool(d, "zz"));

            bool threw = false;
            try { MiniJson.Parse("{\"a\":"); } catch (FormatException) { threw = true; }
            Check("json: truncated input rejected", threw);
            threw = false;
            try { MiniJson.Parse("{\"a\":1} x"); } catch (FormatException) { threw = true; }
            Check("json: trailing data rejected", threw);
        }

        static void TestEncoding()
        {
            Check("cwd encoding", SessionScanner.EncodeCwd("C:\\Users\\A B\\New folder (2)") == "C--Users-A-B-New-folder--2-");
            Check("safe id accepts uuid", SessionScanner.IsSafeId("local_2d482964-aaaa-bbbb-cccc-0123456789ab"));
            Check("safe id rejects traversal", !SessionScanner.IsSafeId("..\\..\\x") && !SessionScanner.IsSafeId("a/b") && !SessionScanner.IsSafeId(""));
            Check("status map", SessionScanner.MapStatus("busy") == BuddyState.Working && SessionScanner.MapStatus("shell") == BuddyState.Working &&
                SessionScanner.MapStatus("waiting") == BuddyState.Waiting && SessionScanner.MapStatus("idle") == BuddyState.Idle &&
                SessionScanner.MapStatus("something-new") == BuddyState.Idle);
        }

        static void TestHotkey()
        {
            Hotkey h = Hotkey.Parse("Ctrl+Alt+H");
            Check("hotkey: ctrl+alt+h", h != null && h.Modifiers == (Native.MOD_CONTROL | Native.MOD_ALT) && h.Key == 'H' && h.Display == "Ctrl+Alt+H");
            h = Hotkey.Parse(" control + shift+f9 ");
            Check("hotkey: spacing, case, function key", h != null && h.Modifiers == (Native.MOD_CONTROL | Native.MOD_SHIFT) && h.Key == 0x78 && h.Display == "Ctrl+Shift+F9");
            h = Hotkey.Parse("F12");
            Check("hotkey: bare function key allowed", h != null && h.Modifiers == 0 && h.Key == 0x7B);
            h = Hotkey.Parse("ctrl+h");
            Check("hotkey: ctrl+h accepted when asked for", h != null && h.Display == "Ctrl+H");
            Check("hotkey: rejects none / empty / bare letter / junk",
                Hotkey.Parse("none") == null && Hotkey.Parse("") == null && Hotkey.Parse(null) == null && Hotkey.Parse("H") == null &&
                Hotkey.Parse("Ctrl+") == null && Hotkey.Parse("Ctrl+Alt") == null && Hotkey.Parse("Ctrl+A+B") == null &&
                Hotkey.Parse("Ctrl+F99") == null && Hotkey.Parse("Ctrl+Enter") == null);
        }

        static string IsoNow(TimeSpan ago)
        {
            return (DateTime.UtcNow - ago).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        static string Esc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static void WriteSession(string sessionsDir, string fileName, int pid, string procStart, string id, string host,
            string status, string waitingFor, bool socket, string cwd, string name)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"pid\":").Append(pid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"sessionId\":\"").Append(id).Append("\"");
            sb.Append(",\"cwd\":\"").Append(Esc(cwd)).Append("\"");
            sb.Append(",\"procStart\":\"").Append(procStart).Append("\"");
            sb.Append(",\"kind\":\"interactive\",\"entrypoint\":\"claude-desktop\"");
            if (host != null) sb.Append(",\"hostSessionId\":\"").Append(host).Append("\"");
            if (socket) sb.Append(",\"messagingSocketPath\":\"\\\\\\\\.\\\\pipe\\\\LOCAL\\\\cc-msg-x\"");
            sb.Append(",\"name\":\"").Append(Esc(name)).Append("\"");
            sb.Append(",\"status\":\"").Append(status).Append("\"");
            if (waitingFor != null) sb.Append(",\"waitingFor\":\"").Append(waitingFor).Append("\"");
            sb.Append(",\"updatedAt\":1790854084986,\"statusUpdatedAt\":1790854084986}");
            File.WriteAllText(Path.Combine(sessionsDir, fileName), sb.ToString());
        }

        static string ApiError(string uuid, TimeSpan ago)
        {
            // Key order mirrors newer transcripts, where "message" (with its own "type") comes first.
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"message\":{\"model\":\"<synthetic>\",\"role\":\"assistant\",\"type\":\"message\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"API Error: 529 Overloaded.\"}]},\"type\":\"assistant\",\"uuid\":\"" + uuid + "\"," +
                "\"timestamp\":\"" + IsoNow(ago) + "\",\"error\":\"server_error\",\"isApiErrorMessage\":true,\"apiErrorStatus\":529}";
        }

        static string Assistant(string text)
        {
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"type\":\"assistant\",\"uuid\":\"u-ok\",\"timestamp\":\"" + IsoNow(TimeSpan.Zero) +
                "\",\"message\":{\"role\":\"assistant\",\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
        }

        static string User(string text)
        {
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"type\":\"user\",\"uuid\":\"u-user\",\"timestamp\":\"" + IsoNow(TimeSpan.Zero) +
                "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";
        }

        const string Trailer = "{\"type\":\"last-prompt\",\"lastPrompt\":\"hi\"}\n{\"type\":\"custom-title\",\"customTitle\":\"t\"}\n{\"type\":\"mode\",\"mode\":\"default\"}\n";

        static void WriteTranscript(string projectsDir, string cwd, string id, string body)
        {
            string dir = Path.Combine(projectsDir, SessionScanner.EncodeCwd(cwd));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), body, new UTF8Encoding(false));
        }

        static SessionInfo Find(List<SessionInfo> list, string id)
        {
            foreach (SessionInfo s in list) if (s.SessionId == id) return s;
            return null;
        }

        static void TestScanner(string root)
        {
            string sessionsDir = Path.Combine(root, "sessions");
            string projectsDir = Path.Combine(root, "projects");
            Directory.CreateDirectory(sessionsDir);
            Directory.CreateDirectory(projectsDir);

            int me;
            string myStart;
            using (Process self = Process.GetCurrentProcess())
            {
                me = self.Id;
                myStart = self.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            }
            const int DeadPid = 0x3FFFFFFC; // far beyond any real pid
            string cwd = "C:\\work\\demo project";

            SessionScanner empty = new SessionScanner(sessionsDir, projectsDir);
            Check("no sessions -> Sleep", SessionScanner.Aggregate(empty.Scan()) == BuddyState.Sleep);
            SessionScanner missing = new SessionScanner(Path.Combine(root, "nope"), projectsDir);
            Check("missing sessions dir -> Sleep", SessionScanner.Aggregate(missing.Scan()) == BuddyState.Sleep);

            WriteSession(sessionsDir, "1.json", me, myStart, "s-idle", "local_aaaa", "idle", null, true, cwd, "Idle one");
            WriteTranscript(projectsDir, cwd, "s-idle", User("hi") + "\n" + Assistant("done") + "\n" + Trailer);
            SessionScanner scanner = new SessionScanner(sessionsDir, projectsDir);
            List<SessionInfo> list = scanner.Scan();
            Check("idle session -> Idle", list.Count == 1 && SessionScanner.Aggregate(list) == BuddyState.Idle);

            WriteSession(sessionsDir, "2.json", me, myStart, "s-busy", "local_bbbb", "busy", null, true, cwd, "Busy one");
            list = scanner.Scan();
            Check("busy session -> Working", list.Count == 2 && SessionScanner.Aggregate(list) == BuddyState.Working);
            Check("most urgent sorted first", list.Count == 2 && list[0].SessionId == "s-busy");

            // Recent API error at the end of an idle session's transcript.
            WriteSession(sessionsDir, "3.json", me, myStart, "s-err", "local_cccc", "idle", null, true, cwd, "Errored");
            WriteTranscript(projectsDir, cwd, "s-err", User("go") + "\n" + ApiError("e1", TimeSpan.FromMinutes(2)) + "\n" + Trailer);
            list = scanner.Scan();
            SessionInfo err = Find(list, "s-err");
            Check("recent api error -> Error", err != null && err.State == BuddyState.Error && err.Error != null && err.Error.Kind == "server_error");
            Check("error outranks working", SessionScanner.Aggregate(list) == BuddyState.Error);
            Check("error text captured", err != null && err.Error != null && err.Error.Text.StartsWith("API Error", StringComparison.Ordinal));

            WriteSession(sessionsDir, "4.json", me, myStart, "s-wait", "local_dddd", "waiting", "permission prompt", true, cwd, "Needs you");
            list = scanner.Scan();
            Check("waiting outranks everything", SessionScanner.Aggregate(list) == BuddyState.Waiting && list[0].SessionId == "s-wait");
            Check("waiting text", list[0].StateText == "waiting: permission prompt");

            scanner.Acknowledge(list);
            list = scanner.Scan();
            err = Find(list, "s-err");
            Check("acknowledged error -> Idle", err != null && err.State == BuddyState.Idle);

            // A new error in the same session lights up again after an acknowledgement.
            WriteTranscript(projectsDir, cwd, "s-err", User("go") + "\n" + ApiError("e1", TimeSpan.FromMinutes(2)) + "\n" +
                User("again") + "\n" + ApiError("e2", TimeSpan.FromSeconds(5)) + "\n" + Trailer);
            list = scanner.Scan();
            err = Find(list, "s-err");
            Check("new error after ack -> Error", err != null && err.State == BuddyState.Error && err.Error.Uuid == "e2");

            // Old error (outside the window) and error followed by a normal reply.
            WriteSession(sessionsDir, "5.json", me, myStart, "s-old", "local_eeee", "idle", null, true, cwd, "Old error");
            WriteTranscript(projectsDir, cwd, "s-old", ApiError("e-old", TimeSpan.FromHours(3)) + "\n" + Trailer);
            WriteSession(sessionsDir, "6.json", me, myStart, "s-recovered", "local_ffff", "idle", null, true, cwd, "Recovered");
            WriteTranscript(projectsDir, cwd, "s-recovered", ApiError("e-r", TimeSpan.FromMinutes(1)) + "\n" + User("retry") + "\n" + Assistant("ok") + "\n" + Trailer);
            list = scanner.Scan();
            Check("stale error -> Idle", Find(list, "s-old") != null && Find(list, "s-old").State == BuddyState.Idle);
            Check("error then normal reply -> Idle", Find(list, "s-recovered") != null && Find(list, "s-recovered").State == BuddyState.Idle);

            // Transcript in a folder that does not match the cwd encoding is still found.
            WriteSession(sessionsDir, "7.json", me, myStart, "s-moved", "local_abab", "idle", null, true, "C:\\somewhere\\else", "Moved");
            WriteTranscript(projectsDir, "C:\\original\\place", "s-moved", ApiError("e-m", TimeSpan.FromMinutes(1)) + "\n");
            list = scanner.Scan();
            Check("transcript found by search", Find(list, "s-moved") != null && Find(list, "s-moved").State == BuddyState.Error);

            // Error hidden behind a long final line still inside the tail window.
            WriteSession(sessionsDir, "8.json", me, myStart, "s-big", "local_cdcd", "idle", null, true, cwd, "Big");
            WriteTranscript(projectsDir, cwd, "s-big", Assistant(new string('x', 400000)) + "\n" + User("go") + "\n" + ApiError("e-b", TimeSpan.FromMinutes(1)) + "\n" + Trailer);
            list = scanner.Scan();
            Check("error found in tail of large transcript", Find(list, "s-big") != null && Find(list, "s-big").State == BuddyState.Error);

            // Things that must be ignored.
            int before = list.Count;
            WriteSession(sessionsDir, "9.json", me, myStart, "s-helper", "local_bbbb", "busy", null, false, cwd, "scratch-1b");
            WriteSession(sessionsDir, "10.json", DeadPid, myStart, "s-dead", "local_dead", "busy", null, true, cwd, "Dead");
            WriteSession(sessionsDir, "11.json", me, "116444736000000000", "s-reused", "local_reus", "waiting", null, true, cwd, "Pid reused");
            File.WriteAllText(Path.Combine(sessionsDir, "12.json"), "{\"pid\": 12, \"sessionId\": \"trunc");
            File.WriteAllText(Path.Combine(sessionsDir, "13.json"), "[1,2,3]");
            File.WriteAllText(Path.Combine(sessionsDir, "14.json"), "");
            File.WriteAllText(Path.Combine(sessionsDir, "abc.key"), "secret");
            list = scanner.Scan();
            Check("helper fork ignored", Find(list, "s-helper") == null);
            Check("dead pid ignored", Find(list, "s-dead") == null);
            Check("reused pid (procStart mismatch) ignored", Find(list, "s-reused") == null);
            Check("malformed files ignored", list.Count == before);

            // A helper-shaped entry whose parent is absent is kept (it is the only record of that session).
            WriteSession(sessionsDir, "15.json", me, myStart, "s-solo", "local_solo", "busy", null, false, cwd, "Solo");
            // A terminal session has no hostSessionId at all.
            WriteSession(sessionsDir, "16.json", me, myStart, "s-cli", null, "busy", null, false, cwd, "");
            list = scanner.Scan();
            Check("socketless session without sibling kept", Find(list, "s-solo") != null);
            Check("terminal session kept", Find(list, "s-cli") != null && Find(list, "s-cli").DisplayName == "demo project");

            // Status changes are picked up on rescan; removed files disappear.
            WriteSession(sessionsDir, "4.json", me, myStart, "s-wait", "local_dddd", "busy", null, true, cwd, "Needs you");
            File.SetLastWriteTimeUtc(Path.Combine(sessionsDir, "4.json"), DateTime.UtcNow.AddSeconds(3));
            list = scanner.Scan();
            Check("status change picked up", Find(list, "s-wait") != null && Find(list, "s-wait").State == BuddyState.Working);
            File.Delete(Path.Combine(sessionsDir, "4.json"));
            list = scanner.Scan();
            Check("removed session disappears", Find(list, "s-wait") == null);

            Check("process match: self", SessionScanner.ProcessMatches(me, myStart));
            Check("process match: dead pid", !SessionScanner.ProcessMatches(DeadPid, myStart));
            Check("process match: no procStart", SessionScanner.ProcessMatches(me, ""));
        }

        static string LimitError(string uuid, TimeSpan ago, string text)
        {
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + IsoNow(ago) +
                "\",\"message\":{\"model\":\"<synthetic>\",\"role\":\"assistant\",\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"" + Esc(text) +
                "\"}]},\"error\":\"rate_limit\",\"isApiErrorMessage\":true,\"apiErrorStatus\":429}";
        }

        static string UserAt(string text, TimeSpan ago)
        {
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"type\":\"user\",\"uuid\":\"u-user-at\",\"timestamp\":\"" + IsoNow(ago) +
                "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";
        }

        static string AssistantAt(string text, TimeSpan ago)
        {
            return "{\"parentUuid\":\"p\",\"isSidechain\":false,\"type\":\"assistant\",\"uuid\":\"u-at\",\"timestamp\":\"" + IsoNow(ago) +
                "\",\"message\":{\"role\":\"assistant\",\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
        }

        static void TestLimit(string root)
        {
            // Reset-time parsing. Clock times in the message are local.
            DateTime at1am = new DateTime(2026, 10, 2, 1, 0, 0, DateTimeKind.Local).ToUniversalTime();
            DateTime at4am = new DateTime(2026, 10, 2, 4, 0, 0, DateTimeKind.Local).ToUniversalTime();
            DateTime at11pm = new DateTime(2026, 10, 2, 23, 0, 0, DateTimeKind.Local).ToUniversalTime();
            const string Session = "You've hit your session limit \u00b7 resets ";
            Check("limit: reset later the same day",
                LimitMessage.ParseReset(Session + "3:40am (Asia/Calcutta)", at1am).ToLocalTime() == new DateTime(2026, 10, 2, 3, 40, 0));
            Check("limit: reset time already passed -> next day",
                LimitMessage.ParseReset(Session + "3:40am (Asia/Calcutta)", at4am).ToLocalTime() == new DateTime(2026, 10, 3, 3, 40, 0));
            Check("limit: pm", LimitMessage.ParseReset(Session + "10:20pm (Asia/Kolkata)", at1am).ToLocalTime() == new DateTime(2026, 10, 2, 22, 20, 0));
            Check("limit: no minutes", LimitMessage.ParseReset(Session + "3pm", at1am).ToLocalTime() == new DateTime(2026, 10, 2, 15, 0, 0));
            Check("limit: 12am is midnight", LimitMessage.ParseReset(Session + "12:05am", at11pm).ToLocalTime() == new DateTime(2026, 10, 3, 0, 5, 0));
            Check("limit: 12pm is noon", LimitMessage.ParseReset(Session + "12:30pm", at1am).ToLocalTime() == new DateTime(2026, 10, 2, 12, 30, 0));
            Check("limit: explicit date", LimitMessage.ParseReset("You've hit your weekly limit \u00b7 resets Oct 6, 9am (Asia/Calcutta)", at1am).ToLocalTime() == new DateTime(2026, 10, 6, 9, 0, 0) &&
                LimitMessage.ParseReset("resets Oct 6 at 9:15am", at1am).ToLocalTime() == new DateTime(2026, 10, 6, 9, 15, 0));
            Check("limit: messages without a reset time",
                LimitMessage.ParseReset("You're out of usage credits. Switch to another model, or manage usage credits at claude.ai, to continue.", at1am) == DateTime.MinValue &&
                LimitMessage.ParseReset("You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.", at1am) == DateTime.MinValue &&
                LimitMessage.ParseReset("", at1am) == DateTime.MinValue && LimitMessage.ParseReset("resets 25:99am", at1am) == DateTime.MinValue &&
                LimitMessage.ParseReset("resets Feb 30, 9am", at1am) == DateTime.MinValue && LimitMessage.ParseReset("resets Dec 25, 9am", at1am) == DateTime.MinValue);

            // Countdown text.
            Check("countdown formats",
                Sprite.FormatCountdown(new TimeSpan(2, 34, 0)) == "2:34" && Sprite.FormatCountdown(new TimeSpan(2, 33, 1)) == "2:34" &&
                Sprite.FormatCountdown(new TimeSpan(0, 59, 30)) == "1:00" && Sprite.FormatCountdown(new TimeSpan(0, 45, 0)) == "45m" &&
                Sprite.FormatCountdown(TimeSpan.FromSeconds(60)) == "1m" && Sprite.FormatCountdown(TimeSpan.FromSeconds(30)) == "30s" &&
                Sprite.FormatCountdown(TimeSpan.FromSeconds(0.2)) == "1s" && Sprite.FormatCountdown(new TimeSpan(12, 5, 0)) == "12h" &&
                Sprite.FormatCountdown(new TimeSpan(26, 0, 0)) == "2d" && Sprite.FormatCountdown(new TimeSpan(9, 59, 0)) == "9:59");
            bool fits = true;
            foreach (string sample in new string[] { "9:59", "1:00", "59m", "59s", "23h", "8d", "1s" })
            {
                int w = Sprite.TextWidth(sample);
                if (w <= 0 || w > 16) fits = false;
            }
            Check("countdown text fits the head", fits && Sprite.TextWidth("x") == 0);
            Check("duration in words", Sprite.FormatDuration(new TimeSpan(2, 34, 0)) == "2h 34m" && Sprite.FormatDuration(TimeSpan.FromMinutes(12)) == "12m" &&
                Sprite.FormatDuration(TimeSpan.FromSeconds(20)) == "under a minute");

            // Scanner: a limit error stays red past the 30-minute window, until its reset time.
            string sessionsDir = Path.Combine(root, "limit-sessions");
            string projectsDir = Path.Combine(root, "limit-projects");
            Directory.CreateDirectory(sessionsDir);
            Directory.CreateDirectory(projectsDir);
            int me;
            string myStart;
            using (Process self = Process.GetCurrentProcess())
            {
                me = self.Id;
                myStart = self.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            }
            string cwd = "C:\\work\\limit";
            DateTime resetLocal = DateTime.Now.AddHours(1);
            resetLocal = new DateTime(resetLocal.Year, resetLocal.Month, resetLocal.Day, resetLocal.Hour, resetLocal.Minute, 0, DateTimeKind.Local);
            string clock = resetLocal.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();

            WriteSession(sessionsDir, "1.json", me, myStart, "s-limit", "local_1111", "idle", null, true, cwd, "Limited");
            WriteTranscript(projectsDir, cwd, "s-limit", User("go") + "\n" + LimitError("lim1", TimeSpan.FromHours(2), Session + clock + " (Asia/Calcutta)") + "\n" + Trailer);
            WriteSession(sessionsDir, "2.json", me, myStart, "s-other", "local_2222", "idle", null, true, cwd, "Other");
            WriteTranscript(projectsDir, cwd, "s-other", User("old") + "\n" + AssistantAt("old reply", TimeSpan.FromHours(5)) + "\n" + Trailer);

            SessionScanner scanner = new SessionScanner(sessionsDir, projectsDir);
            List<SessionInfo> list = scanner.Scan();
            SessionInfo limited = Find(list, "s-limit");
            Check("limit: still an error two hours later", limited != null && limited.State == BuddyState.Error);
            Check("limit: reset time reported", Math.Abs((scanner.LimitResetUtc - resetLocal.ToUniversalTime()).TotalSeconds) < 1);
            Check("limit: session text names the reset", limited != null && limited.StateText.StartsWith("usage limit, resets ", StringComparison.Ordinal));

            // A local slash command or task notification after the limit message is not a reply.
            WriteTranscript(projectsDir, cwd, "s-limit", User("go") + "\n" + LimitError("lim1", TimeSpan.FromHours(2), Session + clock + " (Asia/Calcutta)") + "\n" +
                User("<command-name>/model</command-name>") + "\n" + Trailer);
            list = scanner.Scan();
            Check("limit: survives a trailing user record", Find(list, "s-limit").State == BuddyState.Error && scanner.LimitResetUtc > DateTime.UtcNow);

            // A reply whose request went out before the limit hit, and finished just after it, proves nothing.
            WriteTranscript(projectsDir, cwd, "s-other", UserAt("earlier", TimeSpan.FromHours(3)) + "\n" +
                AssistantAt("was already streaming", TimeSpan.FromHours(2) - TimeSpan.FromSeconds(3)) + "\n" + Trailer);
            list = scanner.Scan();
            Check("limit: an in-flight reply does not lift it", Find(list, "s-limit").State == BuddyState.Error && scanner.LimitResetUtc > DateTime.UtcNow);

            // An ordinary API error also survives a trailing user record.
            WriteSession(sessionsDir, "3.json", me, myStart, "s-apierr", "local_3333", "idle", null, true, cwd, "Api error");
            WriteTranscript(projectsDir, cwd, "s-apierr", User("go") + "\n" + ApiError("e-u", TimeSpan.FromMinutes(1)) + "\n" + User("<task-notification>done</task-notification>") + "\n" + Trailer);
            list = scanner.Scan();
            Check("error: survives a trailing user record", Find(list, "s-apierr") != null && Find(list, "s-apierr").State == BuddyState.Error);
            File.Delete(Path.Combine(sessionsDir, "3.json"));

            scanner.Acknowledge(list);
            list = scanner.Scan();
            Check("limit: acknowledged -> lamp off, countdown stays",
                Find(list, "s-limit").State == BuddyState.Idle && scanner.LimitResetUtc > DateTime.UtcNow);

            // The limit is remembered even when its message disappears from the transcript
            // (the user rewinds or deletes it).
            string limitTranscript = User("go") + "\n" + LimitError("lim2", TimeSpan.FromHours(2), Session + clock + " (Asia/Calcutta)") + "\n" + Trailer;
            WriteTranscript(projectsDir, cwd, "s-limit", UserAt("old", TimeSpan.FromHours(6)) + "\n" + AssistantAt("old reply", TimeSpan.FromHours(6)) + "\n" + Trailer);
            list = scanner.Scan();
            Check("limit: remembered after its message is deleted", Find(list, "s-limit").State == BuddyState.Idle && scanner.LimitResetUtc > DateTime.UtcNow);

            // ...and across a restart of the buddy.
            SessionScanner restarted = new SessionScanner(sessionsDir, projectsDir);
            restarted.RestoreLimit(scanner.LimitResetUtc, scanner.LimitSeenUtc);
            restarted.Scan();
            Check("limit: restored after a restart", Math.Abs((restarted.LimitResetUtc - scanner.LimitResetUtc).TotalSeconds) < 1);
            SessionScanner expired = new SessionScanner(sessionsDir, projectsDir);
            expired.RestoreLimit(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(-3));
            expired.Scan();
            Check("limit: an expired one is dropped on restore", expired.LimitResetUtc == DateTime.MinValue);
            SessionScanner absurd = new SessionScanner(sessionsDir, projectsDir);
            absurd.RestoreLimit(DateTime.UtcNow.AddDays(400), DateTime.UtcNow);
            Check("limit: an absurd restored value is ignored", absurd.LimitResetUtc == DateTime.MinValue);

            // A session parked until the limit resets (auto-resume) reports "busy". It is not working.
            WriteSession(sessionsDir, "1.json", me, myStart, "s-limit", "local_1111", "busy", null, true, cwd, "Limited");
            File.SetLastWriteTimeUtc(Path.Combine(sessionsDir, "1.json"), DateTime.UtcNow.AddSeconds(5));
            WriteTranscript(projectsDir, cwd, "s-limit", limitTranscript);
            list = scanner.Scan();
            Check("limit: busy but parked on the limit is not Working",
                Find(list, "s-limit").State == BuddyState.Error && SessionScanner.Aggregate(list) == BuddyState.Error && scanner.LimitResetUtc > DateTime.UtcNow);
            WriteTranscript(projectsDir, cwd, "s-limit", limitTranscript.Replace(Trailer, "") + User("try again") + "\n" + Trailer);
            list = scanner.Scan();
            Check("limit: busy with a new prompt after the limit is Working", Find(list, "s-limit").State == BuddyState.Working);
            WriteSession(sessionsDir, "1.json", me, myStart, "s-limit", "local_1111", "idle", null, true, cwd, "Limited");
            File.SetLastWriteTimeUtc(Path.Combine(sessionsDir, "1.json"), DateTime.UtcNow.AddSeconds(10));
            WriteTranscript(projectsDir, cwd, "s-limit", limitTranscript);
            list = scanner.Scan();
            Check("limit: back to idle on the limit", Find(list, "s-limit").State == BuddyState.Error);

            // A newer ordinary reply anywhere proves the limit was lifted.
            WriteTranscript(projectsDir, cwd, "s-other", User("again") + "\n" + Assistant("works") + "\n" + Trailer);
            SessionScanner fresh = new SessionScanner(sessionsDir, projectsDir);
            list = fresh.Scan();
            Check("limit: lifted by a newer reply", fresh.LimitResetUtc == DateTime.MinValue && Find(list, "s-limit").State == BuddyState.Idle);
            scanner.Scan();
            Check("limit: a remembered limit is cleared by a newer reply too", scanner.LimitResetUtc == DateTime.MinValue);

            // Forehead rendering.
            using (Bitmap bmp = new Bitmap(Sprite.CanvasW * 3, Sprite.CanvasH * 3, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Pose pose = Sprite.GetPose(BuddyState.Error, 1);
                Sprite.ApplyTimer(pose, "2:34");
                Sprite.Render(g, pose, 3);
                int ink = 0;
                for (int y = (Sprite.BotY + 1) * 3; y < (Sprite.BotY + 6) * 3; y++)
                {
                    for (int x = (Sprite.BotX + 4) * 3; x < (Sprite.BotX + 20) * 3; x++)
                    {
                        if (bmp.GetPixel(x, y).ToArgb() == Sprite.Eye.ToArgb()) ink++;
                    }
                }
                Check("timer: digits drawn on the forehead, bot holds still", ink > 150 && pose.Dx == 0 && pose.EyeDy == 5);
            }
        }

        static void TestSprite()
        {
            BuddyState[] states = new BuddyState[]
            {
                BuddyState.Sleep, BuddyState.Idle, BuddyState.Working, BuddyState.Waiting, BuddyState.Error, BuddyState.Done
            };
            bool posesOk = true;
            using (Bitmap bmp = new Bitmap(Sprite.CanvasW * 3, Sprite.CanvasH * 3, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                foreach (BuddyState s in states)
                {
                    for (int t = 0; t < 600; t++)
                    {
                        Pose p = Sprite.GetPose(s, t);
                        if (p.Glow < 0 || p.Glow > 8) posesOk = false;
                        if (Sprite.BotY + p.Dy - 6 < 0) posesOk = false;                 // lamp stays on canvas
                        for (int i = 0; i < 4; i++)
                        {
                            if (p.Legs[i] < 1) posesOk = false;
                            if (p.Dy + Sprite.BodyH + p.Legs[i] > Sprite.BotH) posesOk = false; // feet never below ground
                        }
                        if (t < 40) Sprite.Render(g, p, 3);
                    }
                }
                Sprite.Render(g, Sprite.GetPose(BuddyState.Idle, 3), 3);
                Color body = bmp.GetPixel((Sprite.BotX + 12) * 3 + 1, (Sprite.BotY + 7) * 3 + 1);
                Color corner = bmp.GetPixel(0, 0);
                Check("sprite: body pixel is Claude orange", body.ToArgb() == Sprite.Body.ToArgb());
                Check("sprite: canvas corner transparent", corner.A == 0);
            }
            Check("sprite: all poses within bounds", posesOk);
            bool sawLaptop = false, sawRun = false, sawDots = false;
            for (int t = 0; t < Sprite.WorkActTicks * Sprite.WorkActs; t++)
            {
                Pose w = Sprite.GetPose(BuddyState.Working, t);
                if (w.Prop == Prop.Laptop) sawLaptop = true;
                else if (w.Prop == Prop.ThoughtDots) sawDots = true;
                else if (w.Legs[0] != w.Legs[1]) sawRun = true;
            }
            Check("sprite: working rotates laptop, running, thinking", sawLaptop && sawRun && sawDots);
            Check("sprite: working rotation repeats", Sprite.GetPose(BuddyState.Working, 5).Key() ==
                Sprite.GetPose(BuddyState.Working, 5 + 60 * Sprite.WorkActTicks * Sprite.WorkActs).Key());
            Check("sprite: lamp colors", Sprite.LedColor(BuddyState.Working) == Sprite.LedGreen && Sprite.LedColor(BuddyState.Waiting) == Sprite.LedYellow &&
                Sprite.LedColor(BuddyState.Error) == Sprite.LedRed);
            foreach (int size in new int[] { 16, 20, 24, 32, 48 })
            {
                using (Bitmap icon = Sprite.MakeIconBitmap(size, BuddyState.Waiting, true))
                {
                    Check("icon " + size + " px", icon.Width == size && icon.Height == size);
                }
            }
        }
    }
}
