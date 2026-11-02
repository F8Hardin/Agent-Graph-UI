using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>One scripted event and the millisecond offset, from the start of the run, at which it fires.</summary>
    public readonly struct ScriptStep
    {
        public int AtMs { get; }
        public AgentEvent Event { get; }

        public ScriptStep(int atMs, AgentEvent ev)
        {
            AtMs = atMs;
            Event = ev;
        }
    }

    /// <summary>
    /// Plays a scripted run with realistic delays, so the app runs with no server, no EVA and no
    /// network. The script is the prototype's SCRIPT, ported verbatim: same tools, same run ids,
    /// same timings, including the robinhood failure, the run_command restart and the retry.
    /// </summary>
    public sealed class ReplayTransport : AgentTransportBase, IDisposable
    {
        public const string ReplayJobId = "job_7f3a";

        private static readonly string[] CapabilityList =
        {
            AgentCapability.Chat, AgentCapability.Stream, AgentCapability.Tools, AgentCapability.Cancel
        };

        /// <summary>The prototype's SCRIPT. Exposed so tests can feed the reducer without any delay.</summary>
        public static readonly IReadOnlyList<ScriptStep> Script = new[]
        {
            new ScriptStep(0, AgentEvent.Meta("qwen3.5-9b", 0.4f, ReplayJobId)),
            new ScriptStep(150, AgentEvent.ChatModelStart()),
            new ScriptStep(320, AgentEvent.Thought("He wants last week’s trading notes checked against the open positions. ")),
            new ScriptStep(1100, AgentEvent.Thought("Memory first — find the note, then read it.")),
            new ScriptStep(1900, AgentEvent.ToolStart("memory__vault_search",
                "{\n  \"query\": \"trading notes week of Oct 27\",\n  \"limit\": 8\n}", "r1")),
            new ScriptStep(3100, AgentEvent.ToolEnd("memory__vault_search", "4 notes", "r1")),
            new ScriptStep(3400, AgentEvent.ToolStart("memory__vault_read",
                "{\n  \"path\": \"Trading/2026-10-29.md\"\n}", "r2")),
            new ScriptStep(4500, AgentEvent.ToolEnd("memory__vault_read", "2.1 KB", "r2")),
            new ScriptStep(4800, AgentEvent.ToolStart("robinhood__get_equity_positions",
                "{\n  \"account_number\": \"512149097\"\n}", "r3")),
            new ScriptStep(6100, AgentEvent.ToolError("robinhood__get_equity_positions",
                "MCP wedged — Recv-Q 1459", "r3")),
            new ScriptStep(6500, AgentEvent.ToolStart("fate_ubuntu_server__run_command",
                "{\n  \"command\": \"systemctl --user restart robinhood-mcp\"\n}", "r4")),
            new ScriptStep(7700, AgentEvent.ToolEnd("fate_ubuntu_server__run_command", "ok", "r4")),
            new ScriptStep(8000, AgentEvent.ToolStart("robinhood__get_equity_positions",
                "{\n  \"account_number\": \"512149097\"\n}", "r5")),
            new ScriptStep(9200, AgentEvent.ToolEnd("robinhood__get_equity_positions", "4 positions", "r5")),
            new ScriptStep(9500, AgentEvent.ToolStart("memory__vault_append",
                "{\n  \"path\": \"Trading/2026-11-02.md\",\n  \"content\": \"positions reconciled\"\n}", "r6")),
            new ScriptStep(10400, AgentEvent.ToolEnd("memory__vault_append", "written", "r6")),
            new ScriptStep(10700, AgentEvent.Content("Your Oct 29 note planned an ENPH exit on a time stop. ")),
            new ScriptStep(11400, AgentEvent.Content("The broker MCP was wedged, so I restarted it before reading — ")),
            new ScriptStep(12100, AgentEvent.Content("positions now agree with the note on OKLO and NNE, but ENPH is still open.")),
            new ScriptStep(13000, AgentEvent.LlmEnd())
        };

        private sealed class Run
        {
            public readonly object Gate = new object();
            public CancellationTokenSource Cts;
            public bool Ended;
        }

        private readonly double _speed;
        private readonly object _gate = new object();
        private Run _current;

        public override IReadOnlyCollection<string> Capabilities => CapabilityList;

        /// <param name="speed">Playback speed multiplier. 1 is the prototype's real timing.</param>
        public ReplayTransport(double speed = 1.0)
        {
            _speed = speed > 0 ? speed : 1.0;
        }

        public override Task<string> SendAsync(string chatId, string prompt, CancellationToken ct)
        {
            Run run = new Run { Cts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
            Run previous;
            lock (_gate)
            {
                previous = _current;
                _current = run;
            }
            if (previous != null)
            {
                EndRun(previous, emitStopped: false);
            }

            // Task.Run so the schedule never sits on the caller's (main) thread or its sync context.
            Task.Run(() => PlayAsync(run));
            return Task.FromResult(ReplayJobId);
        }

        public override Task StopAsync(string jobId)
        {
            Run run;
            lock (_gate)
            {
                run = _current;
            }
            if (run != null)
            {
                // EVA ends a stopped run with an error event reading "Stopped.".
                EndRun(run, emitStopped: true);
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Run run;
            lock (_gate)
            {
                run = _current;
                _current = null;
            }
            if (run != null) EndRun(run, emitStopped: false);
        }

        private void EndRun(Run run, bool emitStopped)
        {
            lock (run.Gate)
            {
                if (run.Ended) return;
                run.Ended = true;
                if (emitStopped) Emit(AgentEvent.Error("Stopped."));
            }
            try
            {
                run.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task PlayAsync(Run run)
        {
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                foreach (ScriptStep step in Script)
                {
                    double waitMs = step.AtMs / _speed - clock.Elapsed.TotalMilliseconds;
                    if (waitMs > 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(waitMs), run.Cts.Token).ConfigureAwait(false);
                    }
                    lock (run.Gate)
                    {
                        if (run.Ended) return;
                        Emit(step.Event);
                    }
                }
                lock (run.Gate)
                {
                    // Script finished and nobody stopped it: this is the clean end of the stream.
                    if (!run.Ended) Emit(AgentEvent.RunEnded());
                    run.Ended = true;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                run.Cts.Dispose();
            }
        }
    }
}
