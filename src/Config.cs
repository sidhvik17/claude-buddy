using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace ClaudeBuddy
{
    // Settings live next to the exe (config.ini) so the install folder is self-contained.
    internal static class Config
    {
        public const int NoPosition = int.MinValue;

        public static int X = NoPosition;
        public static int Y = NoPosition;
        public static int Size = 1;          // 0 small, 1 medium, 2 large
        public static bool TopMost = true;
        public static bool AutoStart = true;
        public static bool KeepAwake;                 // keep the display on and the PC awake while the buddy runs
        public static string Hotkey = "Ctrl+Alt+H";   // hide/show; "none" turns it off
        public static long LimitResetTicks;           // remembered usage limit (UTC ticks), 0 = none
        public static long LimitSeenTicks;

        static string file;

        public static string AppDir
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        public static void Load(bool dev)
        {
            file = Path.Combine(AppDir, dev ? "config.dev.ini" : "config.ini");
            try
            {
                if (!File.Exists(file))
                {
                    Save(); // write the defaults so there is a file to edit (hotkey, size, ...)
                    return;
                }
                foreach (string raw in File.ReadAllLines(file))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim();
                    string value = raw.Substring(eq + 1).Trim();
                    int n;
                    bool isInt = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    if (key == "x" && isInt) X = n;
                    else if (key == "y" && isInt) Y = n;
                    else if (key == "size" && isInt) Size = Math.Max(0, Math.Min(2, n));
                    else if (key == "topmost") TopMost = value != "0";
                    else if (key == "autostart") AutoStart = value != "0";
                    else if (key == "keepawake") KeepAwake = value == "1";
                    else if (key == "hotkey") Hotkey = value;
                    else if (key == "limit_reset") long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out LimitResetTicks);
                    else if (key == "limit_seen") long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out LimitSeenTicks);
                }
            }
            catch (Exception ex)
            {
                Log.Write("config load failed: " + ex.Message);
            }
        }

        public static void Save()
        {
            if (file == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("x=" + X.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("y=" + Y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("size=" + Size.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("topmost=" + (TopMost ? "1" : "0"));
                sb.AppendLine("autostart=" + (AutoStart ? "1" : "0"));
                sb.AppendLine("keepawake=" + (KeepAwake ? "1" : "0"));
                sb.AppendLine("hotkey=" + Hotkey);
                sb.AppendLine("limit_reset=" + LimitResetTicks.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("limit_seen=" + LimitSeenTicks.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(file, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Write("config save failed: " + ex.Message);
            }
        }
    }

    // A system-wide key combination such as "Ctrl+Alt+H". The buddy never has keyboard
    // focus, so its shortcut has to be global, which is why the default includes Alt:
    // plain Ctrl+H would be taken away from every other program (browser history,
    // find-and-replace, backspace in terminals).
    internal sealed class Hotkey
    {
        public uint Modifiers;
        public uint Key;
        public string Display = "";

        // Returns null for "none", an empty string, or anything that is not a usable combination.
        public static Hotkey Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Hotkey h = new Hotkey();
            List<string> shown = new List<string>();
            string keyName = null;
            foreach (string raw in text.Split('+'))
            {
                string token = raw.Trim().ToUpperInvariant();
                if (token.Length == 0) return null;
                uint mod = 0;
                string modName = null;
                if (token == "CTRL" || token == "CONTROL") { mod = Native.MOD_CONTROL; modName = "Ctrl"; }
                else if (token == "ALT") { mod = Native.MOD_ALT; modName = "Alt"; }
                else if (token == "SHIFT") { mod = Native.MOD_SHIFT; modName = "Shift"; }
                else if (token == "WIN") { mod = Native.MOD_WIN; modName = "Win"; }
                if (mod != 0)
                {
                    if ((h.Modifiers & mod) == 0) shown.Add(modName);
                    h.Modifiers |= mod;
                    continue;
                }
                if (keyName != null) return null; // two keys
                int fn;
                if (token.Length == 1 && ((token[0] >= 'A' && token[0] <= 'Z') || (token[0] >= '0' && token[0] <= '9')))
                {
                    h.Key = token[0]; // virtual-key codes of letters and digits equal their ASCII codes
                }
                else if (token.Length >= 2 && token[0] == 'F' &&
                    int.TryParse(token.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out fn) && fn >= 1 && fn <= 24)
                {
                    h.Key = (uint)(0x70 + fn - 1);
                }
                else
                {
                    return null;
                }
                keyName = token;
            }
            if (keyName == null) return null;
            bool functionKey = h.Key >= 0x70;
            if (h.Modifiers == 0 && !functionKey) return null; // a bare letter would swallow typing
            shown.Add(keyName);
            h.Display = string.Join("+", shown.ToArray());
            return h;
        }
    }

    // Stops the display from dimming or turning off, and the PC from going to sleep by
    // itself, for as long as the request is held. Closing the lid or pressing the power
    // button still works. The request belongs to the thread that makes it, so it is always
    // made from the UI thread; Windows drops it when the process ends.
    internal static class ScreenAwake
    {
        // Returns the thread's previous execution state, or 0 if Windows refused.
        public static uint Set(bool on)
        {
            uint flags = Native.ES_CONTINUOUS;
            if (on) flags |= Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED;
            return Native.SetThreadExecutionState(flags);
        }
    }

    // Per-user autostart: HKCU Run key, no elevation needed.
    internal static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "ClaudeBuddy";

        public static void Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        string command = "\"" + Assembly.GetExecutingAssembly().Location + "\"";
                        if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                        {
                            key.SetValue(ValueName, command, RegistryValueKind.String);
                            Log.Write("autostart registered: " + command);
                        }
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                        Log.Write("autostart removed");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("autostart update failed: " + ex.Message);
            }
        }
    }

    internal static class Log
    {
        const long MaxBytes = 512 * 1024;
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string path = Path.Combine(Config.AppDir, "buddy.log");
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        try
                        {
                            string old = path + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                        }
                        catch (Exception)
                        {
                            // Something has the log open. Keep writing rather than go silent,
                            // up to a hard limit; rotation is retried on the next write.
                            if (fi.Length > MaxBytes * 4) return;
                        }
                    }
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never take the buddy down.
            }
        }
    }

    // Opens the Claude desktop app on its Code tab through claude:// deep links.
    internal static class Launcher
    {
        const string CodeHome = "claude://code/new?source=desktop_action";

        public static void OpenCodeHome()
        {
            Open(CodeHome);
        }

        public static void OpenSession(SessionInfo s)
        {
            // Only desktop-hosted sessions have a page to jump to.
            if (s != null && s.HostSessionId.StartsWith("local_", StringComparison.Ordinal) && SessionScanner.IsSafeId(s.HostSessionId))
            {
                Open("claude://code/continue?session=" + s.HostSessionId + "&source=desktop_action");
            }
            else
            {
                OpenCodeHome();
            }
        }

        static void Open(string url)
        {
            Log.Write("open " + url);
            // The Store app's execution alias survives app updates; the registered
            // claude:// handler path contains the version and can go stale.
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\claude-desktop.exe");
            try
            {
                if (File.Exists(alias))
                {
                    ProcessStartInfo psi = new ProcessStartInfo(alias, "\"" + url + "\"");
                    psi.UseShellExecute = false;
                    using (Process.Start(psi)) { }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("alias launch failed: " + ex.Message);
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex)
            {
                Log.Write("could not open Claude: " + ex.Message);
            }
        }
    }
}
