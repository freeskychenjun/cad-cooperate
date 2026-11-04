// ZwcClient.cs - M3-E1 fast client: the piece that removes the PowerShell startup tax.
//
//   bin\ZwcClient.exe <command> [flags...]        (zwc.cmd calls this)
//   bin\ZwcClient.exe -RequestFile <req.json>     (verbatim request, no parsing)
//
// Why this exists: measured on this machine, `powershell -Command exit` alone costs ~1856 ms and
// `powershell -File zwc.ps1 doctor` ~4165 ms, while the helper itself needs 25-50 ms. A resident
// daemon cannot fix that - the process that LAUNCHES the request is the bottleneck. A native
// console exe starts in ~60-100 ms and speaks the same named-pipe JSON protocol, so the whole
// command fits in the sub-600 ms budget.
//
// Fallback chain (every step bounded, nothing can hang):
//   1. connect to the daemon pipe (name derived from the helper source hash) and send the request;
//   2. no daemon -> start `ZwcHelper.exe --serve <pipe>` hidden, poll up to 20 s, then send;
//   3. still no answer -> one-shot helper (--request/--response temp files), same protocol.
// Anything unexpected prints exactly one JSON envelope on stdout and exits 0/2/3 like zwc.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZwcClient
{
    internal static class Program
    {
        private const string RepoMarker = "scripts";

        private static string RepoRoot()
        {
            // exe lives in <repo>\bin\ ; fall back to the current directory
            try
            {
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string root = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, RepoMarker))) return root;
                }
            }
            catch (Exception) { }
            return Directory.GetCurrentDirectory();
        }

        /// <summary>Same source-hash recipe as scripts\zwc.ps1 so client, daemon pipe name and the
        /// cached helper exe always agree: file count | newest mtime ticks | sorted sizes.</summary>
        private static string HelperHash(string root)
        {
            string dir = Path.Combine(Path.Combine(root, "scripts"), "helper");
            FileInfo[] files = new DirectoryInfo(dir).GetFiles("*.cs");
            Array.Sort(files, delegate(FileInfo a, FileInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            long newest = 0;
            List<long> sizes = new List<long>();
            foreach (FileInfo f in files)
            {
                sizes.Add(f.Length);
                if (f.LastWriteTimeUtc.Ticks > newest) newest = f.LastWriteTimeUtc.Ticks;
            }
            sizes.Sort();
            StringBuilder key = new StringBuilder();
            key.Append(files.Length).Append('|').Append(newest).Append('|');
            for (int i = 0; i < sizes.Count; i++)
            {
                if (i > 0) key.Append(',');
                key.Append(sizes[i]);
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToString()));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch (Exception) { }

            if (args.Length == 0)
            {
                Console.Out.Write("{\"ok\":false,\"cmd\":\"\",\"error\":{\"code\":\"BAD_ARGS\",\"message\":\"usage: ZwcClient.exe <command> [flags...] | -RequestFile <json>\"}}");
                return 2;
            }

            string root = RepoRoot();

            // serve / --build-client are wrapper-level admin commands, not helper commands: hand
            // them back to zwc.ps1 (rare, and the PowerShell cost is irrelevant for them)
            string admin = args[0].ToLowerInvariant();
            if (admin == "serve" || admin == "--build-client" || admin == "buildclient")
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("powershell",
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(Path.Combine(root, "scripts"), "zwc.ps1") + "\" " + JoinArgs(args));
                    psi.UseShellExecute = false;
                    Process p = Process.Start(psi);
                    p.WaitForExit();
                    return p.ExitCode;
                }
                catch (Exception e)
                {
                    Console.Out.Write("{\"ok\":false,\"cmd\":\"serve\",\"error\":{\"code\":\"CLIENT_FAILED\",\"message\":\"" + JsonEscape(e.Message) + "\"}}");
                    return 2;
                }
            }

            string requestJson;
            string command = args[0];
            int timeoutSec = 120;

            try
            {
                requestJson = BuildRequest(args, out command, out timeoutSec);
            }
            catch (Exception e)
            {
                Console.Out.Write("{\"ok\":false,\"cmd\":\"" + JsonEscape(command) + "\",\"error\":{\"code\":\"BAD_ARGS\",\"message\":\"" + JsonEscape(e.Message) + "\"}}");
                return 2;
            }

            string sha = HelperHash(root);
            // A frozen/copied tree may carry the sources but not bin\cache\<sha>\: compile the helper
            // on demand instead of failing with CLIENT_FAILED (measured by audit-dev on a frozen
            // snapshot: some tasks worked, others reported missing helper binary).
            EnsureHelper(root, sha);
            string pipeName = "zwc-" + sha;
            string helperExe = Path.Combine(Path.Combine(Path.Combine(root, "bin"), "cache"), sha);
            helperExe = Path.Combine(helperExe, "ZwcHelper.exe");

            string response = null;
            string transport = null;

            // 1) warm daemon?
            response = TryPipe(pipeName, requestJson, 400, timeoutSec);
            if (response != null) transport = "daemon";

            // 2) start the daemon, then retry. A cross-process mutex makes sure only one client
            // spawns it: without it three concurrent sessions all see "no pipe", all launch a
            // helper, one wins the pipe and the losers retry for seconds (20 s spikes).
            if (response == null && File.Exists(helperExe))
            {
                System.Diagnostics.Stopwatch startSw = System.Diagnostics.Stopwatch.StartNew();
                Mutex startLock = new Mutex(false, "Local\\zwc-start-" + sha);
                bool haveStartLock = false;
                try { haveStartLock = startLock.WaitOne(8000); } catch (Exception) { }
                try
                {
                    if (haveStartLock)
                    {
                        // P0 (2026-11-04): the daemon is a LONG-LIVED child. Process.Start with
                        // UseShellExecute=false + redirects still lets the child inherit OTHER
                        // inheritable handles (incl. the caller stdout pipe), so ShellExecuteEx is
                        // used instead: it does NOT inherit handles, and the daemon is detached.
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(helperExe, "--serve " + pipeName + " --idle-ms 1200000");
                            psi.UseShellExecute = true;
                            psi.WindowStyle = ProcessWindowStyle.Hidden;
                            psi.WorkingDirectory = root;
                            try { Process.Start(psi); } catch (Exception) { }
                        }
                        catch (Exception) { }
                        bool ready = false;
                        for (int i = 0; i < 25 && !ready; i++)
                        {
                            Thread.Sleep(150);
                            ready = (PingPipe(pipeName) != null);
                        }
                    }
                    response = TryPipe(pipeName, requestJson, 500, Math.Min(timeoutSec, 30));
                    if (response != null) transport = "daemon-started";
                }
                finally
                {
                    try { if (haveStartLock) startLock.ReleaseMutex(); } catch (Exception) { }
                    try { startLock.Dispose(); } catch (Exception) { }
                }
            }

            // 3) one-shot helper fallback
            if (response == null && File.Exists(helperExe))
            {
                response = OneShot(helperExe, requestJson, timeoutSec);
                if (response != null) transport = "one-shot";
            }

            if (response == null)
            {
                string msg = File.Exists(helperExe)
                    ? "neither the resident daemon nor the one-shot helper answered"
                    : "helper binary missing: " + helperExe + " (run scripts\\zwc.ps1 once to compile it)";
                Console.Out.Write("{\"ok\":false,\"cmd\":\"" + JsonEscape(command)
                    + "\",\"error\":{\"code\":\"CLIENT_FAILED\",\"message\":\"" + JsonEscape(msg) + "\"}}");
                return 2;
            }

            // annotate the transport without re-serializing the whole payload
            string text = response.TrimEnd();
            if (text.StartsWith("{", StringComparison.Ordinal) && transport != null)
                text = "{\"transport\":\"" + transport + "\"," + text.Substring(1);

            // ---- system-tray "AI" badge integration (hint -Tray / hint -Clear / finish)
            try { MaybeTray(root, command, args, text); } catch (Exception) { }

            Console.Out.Write(text);
            bool ok = text.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0
                   && text.IndexOf("\"ok\":false", StringComparison.Ordinal) < 0;
            if (ok) return 0;
            foreach (string gate in new string[] { "VERIFY_FAILED", "BAD_DATA", "TOO_WIDE", "CMD_STATE_POLLUTED", "SENDCMD_TOO_LONG", "LISP_UNBALANCED", "OPEN_FAILED" })
            {
                if (text.IndexOf(gate, StringComparison.Ordinal) >= 0) return 3;
            }
            return 2;
        }

        // ------------------------------------------------------------------ tray badge
        /// <summary>hint -Tray starts the tray icon for the drawing; hint -Clear and finish stop it.
        /// UI text comes from scripts\tray\ui.json so both C# and .ps1 sources stay pure ASCII.</summary>
        private static void MaybeTray(string root, string command, string[] args, string responseText)
        {
            string trayScript = Path.Combine(Path.Combine(root, "scripts"), "tray.ps1");
            if (!File.Exists(trayScript)) return;
            bool trayFlag = HasFlag(args, "-Tray") || HasFlag(args, "--tray");
            bool clearFlag = HasFlag(args, "-Clear");
            bool isFinish = string.Equals(command, "finish", StringComparison.OrdinalIgnoreCase);
            if (!trayFlag && !clearFlag && !isFinish) return;
            string doc = ValueOf(args, "-Doc");
            if (string.IsNullOrEmpty(doc))
            {
                Match m = Regex.Match(responseText ?? "", "\"doc\":\\{[^}]*\"name\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (m.Success) doc = UnescapeJson(m.Groups[1].Value);
            }
            if (clearFlag || isFinish)
            {
                if (!string.IsNullOrEmpty(doc))
                {
                    SpawnTray(trayScript, new string[] { "-Stop", "-Doc", doc });
                }
                else
                {
                    // finish without -Doc: stop every tray state file we own (one per drawing)
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "zwcad-cooperate");
                        if (Directory.Exists(dir))
                        {
                            foreach (string f in Directory.GetFiles(dir, "tray-*.json"))
                            {
                                string j = File.ReadAllText(f);
                                Match m = Regex.Match(j, "\"doc\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                                if (m.Success) SpawnTray(trayScript, new string[] { "-Stop", "-Doc", UnescapeJson(m.Groups[1].Value) });
                            }
                        }
                    }
                    catch (Exception) { }
                }
                return;
            }
            if ((responseText ?? "").IndexOf("\"ok\":true", StringComparison.Ordinal) < 0) return;
            string step = ValueOf(args, "-Text");
            string hwnd = Match1(responseText, "\"hwnd\":(\\d+)");
            string pid = Match1(responseText, "\"pid\":(\\d+)");
            SpawnTray(trayScript, new string[] { "-Start", "-Doc", doc, "-Step", step, "-Hwnd", hwnd, "-CadPid", pid });
        }

        private static void SpawnTray(string trayScript, string[] trayArgs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -File \"").Append(trayScript).Append("\"");
            foreach (string a in trayArgs)
            {
                sb.Append(' ');
                if (a == null || a.Length == 0) sb.Append("\"\"");
                else if (a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0) sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                else sb.Append(a);
            }
            ProcessStartInfo psi = new ProcessStartInfo("powershell", sb.ToString());
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            try { Process.Start(psi); } catch (Exception) { }
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ValueOf(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        private static string Match1(string text, string pattern)
        {
            Match m = Regex.Match(text ?? "", pattern);
            return m.Success ? m.Groups[1].Value : "0";
        }

        private static string UnescapeJson(string s)
        {
            return s.Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\/", "/");
        }

        // ------------------------------------------------------------------ transports
        /// <summary>Cheap liveness probe while a freshly spawned daemon initialises: ping with a
        /// ~1 s deadline so a half-initialised daemon can never hold the client for the full
        /// request timeout (measured once: 300 s cold start, client looked hung).</summary>
        private static string PingPipe(string pipeName)
        {
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(300);
                WriteMessage(pipe, "{\"cmd\":\"ping\"}");
                return ReadMessage(pipe, 1000);
            }
            catch (Exception) { return null; }
            finally { if (pipe != null) { try { pipe.Dispose(); } catch (Exception) { } } }
        }

        private static string TryPipe(string pipeName, string requestJson, int connectMs, int timeoutSec)
        {
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(connectMs);
                WriteMessage(pipe, requestJson);
                return ReadMessage(pipe, timeoutSec * 1000);
            }
            catch (Exception) { return null; }
            finally { if (pipe != null) { try { pipe.Dispose(); } catch (Exception) { } } }
        }

        /// <summary>Compile scripts\helper\*.cs into bin\cache\&lt;sha&gt;\ZwcHelper.exe when the cached
        /// binary for the current source hash is absent (frozen snapshot / fresh clone / cache purge).
        /// Mirrors scripts\zwc.ps1 so both entries can bootstrap independently.</summary>
        private static bool EnsureHelper(string root, string sha)
        {
            string cacheDir = Path.Combine(Path.Combine(Path.Combine(root, "bin"), "cache"), sha);
            string exe = Path.Combine(cacheDir, "ZwcHelper.exe");
            if (File.Exists(exe)) return true;
            string[] cscCandidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET\\Framework\\v4.0.30319\\csc.exe")
            };
            string csc = null;
            foreach (string cand in cscCandidates) { if (File.Exists(cand)) { csc = cand; break; } }
            if (csc == null) return false;
            string helperDir = Path.Combine(Path.Combine(root, "scripts"), "helper");
            if (!Directory.Exists(helperDir)) return false;
            FileInfo[] sources = new DirectoryInfo(helperDir).GetFiles("*.cs");
            if (sources.Length == 0) return false;
            try
            {
                Directory.CreateDirectory(cacheDir);
                string tmp = Path.Combine(cacheDir, "build-" + Process.GetCurrentProcess().Id + ".exe");
                StringBuilder args = new StringBuilder();
                args.Append("-nologo -optimize+ -target:exe -platform:anycpu -codepage:65001 ");
                args.Append("-out:\"").Append(tmp).Append("\" -r:System.dll -r:System.Core.dll -r:System.Management.dll");
                foreach (FileInfo f in sources) args.Append(" \"").Append(f.FullName).Append("\"");
                ProcessStartInfo psi = new ProcessStartInfo(csc, args.ToString());
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return false;
                }
                if (!File.Exists(tmp)) return false;
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(tmp, exe);
                return true;
            }
            catch (Exception) { return false; }
        }
        private static string OneShot(string helperExe, string requestJson, int timeoutSec)
        {
            string work = Path.Combine(Path.GetTempPath(), "zwcad-cooperate\\client-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(work);
                string reqPath = Path.Combine(work, "request.json");
                string respPath = Path.Combine(work, "response.json");
                File.WriteAllText(reqPath, requestJson, new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo(helperExe, "--request \"" + reqPath + "\" --response \"" + respPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                // drain both pipes asynchronously: the helper prints the whole response JSON on
                // stdout, and a redirected stream that nobody reads deadlocks the child once the
                // 4 KB pipe buffer fills up
                StringBuilder so = new StringBuilder();
                StringBuilder se = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (so) { so.AppendLine(e.Data); } };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (se) { se.AppendLine(e.Data); } };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return "{\"ok\":false,\"cmd\":\"\",\"error\":{\"code\":\"TIMEOUT\",\"message\":\"one-shot helper did not answer within " + timeoutSec + " s\"}}";
                }
                if (File.Exists(respPath)) return File.ReadAllText(respPath, new UTF8Encoding(false));
                return null;
            }
            catch (Exception) { return null; }
            finally { try { Directory.Delete(work, true); } catch (Exception) { } }
        }

        private static void WriteMessage(Stream s, string text)
        {
            byte[] buf = Encoding.UTF8.GetBytes(text ?? "");
            byte[] hdr = BitConverter.GetBytes(buf.Length);
            s.Write(hdr, 0, 4);
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }

        /// <summary>Read one message but never longer than the deadline (a daemon stuck inside a
        /// blocked COM call must not hang the client).</summary>
        private static string ReadMessage(Stream s, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            byte[] hdr = new byte[4];
            if (!ReadFully(s, hdr, 4, sw, timeoutMs)) return null;
            int len = BitConverter.ToInt32(hdr, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;
            byte[] buf = new byte[len];
            if (!ReadFully(s, buf, len, sw, timeoutMs)) return null;
            return Encoding.UTF8.GetString(buf);
        }

        private static bool ReadFully(Stream s, byte[] buf, int count, Stopwatch sw, int timeoutMs)
        {
            int got = 0;
            while (got < count)
            {
                int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                IAsyncResult ar = s.BeginRead(buf, got, count - got, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(remaining)) return false;
                int n = s.EndRead(ar);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        // ------------------------------------------------------------------ argument -> request JSON
        private static readonly string[] BoolSwitches = new string[]
        {
            "launch", "readonly", "nohint", "dryrun", "nocad", "noaxis", "nopointno", "noautoscale",
            "axis", "handles", "summary", "fast", "clear", "quit", "new", "deep", "keep", "help",
            "slow", "keeponfail", "nolegend", "scalebar", "pointno", "strict", "continue", "plan", "noundomark", "reveal", "sweephidden", "noreveal", "tray", "geom", "forcelispfile"
        };

        private static readonly string[] ValueSwitches = new string[]
        {
            "app", "progid", "doc", "data", "sheet", "cols", "paper", "origin", "waterlevels", "text",
            "token", "scope", "space", "mode", "limit", "cursor", "layerprefix", "out", "hscale",
            "vscale", "labelevery", "count", "max", "comthreshold", "timeoutsec", "requestfile",
            "layer", "type", "bbox", "xdata", "items", "since", "tol", "locktimeout", "busytimeout", "faultinject", "filter"
        };

        private static readonly string[] NumericSwitches = new string[]
        {
            "hscale", "vscale", "labelevery", "count", "max", "comthreshold", "timeoutsec", "limit",
            "locktimeout", "busytimeout"
        };

        private static string BuildRequest(string[] args, out string command, out int timeoutSec)
        {
            timeoutSec = 120;
            if (args.Length == 0) throw new Exception("no command given");

            // -RequestFile passthrough
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-RequestFile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string path = args[i + 1];
                    if (!File.Exists(path)) throw new Exception("request file not found: " + path);
                    string raw = File.ReadAllText(path, new UTF8Encoding(false));
                    command = GuessCommand(raw);
                    return raw;
                }
            }

            command = args[0];
            Dictionary<string, string> opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                string tok = args[i];
                if (tok.Length > 1 && tok.StartsWith("-", StringComparison.Ordinal))
                {
                    string key = tok.TrimStart('-').ToLowerInvariant();
                    if (Array.IndexOf(BoolSwitches, key) >= 0) { opts[key] = "true"; continue; }
                    bool valueSwitch = Array.IndexOf(ValueSwitches, key) >= 0;
                    string val = null;
                    if (i + 1 < args.Length)
                    {
                        string next = args[i + 1];
                        bool nextLooksFlag = next.Length > 1 && next.StartsWith("-", StringComparison.Ordinal)
                            && (Array.IndexOf(BoolSwitches, next.TrimStart('-').ToLowerInvariant()) >= 0
                                || Array.IndexOf(ValueSwitches, next.TrimStart('-').ToLowerInvariant()) >= 0);
                        if (valueSwitch || !nextLooksFlag) { val = next; i++; }
                    }
                    opts[key] = val == null ? "true" : val;
                }
            }

            string appKind = opts.ContainsKey("app") ? opts["app"] : "zwcad";
            string docArg = null;
            if (opts.ContainsKey("doc")) docArg = opts["doc"];
            if (opts.ContainsKey("timeoutsec"))
            {
                int t;
                if (int.TryParse(opts["timeoutsec"], NumberStyles.Integer, CultureInfo.InvariantCulture, out t) && t > 0) timeoutSec = t;
            }

            StringBuilder a = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, string> kv in opts)
            {
                string key = kv.Key;
                if (key == "app" || key == "doc" || key == "keep" || key == "timeoutsec" || key == "requestfile") continue;
                if (key == "space") key = "scope";                    // -Space is the read alias
                if (!first) a.Append(',');
                first = false;
                a.Append('"').Append(JsonEscape(key)).Append("\":");
                if (Array.IndexOf(NumericSwitches, kv.Key) >= 0)
                {
                    a.Append(kv.Value);
                }
                else if (Array.IndexOf(BoolSwitches, kv.Key) >= 0)
                {
                    a.Append("true");
                }
                else
                {
                    a.Append('"').Append(JsonEscape(PathValue(kv.Key, kv.Value))).Append('"');
                }
            }
            a.Append('}');

            StringBuilder req = new StringBuilder();
            req.Append("{\"cmd\":\"").Append(JsonEscape(command)).Append("\",");
            req.Append("\"doc\":").Append(docArg == null ? "null" : "\"" + JsonEscape(docArg) + "\"").Append(',');
            req.Append("\"app\":\"").Append(JsonEscape(appKind)).Append("\",");
            req.Append("\"args\":").Append(a).Append('}');
            return req.ToString();
        }

        private static string PathValue(string key, string value)
        {
            if (key != "data" && key != "out") return value;
            try
            {
                if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
                string cand = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), value));
                return cand;
            }
            catch (Exception) { return value; }
        }

        private static string GuessCommand(string raw)
        {
            int i = raw.IndexOf("\"cmd\"", StringComparison.Ordinal);
            if (i < 0) return "";
            int c = raw.IndexOf(':', i);
            int q1 = raw.IndexOf('"', c + 1);
            int q2 = raw.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 < 0) return "";
            return raw.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static string JoinArgs(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                string v = a == null ? "" : a;
                if (v.IndexOf(' ') >= 0 && !v.StartsWith("\"", StringComparison.Ordinal)) sb.Append('"').Append(v).Append('"');
                else sb.Append(v);
            }
            return sb.ToString();
        }
        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
