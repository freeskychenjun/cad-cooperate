// Daemon.cs - M3-E1 performance base: resident daemon, pipe protocol, request dispatcher.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). The daemon
// stays resident, reads one framed request per pipe connection and answers through the same
// Commands entry points as the one-shot mode. No logic, visibility, idle timing or error
// text was changed; the file-level "Entry point" banner stays with Program.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZwcHelper
{
    // ======================================================================================
    // M3-E1 performance base: resident daemon + pipe protocol + request dispatcher
    // ======================================================================================
    internal static class Daemon
    {
        public static int Run(string pipeName, int idleMs)
        {
            if (idleMs <= 0) idleMs = 20 * 60 * 1000;
            int bindFailures = 0;
            while (true)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    Task wait = pipe.WaitForConnectionAsync();
                    if (!wait.Wait(idleMs)) return 0;            // idle -> self destruct
                    bindFailures = 0;
                }
                catch (Exception)
                {
                    try { if (pipe != null) pipe.Dispose(); } catch (Exception) { }
                    bindFailures++;
                    if (bindFailures >= 10) return 0;   // ~2 s: another daemon owns the pipe   // another daemon owns this pipe: stop piling up
                    Thread.Sleep(200);
                    continue;
                }

                string respJson = null;
                try
                {
                    string reqJson = PipeIo.ReadMessage(pipe);
                    if (reqJson == null)
                    {
                        respJson = "{\"ok\":false,\"cmd\":\"\",\"error\":{\"code\":\"BAD_REQUEST\",\"message\":\"empty or truncated pipe message\"}}";
                    }
                    else
                    {
                        // P2-5.4: route the two pipe-level commands by the request's top-level "cmd"
                        // field, not by searching the raw text. The old IndexOf("\"ping\"") match sent
                        // ANY request that merely mentioned ping into the ping branch - e.g.
                        // {"cmd":"query","args":{"text":"ping"}} came back as {"cmd":"ping"} instead
                        // of a query result (verified against the live daemon before this fix).
                        // Parsing costs nothing here: this daemon serves a handful of requests per
                        // minute, not a hot loop. A payload that does not parse falls through
                        // unchanged so the normal dispatcher answers BAD_ARGS exactly as before -
                        // a malformed request must never be refused by this branch.
                        string pipeCmd = null;
                        try
                        {
                            JMap reqMap = Json.Parse(reqJson) as JMap;
                            if (reqMap != null) pipeCmd = reqMap.Str("cmd", "");
                        }
                        catch (Exception) { pipeCmd = null; }

                        if (pipeCmd != null && string.Equals(pipeCmd, "daemon-stop", StringComparison.Ordinal))
                        {
                            // no WaitForPipeDrain here: a client that never reads its answer would
                            // block the daemon forever inside the pipe (measured: the daemon stayed
                            // alive after "stop" and then refused every later connection)
                            PipeIo.WriteMessage(pipe, "{\"ok\":true,\"cmd\":\"daemon-stop\",\"result\":{\"stopped\":true},\"elapsedMs\":0}");
                            try { pipe.Dispose(); } catch (Exception) { }
                            return 0;
                        }
                        else if (pipeCmd != null && string.Equals(pipeCmd, "ping", StringComparison.Ordinal))
                        {
                            PipeIo.WriteMessage(pipe, "{\"ok\":true,\"cmd\":\"ping\",\"result\":{\"pid\":" + Process.GetCurrentProcess().Id + "},\"elapsedMs\":0}");
                            respJson = null;
                        }
                        else
                        {
                            respJson = Program.HandleRequestText(reqJson);
                        }
                    }
                    if (respJson != null) PipeIo.WriteMessage(pipe, respJson);
                }
                catch (Exception e)
                {
                    try { PipeIo.WriteMessage(pipe, "{\"ok\":false,\"error\":{\"code\":\"DAEMON_ERROR\",\"message\":" + Json.Write(e.Message) + "}}"); }
                    catch (Exception) { }
                }
                finally
                {
                    try { pipe.Dispose(); } catch (Exception) { }
                }
            }
        }
    }
}
