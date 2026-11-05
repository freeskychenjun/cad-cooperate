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
        /// cached helper exe always agree: file count | newest mtime ticks | sorted sizes.
        /// <paramref name="key"/> returns the raw recipe string: `ZwcClient.exe hash` prints it so the
        /// wrapper can compare both implementations of this recipe instead of trusting a comment
        /// (P2-5.2: the recipe used to be copy-pasted in four places).</summary>
        private static string HelperHash(string root, out string key)
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
            StringBuilder recipe = new StringBuilder();
            recipe.Append(files.Length).Append('|').Append(newest).Append('|');
            for (int i = 0; i < sizes.Count; i++)
            {
                if (i > 0) recipe.Append(',');
                recipe.Append(sizes[i]);
            }
            key = recipe.ToString();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ exit-code classification
        // P2-5.4: the exit code used to be decided by searching the WHOLE response text for the
        // substrings "ok":true / "ok":false and for the gate codes, which meant a legitimate payload
        // such as {"ok":true,"result":{"note":"BAD_DATA appeared in text"}} exited 3 (gate failure)
        // and any ok:false response merely echoing "ok":true inside a message could exit 0.
        // Only the top-level members of the response document decide now:
        //   top-level ok === true                          -> 0
        //   else top-level error.code in the gate set      -> 3
        //   else                                          -> 2
        // The gate set mirrors the legacy PowerShell path (scripts\zwc.ps1, $gate list) on purpose:
        // both entry points must classify a response identically.
        private static readonly string[] GateCodes = new string[]
        {
            "VERIFY_FAILED", "BAD_DATA", "TOO_WIDE", "CMD_STATE_POLLUTED", "SENDCMD_TOO_LONG",
            "LISP_UNBALANCED", "OPEN_FAILED"
        };

        /// <summary>Exit code for one response document. Pure function: no daemon, no CAD, no
        /// filesystem. Exposed for unit tests through --selftest-exitcode.</summary>
        private static int ExitCodeFor(string text)
        {
            bool ok;
            if (TopLevelBool(text, "ok", out ok) && ok) return 0;
            string code = TopLevelErrorCode(text);
            if (code != null) { code = code.Trim(); }
            if (!string.IsNullOrEmpty(code) && Array.IndexOf(GateCodes, code) >= 0) return 3;
            return 2;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static bool ExpectLiteral(string s, int i, string lit)
        {
            if (i < 0 || i + lit.Length > s.Length) return false;
            return string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0;
        }

        /// <summary>Read a JSON string starting at s[i] == '"'. Returns the decoded content
        /// (only the escapes that matter for key/code comparison are decoded).</summary>
        private static bool ReadJsonString(string s, ref int i, out string value)
        {
            value = null;
            if (i >= s.Length || s[i] != '"') return false;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; value = sb.ToString(); return true; }
                if (c == '\\' && i + 1 < s.Length)
                {
                    char e = s[i + 1];
                    i += 2;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int v;
                                if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                                    sb.Append((char)v);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return false;
        }

        /// <summary>Skip one JSON value (nested objects/arrays included) so the member scanner can
        /// walk past fields it does not care about - e.g. a "result" object that itself contains an
        /// "ok" field, or a "message" string that mentions BAD_DATA.</summary>
        private static bool SkipValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return false;
            char c = s[i];
            if (c == '"') { string ignored; return ReadJsonString(s, ref i, out ignored); }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (ch == '"')
                    {
                        string ignored;
                        if (!ReadJsonString(s, ref i, out ignored)) return false;
                        continue;
                    }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        i++;
                        if (depth == 0) return true;
                        continue;
                    }
                    i++;
                }
                return false;
            }
            int start = i;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == ',' || ch == '}' || ch == ']' || ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') break;
                i++;
            }
            return i > start;
        }

        /// <summary>Find a member of the object whose opening brace is at objStart and return the
        /// [start,end) span of its value. Only that object's own members are considered, so nested
        /// objects are never mistaken for top-level ones.</summary>
        private static bool FindMember(string s, int objStart, string name, out int valueStart, out int valueEnd)
        {
            valueStart = -1;
            valueEnd = -1;
            int i = objStart;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '{') return false;
            i++;
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == '}') return false;
                string key;
                if (!ReadJsonString(s, ref i, out key)) return false;
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') return false;
                i++;
                SkipWs(s, ref i);
                int vs = i;
                if (!SkipValue(s, ref i)) return false;
                int ve = i;
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    valueStart = vs;
                    valueEnd = ve;
                    return true;
                }
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                return false;
            }
        }

        private static bool RootObjectStart(string text, out int start)
        {
            start = 0;
            if (string.IsNullOrEmpty(text)) return false;
            SkipWs(text, ref start);
            if (start >= text.Length || text[start] != '{') return false;
            return true;
        }

        private static bool TopLevelBool(string text, string name, out bool value)
        {
            value = false;
            int root;
            int vs, ve;
            if (!RootObjectStart(text, out root)) return false;
            if (!FindMember(text, root, name, out vs, out ve)) return false;
            if (ExpectLiteral(text, vs, "true")) { value = true; return true; }
            if (ExpectLiteral(text, vs, "false")) { value = false; return true; }
            return false;
        }

        /// <summary>Top-level error.code of a response, or null. The "error" member may sit anywhere
        /// at the top level (the helper writes it after result/warnings/undoToken/elapsedMs/app), and
        /// its own "message" may contain braces or the word BAD_DATA - that is why this walks the
        /// object structurally instead of searching text.</summary>
        private static string TopLevelErrorCode(string text)
        {
            int root;
            int vs, ve;
            if (!RootObjectStart(text, out root)) return null;
            if (!FindMember(text, root, "error", out vs, out ve)) return null;
            int i = vs;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') return null;
            int codeVs, codeVe;
            if (!FindMember(text, i, "code", out codeVs, out codeVe)) return null;
            int j = codeVs;
            SkipWs(text, ref j);
            string code;
            if (!ReadJsonString(text, ref j, out code)) return null;
            return code;
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
            string admin = args[0].ToLowerInvariant();

            // ---- offline/test subcommands (no daemon, no CAD, no compile) -------------------
            // --selftest-exitcode [file]: classify a fake response document and exit with the code
            // the real command would have used. This is the unit-test entry point for the exit-code
            // extractor (P2-5.4): stdin when no file is given, nothing else is touched.
            if (admin == "--selftest-exitcode" || admin == "selftest-exitcode")
            {
                string sample = null;
                if (args.Length > 1 && !args[1].StartsWith("-", StringComparison.Ordinal))
                {
                    try { sample = File.ReadAllText(args[1], new UTF8Encoding(false)); }
                    catch (Exception) { sample = null; }
                }
                else
                {
                    try { sample = Console.In.ReadToEnd(); } catch (Exception) { sample = null; }
                }
                int code = ExitCodeFor(sample == null ? "" : sample);
                Console.Out.Write(code.ToString(CultureInfo.InvariantCulture));
                return code;
            }

            // hash: print this build's view of the helper source hash recipe (pure computation).
            // `scripts\zwc.ps1 serve -Action status` compares it with its own implementation and
            // reports recipeMatch, so a one-sided recipe edit can no longer split the two paths.
            if (admin == "hash" || admin == "--hash")
            {
                string recipeKey;
                string recipeSha;
                try
                {
                    recipeSha = HelperHash(root, out recipeKey);
                }
                catch (Exception e)
                {
                    Console.Out.Write("{\"ok\":false,\"cmd\":\"hash\",\"error\":{\"code\":\"HASH_FAILED\",\"message\":\""
                        + JsonEscape(e.Message) + "\"}}");
                    return 2;
                }
                Console.Out.Write("{\"ok\":true,\"cmd\":\"hash\",\"helperSha\":\"" + recipeSha
                    + "\",\"key\":\"" + JsonEscape(recipeKey) + "\"}");
                return 0;
            }

            // --prune-cache [-Keep N]: compile-cache LRU eviction (P1-4.3). One JSON document, works
            // without a daemon and never disturbs a running one (locked binaries simply fail to be
            // deleted and are reported in "failed").
            if (admin == "--prune-cache" || admin == "prunecache" || admin == "prune-cache")
            {
                int keep = 10;
                for (int i = 1; i + 1 < args.Length; i++)
                {
                    if (string.Equals(args[i], "-Keep", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(args[i], "--keep", StringComparison.OrdinalIgnoreCase))
                    {
                        int parsed;
                        if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0) keep = parsed;
                    }
                }
                string currentSha = null;
                try
                {
                    string prunedKey;
                    currentSha = HelperHash(root, out prunedKey);
                }
                catch (Exception) { currentSha = null; }
                string statsJson;
                try { statsJson = PruneCache(root, keep, currentSha); }
                catch (Exception e)
                {
                    Console.Out.Write("{\"ok\":false,\"cmd\":\"prune-cache\",\"error\":{\"code\":\"PRUNE_FAILED\",\"message\":\""
                        + JsonEscape(e.Message) + "\"}}");
                    return 2;
                }
                Console.Out.Write("{\"ok\":true,\"cmd\":\"prune-cache\",\"result\":" + statsJson + "}");
                return 0;
            }

            // serve / --build-client are wrapper-level admin commands, not helper commands: hand
            // them back to zwc.ps1 (rare, and the PowerShell cost is irrelevant for them)
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

            string sha;
            string helperKey;
            sha = HelperHash(root, out helperKey);
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
                // A fresh daemon means "the cache just gained/uses an entry": this is the one cheap
                // moment to evict old compile-cache directories (P1-4.3). Deliberately NOT done on
                // every command - the scan would add directory I/O to the sub-600 ms budget.
                if (transport == "daemon-started")
                {
                    try { PruneCache(root, PruneKeep, sha); } catch (Exception) { }
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
            // The exit code comes from the top-level envelope only (see ExitCodeFor): 0 = ok:true,
            // 3 = ok:false with a gate error.code, 2 = anything else. Never from a substring of the
            // whole document - a message may legitimately mention BAD_DATA or VERIFY_FAILED.
            return ExitCodeFor(text);
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
        /// Mirrors scripts\zwc.ps1 so both entries can bootstrap independently.
        /// P1-4.3: a failed compile used to leave the freshly created bin\cache\&lt;sha&gt; directory
        /// behind (33 empty directories / 13 MB of dead cache on this machine). Everything this call
        /// creates is now rolled back on failure - and only what it created: an existing binary,
        /// another process's build-&lt;pid&gt;.exe or a directory that appeared in the meantime are left
        /// alone.</summary>
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

            bool createdCacheDir = false;
            string tmp = null;
            bool ownTmpFile = false;
            try
            {
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                    createdCacheDir = true;
                }
                tmp = Path.Combine(cacheDir, "build-" + Process.GetCurrentProcess().Id + ".exe");
                bool tmpExistedBefore = File.Exists(tmp);
                // The name is per-process, so anything we see now is almost certainly our own; still,
                // only delete what this call actually produced.
                ownTmpFile = !tmpExistedBefore;
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
                ownTmpFile = false;
                createdCacheDir = false;      // the directory now holds a valid binary: keep it
                return true;
            }
            catch (Exception) { return false; }
            finally
            {
                if (ownTmpFile && tmp != null)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                }
                if (createdCacheDir)
                {
                    try
                    {
                        if (Directory.Exists(cacheDir) && Directory.GetFileSystemEntries(cacheDir).Length == 0)
                            Directory.Delete(cacheDir);
                    }
                    catch (Exception) { }
                }
            }
        }

        // ------------------------------------------------------------------ compile-cache eviction
        // P1-4.3: every source edit creates a new bin\cache\<sha12>\ZwcHelper.exe (~127 KB) and
        // nothing ever removed the old ones. PruneCache keeps the N most recently used entries
        // (directory mtime) plus the entry for the CURRENT source hash, and always removes empty
        // directories - which is where the 33 failed-compile leftovers came from. Deleting a
        // directory that a live daemon runs from fails (the exe is locked) and is reported under
        // "failed" instead of breaking the daemon, so pruning while a daemon serves is safe.
        private const int PruneKeep = 10;
        // Only touch directories whose name is exactly 12 hex chars: bin\ZwcClient.exe, bin\tray and
        // anything a human put in bin\cache stay untouched.
        private static readonly Regex ShaDirName = new Regex("^[0-9a-f]{12}$", RegexOptions.CultureInvariant);
        // A directory created by a compile that is still running is empty for a moment; never judge
        // "empty" on a freshly touched directory or two concurrent compiles could delete each
        // other's output target.
        private const int EmptyDirGraceSeconds = 300;

        private sealed class CacheEntry
        {
            public string Name;
            public string Path;
            public DateTime MTimeUtc;
            public bool HasExe;
            public bool IsEmpty;
            public long Bytes;
        }

        /// <summary>Evict old compile-cache entries. Returns the JSON body of "result". Idempotent,
        /// silent on per-directory failures, never throws for a missing cache directory.</summary>
        private static string PruneCache(string root, int keep, string currentSha)
        {
            if (keep < 1) keep = 1;
            string cacheRoot = Path.Combine(Path.Combine(root, "bin"), "cache");
            List<CacheEntry> entries = new List<CacheEntry>();
            if (Directory.Exists(cacheRoot))
            {
                foreach (string dir in Directory.GetDirectories(cacheRoot))
                {
                    string name = System.IO.Path.GetFileName(dir);
                    if (!ShaDirName.IsMatch(name)) continue;      // never touch foreign entries
                    CacheEntry e = new CacheEntry();
                    e.Name = name;
                    e.Path = dir;
                    e.MTimeUtc = Directory.GetLastWriteTimeUtc(dir);
                    string exe = System.IO.Path.Combine(dir, "ZwcHelper.exe");
                    e.HasExe = File.Exists(exe);
                    string[] files = Directory.GetFiles(dir);
                    e.IsEmpty = files.Length == 0 && Directory.GetDirectories(dir).Length == 0;
                    long bytes = 0;
                    try
                    {
                        foreach (string f in files) { try { bytes += new FileInfo(f).Length; } catch (Exception) { } }
                    }
                    catch (Exception) { }
                    e.Bytes = bytes;
                    entries.Add(e);
                }
            }
            int before = entries.Count;
            int emptyBefore = 0;
            foreach (CacheEntry e in entries) if (e.IsEmpty) emptyBefore++;

            // Directories a LIVE process is running from: a daemon (or a one-shot helper) keeps its
            // exe open inside bin\cache\<sha>\, so deleting that directory can only fail. Detect them
            // up front and put them in the keep set, instead of relying on a failed delete.
            HashSet<string> running = RunningCacheDirs(cacheRoot);

            // keep set: the N newest USEFUL entries, plus the entry the current source hash maps to,
            // plus everything that is currently executing. Empty directories do not get a keep slot -
            // they are garbage by definition and would otherwise push a real binary out of the
            // window (found by the sandbox test: two empty dirs occupied keep slots and two live
            // helper builds were evicted instead).
            entries.Sort(delegate(CacheEntry a, CacheEntry b) { return b.MTimeUtc.CompareTo(a.MTimeUtc); });
            HashSet<string> keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int picked = 0;
            foreach (CacheEntry e in entries)
            {
                if (picked >= keep) break;
                if (e.IsEmpty) continue;
                keepSet.Add(e.Name);
                picked++;
            }
            if (!string.IsNullOrEmpty(currentSha)) keepSet.Add(currentSha);
            foreach (string r in running) keepSet.Add(r);

            DateTime emptyCutoff = DateTime.UtcNow.AddSeconds(-EmptyDirGraceSeconds);
            int removed = 0, kept = 0, failed = 0, emptyRemoved = 0;
            long freedBytes = 0;
            List<string> deleted = new List<string>();
            List<string> deletedEmpty = new List<string>();
            List<string> skippedRunning = new List<string>();
            List<object> failures = new List<object>();
            foreach (CacheEntry e in entries)
            {
                if (running.Contains(e.Name))
                {
                    skippedRunning.Add(e.Name);
                    kept++;
                    continue;
                }
                if (!string.IsNullOrEmpty(currentSha) && string.Equals(e.Name, currentSha, StringComparison.OrdinalIgnoreCase))
                {
                    kept++;
                    continue;
                }
                // An empty directory is deleted once it is old enough (all 33 failed-compile leftovers
                // are days old); a freshly created one may be a compile that is still running, so it
                // gets the grace window. A non-empty entry is deleted only when it fell out of the
                // keep window.
                bool deleteIt = e.IsEmpty ? (e.MTimeUtc < emptyCutoff) : !keepSet.Contains(e.Name);
                if (!deleteIt) { kept++; continue; }
                try
                {
                    Directory.Delete(e.Path, true);
                    removed++;
                    freedBytes += e.Bytes;
                    deleted.Add(e.Name);
                    if (e.IsEmpty) { emptyRemoved++; deletedEmpty.Add(e.Name); }
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failures.Count < 20)
                    {
                        failures.Add("{\"dir\":\"" + JsonEscape(e.Name) + "\",\"message\":\"" + JsonEscape(ex.Message) + "\"}");
                    }
                }
            }
            int after = 0, afterSha = 0;
            try
            {
                if (Directory.Exists(cacheRoot))
                {
                    foreach (string d in Directory.GetDirectories(cacheRoot))
                    {
                        after++;
                        if (ShaDirName.IsMatch(System.IO.Path.GetFileName(d))) afterSha++;
                    }
                }
            }
            catch (Exception) { }

            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"cacheDir\":\"").Append(JsonEscape(cacheRoot)).Append("\",");
            sb.Append("\"keep\":").Append(keep).Append(',');
            sb.Append("\"before\":").Append(before).Append(',');
            sb.Append("\"after\":").Append(after).Append(',');
            sb.Append("\"afterSha\":").Append(afterSha).Append(',');
            sb.Append("\"removed\":").Append(removed).Append(',');
            sb.Append("\"emptyDirsBefore\":").Append(emptyBefore).Append(',');
            sb.Append("\"emptyRemoved\":").Append(emptyRemoved).Append(',');
            sb.Append("\"kept\":").Append(kept).Append(',');
            sb.Append("\"failed\":").Append(failed).Append(',');
            sb.Append("\"freedBytes\":").Append(freedBytes).Append(',');
            sb.Append("\"freedMB\":").Append((freedBytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"currentSha\":");
            if (string.IsNullOrEmpty(currentSha)) sb.Append("null"); else sb.Append('"').Append(JsonEscape(currentSha)).Append('"');
            sb.Append(',');
            sb.Append("\"deleted\":");
            AppendStringArray(sb, deleted);
            sb.Append(',');
            sb.Append("\"deletedEmpty\":");
            AppendStringArray(sb, deletedEmpty);
            sb.Append(',');
            sb.Append("\"skippedRunning\":");
            AppendStringArray(sb, skippedRunning);
            sb.Append(',');
            sb.Append("\"failedDirs\":[");
            for (int i = 0; i < failures.Count; i++) { if (i > 0) sb.Append(','); sb.Append((string)failures[i]); }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendStringArray(StringBuilder sb, List<string> values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(JsonEscape(values[i])).Append('"');
            }
            sb.Append(']');
        }

        /// <summary>Names of the bin\cache\&lt;sha12&gt; directories that live ZwcHelper processes are
        /// running from. Reading MainModule can fail across bitness/permissions - that only means the
        /// entry may be attempted for deletion and reported under failedDirs instead of
        /// skippedRunning, never that a live daemon gets hurt.</summary>
        private static HashSet<string> RunningCacheDirs(string cacheRoot)
        {
            HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs;
            try { procs = Process.GetProcessesByName("ZwcHelper"); }
            catch (Exception) { return running; }
            foreach (Process p in procs)
            {
                try
                {
                    string path = null;
                    try { path = p.MainModule.FileName; }
                    catch (Exception) { path = null; }
                    if (string.IsNullOrEmpty(path)) continue;
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(dir)) continue;
                    string parent = System.IO.Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent)) continue;
                    if (!string.Equals(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar), cacheRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                    string name = System.IO.Path.GetFileName(dir);
                    if (ShaDirName.IsMatch(name)) running.Add(name);
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            return running;
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
            "slow", "keeponfail", "nolegend", "scalebar", "pointno", "strict", "continue", "plan", "noundomark", "reveal", "sweephidden", "noreveal", "tray", "geom", "forcelispfile", "forcelispinject"
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

            // -App default: "any" = the helper resolves from the running instances (single family), or
            // from the drawing that is already open (both families). -App zwcad|autocad still forces one.
            string appKind = opts.ContainsKey("app") ? opts["app"] : "any";
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
