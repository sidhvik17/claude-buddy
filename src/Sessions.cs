using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // Ordered by display priority (higher wins when sessions disagree), except Done,
    // which is a short celebration the window plays after Working -> Idle.
    internal enum BuddyState
    {
        Sleep = 0,
        Idle = 1,
        Working = 2,
        Error = 3,
        Waiting = 4,
        Done = 5
    }

    internal sealed class ErrorInfo
    {
        public string Uuid = "";
        public string Kind = "";
        public string Text = "";
        public DateTime WhenUtc = DateTime.MinValue;
        public DateTime ResetUtc = DateTime.MinValue;   // usage-limit errors only: when the limit lifts
    }

    // The last assistant record of a transcript.
    internal sealed class TurnInfo
    {
        public DateTime WhenUtc = DateTime.MinValue;
        public DateTime RequestUtc = DateTime.MinValue;   // for a reply: when the request it answers was sent
        public bool Replied;      // an ordinary assistant reply: proof that requests were going through
        public bool PromptAfter;  // a user record follows it: a new prompt is being worked on
        public ErrorInfo Error;   // set when the turn ended in an API error
    }

    internal sealed class SessionInfo
    {
        public int Pid;
        public string SessionId = "";
        public string HostSessionId = "";
        public string Name = "";
        public string Cwd = "";
        public string Status = "";
        public string WaitingFor = "";
        public string Kind = "";
        public string ProcStart = "";
        public bool HasSocket;
        public long StatusUpdatedAt;
        public BuddyState State = BuddyState.Idle;
        public ErrorInfo Error;

        public SessionInfo Clone()
        {
            return (SessionInfo)MemberwiseClone();
        }

        public string DisplayName
        {
            get
            {
                if (Name.Length > 0) return Name;
                if (Cwd.Length > 0)
                {
                    string leaf = Path.GetFileName(Cwd.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(leaf)) return leaf;
                }
                return "session " + Pid.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case BuddyState.Waiting:
                        return WaitingFor.Length > 0 ? "waiting: " + WaitingFor : "waiting for you";
                    case BuddyState.Working:
                        return "working";
                    case BuddyState.Error:
                        if (Error != null && Error.ResetUtc > DateTime.UtcNow)
                        {
                            return "usage limit, resets " + Error.ResetUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
                        }
                        return Error != null && Error.Kind.Length > 0 ? "error: " + Error.Kind.Replace('_', ' ') : "error";
                    default:
                        return "idle";
                }
            }
        }
    }

    // Reads Claude Code's per-session state files (~/.claude/sessions/<pid>.json).
    internal sealed class SessionScanner
    {
        public static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(30);
        static readonly TimeSpan LivenessTtl = TimeSpan.FromSeconds(5);
        static readonly TimeSpan ResolveRetry = TimeSpan.FromSeconds(30);
        const long ProcStartToleranceTicks = 100000000L; // 10 s in FILETIME units
        const long MaxStateFileBytes = 1024 * 1024;
        const int MaxAcked = 512;

        sealed class FileEntry
        {
            public DateTime Mtime;
            public long Length;
            public SessionInfo Info;
        }

        sealed class LiveEntry
        {
            public DateTime At;
            public string ProcStart;
            public bool Alive;
        }

        sealed class ProbeEntry
        {
            public string Path;
            public DateTime LastResolve = DateTime.MinValue;
            public DateTime Mtime;
            public long Length = -1;
            public TurnInfo Turn;
        }

        readonly string sessionsDir;
        readonly string projectsDir;
        readonly Dictionary<string, FileEntry> files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, LiveEntry> live = new Dictionary<int, LiveEntry>();
        readonly Dictionary<string, ProbeEntry> probes = new Dictionary<string, ProbeEntry>(StringComparer.Ordinal);
        readonly HashSet<string> acked = new HashSet<string>(StringComparer.Ordinal);

        public bool CheckLiveness = true;
        public string LastScanError;
        public int LastFileCount;

        // When the usage limit that is currently blocking Claude lifts; MinValue when none is in force.
        // It is remembered from the moment a session reports it until the reset time, even if
        // the message is later deleted from the conversation or the session is closed.
        public DateTime LimitResetUtc = DateTime.MinValue;
        public DateTime LimitSeenUtc = DateTime.MinValue;   // when that limit message was written

        static readonly TimeSpan MaxLimitAhead = TimeSpan.FromDays(8);

        // Puts back a limit remembered by an earlier run. Scan drops it once it has passed.
        public void RestoreLimit(DateTime resetUtc, DateTime seenUtc)
        {
            if (resetUtc - DateTime.UtcNow > MaxLimitAhead) return; // not something Claude would say
            LimitResetUtc = resetUtc;
            LimitSeenUtc = seenUtc;
        }

        public SessionScanner(string sessionsDir, string projectsDir)
        {
            this.sessionsDir = sessionsDir;
            this.projectsDir = projectsDir;
        }

        public string SessionsDir { get { return sessionsDir; } }

        public static string DefaultClaudeDir()
        {
            string custom = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        public List<SessionInfo> Scan()
        {
            DateTime now = DateTime.UtcNow;
            List<SessionInfo> found = new List<SessionInfo>();
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> seenPids = new HashSet<int>();

            string[] paths;
            LastScanError = null;
            try
            {
                paths = Directory.Exists(sessionsDir) ? Directory.GetFiles(sessionsDir, "*.json") : new string[0];
            }
            catch (Exception ex)
            {
                paths = new string[0];
                LastScanError = ex.GetType().Name + ": " + ex.Message;
            }

            LastFileCount = paths.Length;
            foreach (string path in paths)
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                seenFiles.Add(path);
                SessionInfo cached = Load(path);
                if (cached == null) continue;
                seenPids.Add(cached.Pid);
                if (cached.Kind == "daemon" || cached.Kind == "daemon-worker") continue;
                if (CheckLiveness && !IsAlive(cached.Pid, cached.ProcStart, now)) continue;
                found.Add(cached.Clone());
            }

            Prune(files, seenFiles);
            Prune(live, seenPids);

            List<SessionInfo> result = DropHelperForks(found);
            HashSet<string> seenSessions = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<SessionInfo, TurnInfo> turns = new Dictionary<SessionInfo, TurnInfo>();
            DateTime lastReplyUtc = DateTime.MinValue;
            foreach (SessionInfo s in result)
            {
                seenSessions.Add(s.SessionId);
                s.State = MapStatus(s.Status);
                if (s.State == BuddyState.Waiting) continue;
                TurnInfo turn = Probe(s, now);
                if (turn == null) continue;
                if (s.State == BuddyState.Working)
                {
                    // "busy" is also what a session reports while it is parked until a usage
                    // limit resets (auto-resume). That is not work: its last record is the limit
                    // message and no new prompt has followed it.
                    bool parked = turn.Error != null && turn.Error.ResetUtc > now && !turn.PromptAfter;
                    if (!parked) continue;
                    s.State = BuddyState.Idle;
                }
                turns[s] = turn;
                // Judged by when the request went out: a reply that was already streaming when
                // the limit hit another session finishes after the limit message but proves nothing.
                if (turn.Replied && turn.RequestUtc > lastReplyUtc) lastReplyUtc = turn.RequestUtc;
            }
            Prune(probes, seenSessions);

            // A usage limit applies to the whole account. It stays in force until its reset
            // time, unless some session got an ordinary reply after it (then it was lifted).
            ErrorInfo limit = null;
            foreach (KeyValuePair<SessionInfo, TurnInfo> pair in turns)
            {
                ErrorInfo e = pair.Value.Error;
                if (e == null) continue;
                bool limitInForce = e.ResetUtc > now && e.WhenUtc > lastReplyUtc;
                if (limitInForce && (limit == null || e.WhenUtc > limit.WhenUtc)) limit = e;
                if ((limitInForce || now - e.WhenUtc <= ErrorWindow) && !acked.Contains(AckKey(pair.Key, e)))
                {
                    pair.Key.Error = e;
                    pair.Key.State = BuddyState.Error;
                }
            }
            if (limit != null && limit.WhenUtc >= LimitSeenUtc)
            {
                LimitSeenUtc = limit.WhenUtc;
                LimitResetUtc = limit.ResetUtc;
            }
            // Forget it once it has passed, or once a request sent after it got an ordinary reply.
            if (LimitResetUtc <= now || lastReplyUtc > LimitSeenUtc)
            {
                LimitResetUtc = DateTime.MinValue;
                LimitSeenUtc = DateTime.MinValue;
            }

            result.Sort(CompareSessions);
            return result;
        }

        public static BuddyState Aggregate(List<SessionInfo> sessions)
        {
            BuddyState best = BuddyState.Sleep;
            foreach (SessionInfo s in sessions)
            {
                if ((int)s.State > (int)best) best = s.State;
            }
            return best;
        }

        // Clicking the buddy counts as "seen": current errors stop lighting the red lamp.
        public void Acknowledge(List<SessionInfo> sessions)
        {
            foreach (SessionInfo s in sessions)
            {
                if (s.Error == null) continue;
                if (acked.Count >= MaxAcked) acked.Clear();
                acked.Add(AckKey(s, s.Error));
            }
        }

        public static BuddyState MapStatus(string status)
        {
            if (status == "waiting") return BuddyState.Waiting;
            if (status == "busy" || status == "shell") return BuddyState.Working;
            return BuddyState.Idle;
        }

        // Claude Code names a project's transcript folder after the cwd with every
        // non-alphanumeric character replaced by '-'.
        public static string EncodeCwd(string cwd)
        {
            StringBuilder sb = new StringBuilder(cwd.Length);
            foreach (char c in cwd)
            {
                bool alnum = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                sb.Append(alnum ? c : '-');
            }
            return sb.ToString();
        }

        public static bool IsSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        static string AckKey(SessionInfo s, ErrorInfo e)
        {
            return s.SessionId + "|" + e.Uuid + "|" + e.WhenUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        static int CompareSessions(SessionInfo a, SessionInfo b)
        {
            int c = ((int)b.State).CompareTo((int)a.State);
            if (c != 0) return c;
            c = b.StatusUpdatedAt.CompareTo(a.StatusUpdatedAt);
            if (c != 0) return c;
            return string.CompareOrdinal(a.SessionId, b.SessionId);
        }

        static void Prune<TKey, TValue>(Dictionary<TKey, TValue> map, HashSet<TKey> keep)
        {
            if (map.Count == 0) return;
            List<TKey> drop = null;
            foreach (TKey key in map.Keys)
            {
                if (keep.Contains(key)) continue;
                if (drop == null) drop = new List<TKey>();
                drop.Add(key);
            }
            if (drop == null) return;
            foreach (TKey key in drop) map.Remove(key);
        }

        // The desktop app forks short-lived helper processes off a session (same
        // hostSessionId, no messaging socket). They are not sessions the user owns.
        static List<SessionInfo> DropHelperForks(List<SessionInfo> all)
        {
            HashSet<string> hostsWithSocket = new HashSet<string>(StringComparer.Ordinal);
            foreach (SessionInfo s in all)
            {
                if (s.HostSessionId.Length > 0 && s.HasSocket) hostsWithSocket.Add(s.HostSessionId);
            }
            List<SessionInfo> kept = new List<SessionInfo>(all.Count);
            foreach (SessionInfo s in all)
            {
                bool helper = s.HostSessionId.Length > 0 && !s.HasSocket && hostsWithSocket.Contains(s.HostSessionId);
                if (!helper) kept.Add(s);
            }
            return kept;
        }

        SessionInfo Load(string path)
        {
            FileEntry entry;
            files.TryGetValue(path, out entry);
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (entry != null && entry.Mtime == fi.LastWriteTimeUtc && entry.Length == fi.Length) return entry.Info;
                if (fi.Length > MaxStateFileBytes) return null;

                string text = ReadShared(path);
                Dictionary<string, object> d = MiniJson.ParseObject(text);
                if (d == null) throw new FormatException("not an object");

                SessionInfo s = new SessionInfo();
                s.Pid = (int)MiniJson.GetLong(d, "pid");
                s.SessionId = MiniJson.GetString(d, "sessionId");
                s.HostSessionId = MiniJson.GetString(d, "hostSessionId");
                s.Name = MiniJson.GetString(d, "name");
                s.Cwd = MiniJson.GetString(d, "cwd");
                s.Status = MiniJson.GetString(d, "status");
                s.WaitingFor = MiniJson.GetString(d, "waitingFor");
                s.Kind = MiniJson.GetString(d, "kind");
                s.ProcStart = MiniJson.GetString(d, "procStart");
                s.HasSocket = MiniJson.GetString(d, "messagingSocketPath").Length > 0;
                s.StatusUpdatedAt = MiniJson.GetLong(d, "statusUpdatedAt");
                if (s.StatusUpdatedAt == 0) s.StatusUpdatedAt = MiniJson.GetLong(d, "updatedAt");
                if (s.Pid <= 0 || s.SessionId.Length == 0) throw new FormatException("missing pid/sessionId");

                if (entry == null)
                {
                    entry = new FileEntry();
                    files[path] = entry;
                }
                entry.Mtime = fi.LastWriteTimeUtc;
                entry.Length = fi.Length;
                entry.Info = s;
                return s;
            }
            catch (Exception)
            {
                // Caught mid-write or not a session file: keep the last good reading, if any.
                return entry != null ? entry.Info : null;
            }
        }

        static string ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        bool IsAlive(int pid, string procStart, DateTime now)
        {
            LiveEntry entry;
            if (live.TryGetValue(pid, out entry) && entry.ProcStart == procStart && now - entry.At < LivenessTtl) return entry.Alive;
            if (entry == null)
            {
                entry = new LiveEntry();
                live[pid] = entry;
            }
            entry.At = now;
            entry.ProcStart = procStart;
            entry.Alive = ProcessMatches(pid, procStart);
            return entry.Alive;
        }

        // A pid alone is not proof: pids get reused. procStart is the process creation
        // time as a FILETIME, so a mismatch means the file is a leftover.
        public static bool ProcessMatches(int pid, string procStart)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    long want;
                    if (long.TryParse(procStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out want) && want > 0)
                    {
                        long got;
                        try
                        {
                            got = p.StartTime.ToFileTimeUtc();
                        }
                        catch (Exception)
                        {
                            // Start time unreadable. A system process that reused the pid looks
                            // like this; so does Claude started from an elevated terminal.
                            return LooksLikeClaude(p);
                        }
                        if (Math.Abs(got - want) > ProcStartToleranceTicks) return false;
                    }
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch (InvalidOperationException)
            {
                return false; // exited while we looked
            }
            catch (Exception)
            {
                return true;
            }
        }

        static bool LooksLikeClaude(Process p)
        {
            try
            {
                string name = p.ProcessName;
                return string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "node", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        TurnInfo Probe(SessionInfo s, DateTime now)
        {
            if (!IsSafeId(s.SessionId)) return null;
            ProbeEntry pe;
            if (!probes.TryGetValue(s.SessionId, out pe))
            {
                pe = new ProbeEntry();
                probes[s.SessionId] = pe;
            }
            try
            {
                if (pe.Path == null)
                {
                    if (now - pe.LastResolve < ResolveRetry) return null;
                    pe.LastResolve = now;
                    pe.Path = ResolveTranscript(s);
                    if (pe.Path == null) return null;
                }
                FileInfo fi = new FileInfo(pe.Path);
                if (!fi.Exists)
                {
                    pe.Path = null;
                    pe.Turn = null;
                    pe.Length = -1;
                    return null;
                }
                if (fi.Length != pe.Length || fi.LastWriteTimeUtc != pe.Mtime)
                {
                    pe.Turn = Transcript.ReadLastTurn(pe.Path);
                    pe.Length = fi.Length;      // only after a successful read, so a failed one is retried
                    pe.Mtime = fi.LastWriteTimeUtc;
                }
                return pe.Turn;
            }
            catch (Exception)
            {
                return null;
            }
        }

        string ResolveTranscript(SessionInfo s)
        {
            if (!Directory.Exists(projectsDir)) return null;
            string file = s.SessionId + ".jsonl";
            if (s.Cwd.Length > 0)
            {
                string direct = Path.Combine(Path.Combine(projectsDir, EncodeCwd(s.Cwd)), file);
                if (File.Exists(direct)) return direct;
            }
            foreach (string dir in Directory.GetDirectories(projectsDir))
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    internal static class Transcript
    {
        const int TailBytes = 256 * 1024;

        // Returns the API error that ended the session's last turn, or null when the last
        // user/assistant record in the transcript tail is an ordinary message.
        public static ErrorInfo ReadLastError(string path)
        {
            TurnInfo turn = ReadLastTurn(path);
            return turn != null ? turn.Error : null;
        }

        // Describes the last assistant record in the transcript tail; null if there is none.
        public static TurnInfo ReadLastTurn(string path)
        {
            string text;
            bool truncated;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - TailBytes);
                truncated = start > 0;
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[(int)(fs.Length - start)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                text = Encoding.UTF8.GetString(buffer, 0, read);
            }

            string[] lines = text.Split('\n');
            int first = truncated ? 1 : 0; // the first line of a tail is usually cut in half
            TurnInfo reply = null;
            bool promptAfter = false;
            for (int i = lines.Length - 1; i >= first; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                // Cheap pre-filter; key order varies between versions, so confirm by parsing.
                if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0 &&
                    line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) < 0) continue;

                Dictionary<string, object> d;
                try
                {
                    d = MiniJson.ParseObject(line);
                }
                catch (Exception)
                {
                    continue;
                }
                if (d == null) continue;
                if (MiniJson.GetBool(d, "isSidechain")) continue;

                string type = MiniJson.GetString(d, "type");
                if (type != "user" && type != "assistant") continue;

                DateTime when;
                if (!DateTime.TryParse(MiniJson.GetString(d, "timestamp"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                {
                    when = File.GetLastWriteTimeUtc(path);
                }

                if (reply != null)
                {
                    // Found the reply already; now looking for the prompt or tool result it answered.
                    if (type != "user") continue;
                    reply.RequestUtc = when < reply.WhenUtc ? when : reply.WhenUtc;
                    return reply;
                }
                // Slash commands, task notifications and interrupted prompts are user records
                // with no model turn behind them: keep looking for the last assistant record.
                if (type == "user")
                {
                    if (!MiniJson.GetBool(d, "isMeta")) promptAfter = true;
                    continue;
                }

                TurnInfo turn = new TurnInfo();
                turn.WhenUtc = when;
                turn.PromptAfter = promptAfter;
                if (!MiniJson.GetBool(d, "isApiErrorMessage"))
                {
                    turn.Replied = true;
                    turn.RequestUtc = when;
                    reply = turn;
                    continue;
                }

                ErrorInfo e = new ErrorInfo();
                e.Uuid = MiniJson.GetString(d, "uuid");
                e.Kind = MiniJson.GetString(d, "error");
                e.Text = FirstText(d);
                e.WhenUtc = turn.WhenUtc;
                if (e.Kind == "rate_limit") e.ResetUtc = LimitMessage.ParseReset(e.Text, e.WhenUtc);
                turn.Error = e;
                return turn;
            }
            return reply;
        }

        static string FirstText(Dictionary<string, object> record)
        {
            object msgObj;
            if (!record.TryGetValue("message", out msgObj)) return "";
            Dictionary<string, object> msg = msgObj as Dictionary<string, object>;
            if (msg == null) return "";
            object contentObj;
            if (!msg.TryGetValue("content", out contentObj)) return "";
            List<object> content = contentObj as List<object>;
            if (content == null) return "";
            foreach (object item in content)
            {
                Dictionary<string, object> block = item as Dictionary<string, object>;
                if (block == null) continue;
                string text = MiniJson.GetString(block, "text");
                if (text.Length > 0) return text.Length > 160 ? text.Substring(0, 160) : text;
            }
            return "";
        }
    }

    // Usage-limit messages end like "... session limit, resets 3:40am (Asia/Calcutta)".
    // The clock time is in the machine's own time zone.
    internal static class LimitMessage
    {
        static readonly Regex ResetPattern = new Regex(
            @"resets?\s+(?:at\s+)?(?:(?<mon>[A-Za-z]{3,9})\.?\s+(?<day>\d{1,2})(?:,|\s+at)?\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static readonly string[] Months = new string[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        static readonly TimeSpan MaxAhead = TimeSpan.FromDays(8);

        // Returns the reset moment in UTC, or DateTime.MinValue when the message names none.
        public static DateTime ParseReset(string text, DateTime errorUtc)
        {
            if (string.IsNullOrEmpty(text)) return DateTime.MinValue;
            Match m = ResetPattern.Match(text);
            if (!m.Success) return DateTime.MinValue;
            try
            {
                int hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
                int minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                if (hour < 1 || hour > 12 || minute > 59) return DateTime.MinValue;
                bool pm = string.Equals(m.Groups["ap"].Value, "pm", StringComparison.OrdinalIgnoreCase);
                hour = (hour % 12) + (pm ? 12 : 0);

                DateTime errorLocal = errorUtc.ToLocalTime();
                DateTime reset;
                if (m.Groups["mon"].Success)
                {
                    string name = m.Groups["mon"].Value.ToLowerInvariant();
                    int month = name.Length >= 3 ? Array.IndexOf(Months, name.Substring(0, 3)) + 1 : 0;
                    if (month < 1) return DateTime.MinValue;
                    int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
                    reset = new DateTime(errorLocal.Year, month, day, hour, minute, 0, DateTimeKind.Local);
                    if (reset < errorLocal.AddDays(-1)) reset = reset.AddYears(1);
                }
                else
                {
                    reset = new DateTime(errorLocal.Year, errorLocal.Month, errorLocal.Day, hour, minute, 0, DateTimeKind.Local);
                    if (reset <= errorLocal) reset = reset.AddDays(1);
                }
                if (reset - errorLocal > MaxAhead) return DateTime.MinValue;
                return reset.ToUniversalTime();
            }
            catch (Exception)
            {
                return DateTime.MinValue; // e.g. "Feb 30"
            }
        }
    }
}
