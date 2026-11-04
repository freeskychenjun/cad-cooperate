// ZwcTray.cs - system-tray "AI" badge for zwcad-cooperate.
//
// Why a separate process: the helper owns COM/STA work and must never run a WinForms
// message loop; the tray is pure UI and reads a small JSON state file (no COM at all).
//
//   ZwcTray.exe --state <state.json>
//
// State file contract (written by the wrapper, never by the tray):
//   { "doc": "<drawing name>", "step": "<what AI is doing>", "hwnd": 123, "pid": 456,
//     "startedAt": "2026-11-04T21:30:00+08:00",
//     "ui": { "prefix": "...", "balloonTitle": "...", "open": "...", "copy": "...", "end": "..." } }
// The tray exits by itself when: the state file disappears, the CAD pid is gone,
// the user picks the "end" menu item, or 4 hours pass (stale guard).
//
// IMPORTANT: this source is deliberately 100% ASCII. csc.exe reads a BOM-less file using the
// ANSI code page, so any CJK literal here would be mis-decoded and break the string literals;
// all user-visible Chinese text therefore travels in the state JSON (UTF-8, parsed at runtime).
//
// Built with: csc /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ZwcTray
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static string StatePath;
        private static NotifyIcon TrayIcon;
        private static Mutex Guard;
        private static readonly DateTime Started = DateTime.UtcNow;
        private static string LastTip = "";

        [STAThread]
        private static int Main(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--state") StatePath = args[i + 1];

            if (string.IsNullOrEmpty(StatePath) || !File.Exists(StatePath))
            {
                Console.Error.WriteLine("usage: ZwcTray.exe --state <state.json>");
                return 2;
            }

            var st = ReadState();
            if (st == null) return 2;

            string key = "Local\\zwc-tray-" + Sha8(st.doc);
            bool created;
            Guard = new Mutex(true, key, out created);
            if (!created) { Console.Error.WriteLine("tray already running for this drawing"); return 0; }

            Application.EnableVisualStyles();
            TrayIcon = new NotifyIcon();
            TrayIcon.Icon = BuildIcon(Color.FromArgb(255, 122, 0));
            TrayIcon.Text = Tooltip(st);
            TrayIcon.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add(st.uiOpen, null, (s, e) => Reveal(st.hwnd));
            menu.Items.Add(st.uiCopy, null, (s, e) => CopyInfo(st));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(st.uiEnd, null, (s, e) => Shutdown());
            TrayIcon.ContextMenuStrip = menu;

            // A new tray icon usually lands in the "hidden" overflow area, so a balloon is what
            // actually tells the user the AI is attached.
            TrayIcon.BalloonTipTitle = st.uiBalloonTitle;
            TrayIcon.BalloonTipText = st.uiPrefix + st.doc;
            TrayIcon.BalloonTipIcon = ToolTipIcon.Info;
            TrayIcon.ShowBalloonTip(4000);

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1500;
            timer.Tick += (s, e) => Poll();
            timer.Start();
            Application.Run();
            return 0;
        }

        private static void Poll()
        {
            if ((DateTime.UtcNow - Started).TotalHours > 4) { Shutdown(); return; }
            if (StatePath == null || !File.Exists(StatePath)) { Shutdown(); return; }
            var st = ReadState();
            if (st == null || st.pid <= 0 || !PidAlive(st.pid)) { Shutdown(); return; }
            string tip = Tooltip(st);
            if (tip != LastTip) { LastTip = tip; TrayIcon.Text = tip; }
        }

        private static bool PidAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
        }

        private static void Reveal(int hwnd)
        {
            if (hwnd == 0) return;
            try { ShowWindow(new IntPtr(hwnd), 9); SetForegroundWindow(new IntPtr(hwnd)); } catch { }
        }

        private static void CopyInfo(State s)
        {
            try { Clipboard.SetText(s.uiPrefix + " " + s.doc + "\r\nstep: " + s.step + "\r\npid: " + s.pid); } catch { }
        }

        private static void Shutdown()
        {
            try { if (TrayIcon != null) { TrayIcon.Visible = false; TrayIcon.Dispose(); } } catch { }
            try { Environment.Exit(0); } catch { }
        }

        private static string Tooltip(State s)
        {
            string t = s.uiPrefix + s.doc;
            if (!string.IsNullOrEmpty(s.step)) t += " - " + s.step;
            if (t.Length > 120) t = t.Substring(0, 119) + ".";
            return t;
        }

        private static string Sha8(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static Icon BuildIcon(Color fill)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var f = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var w = new SolidBrush(Color.White))
                {
                    SizeF sz = g.MeasureString("AI", f);
                    g.DrawString("AI", f, w, (32 - sz.Width) / 2f + 0.5f, (32 - sz.Height) / 2f + 0.5f);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // --- state model + a minimal flat-JSON reader (the writer is our own wrapper) ---
        private class State
        {
            public string doc = "";
            public string step = "";
            public string startedAt = "";
            public int hwnd;
            public int pid;
            public string uiPrefix = "[AI] ";
            public string uiBalloonTitle = "AI attached";
            public string uiOpen = "Open CAD window";
            public string uiCopy = "Copy takeover info";
            public string uiEnd = "End takeover (remove icon)";
        }

        private static State ReadState()
        {
            try
            {
                string json = File.ReadAllText(StatePath, Encoding.UTF8);
                var s = new State();
                s.doc = GetStr(json, "doc", s.doc);
                s.step = GetStr(json, "step", s.step);
                s.startedAt = GetStr(json, "startedAt", s.startedAt);
                s.hwnd = GetNum(json, "hwnd");
                s.pid = GetNum(json, "pid");
                s.uiPrefix = GetStr(json, "prefix", s.uiPrefix);
                s.uiBalloonTitle = GetStr(json, "balloonTitle", s.uiBalloonTitle);
                s.uiOpen = GetStr(json, "open", s.uiOpen);
                s.uiCopy = GetStr(json, "copy", s.uiCopy);
                s.uiEnd = GetStr(json, "end", s.uiEnd);
                if (string.IsNullOrEmpty(s.doc) && s.pid == 0) return null;
                return s;
            }
            catch { return null; }
        }

        private static string GetStr(string json, string key, string fallback)
        {
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return fallback;
            i = json.IndexOf(':', i + pat.Length);
            if (i < 0) return fallback;
            int q1 = json.IndexOf('"', i + 1);
            if (q1 < 0) return fallback;
            var sb = new StringBuilder();
            for (int j = q1 + 1; j < json.Length; j++)
            {
                char c = json[j];
                if (c == '\\' && j + 1 < json.Length)
                {
                    char n = json[++j];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u':
                            if (j + 4 < json.Length)
                            {
                                sb.Append((char)Convert.ToInt32(json.Substring(j + 1, 4), 16));
                                j += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else if (c == '"') break;
                else sb.Append(c);
            }
            string v = sb.ToString();
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static int GetNum(string json, string key)
        {
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return 0;
            i = json.IndexOf(':', i + pat.Length);
            if (i < 0) return 0;
            int j = i + 1;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
            int start = j;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
            if (j == start) return 0;
            int v;
            return int.TryParse(json.Substring(start, j - start), out v) ? v : 0;
        }
    }
}
