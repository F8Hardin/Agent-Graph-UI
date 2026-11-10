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

        /// <summary>
        /// A memory sequence that is mixed into the run when <c>memoryTree</c> is on, so the session memory tree can be seen
        /// growing offline: a search with six hits across four folders, three reads of some of them, and a note written.
        /// It overlaps the robinhood and restart calls the way concurrent calls do on a real run (each has its own run id),
        /// and it sits in the gaps of the prototype's script rather than moving any of its events.
        ///
        /// Kept apart from <see cref="Script"/> on purpose: that list is pinned as "the prototype's script" by tests and by
        /// the replay-order check, and a run without the flag plays it exactly as before. Rejected: editing Script itself.
        /// Output shapes are the real ones EVA logs: the python repr of an MCP result wrapping the vault tool's JSON.
        /// </summary>
        public static readonly IReadOnlyList<ScriptStep> MemoryScript = new[]
        {
            new ScriptStep(5000, AgentEvent.ToolStart("memory__vault_search",
                "{\"query\": \"trading notes, positions review\", \"limit\": 8}", "m1")),
            new ScriptStep(5600, AgentEvent.ToolEnd("memory__vault_search", SearchOutput(
                "trading notes, positions review",
                "Trading/2026-10-29.md", "Trading/Playbook.md", "Projects/Trading Bot.md",
                "Projects/Eva AI.md", "Engineering/Roadmap.md", "Eva/eva.md"), "m1")),
            new ScriptStep(5700, AgentEvent.ToolStart("memory__vault_read",
                "{\"path\": \"Projects/Trading Bot.md\"}", "m2")),
            new ScriptStep(6050, AgentEvent.ToolEnd("memory__vault_read", ReadOutput(
                "Projects/Trading Bot.md", "Trading Bot", "project"), "m2")),
            new ScriptStep(7000, AgentEvent.ToolStart("memory__vault_read",
                "{\"path\": \"Trading/Playbook.md\"}", "m3")),
            new ScriptStep(7400, AgentEvent.ToolEnd("memory__vault_read", ReadOutput(
                "Trading/Playbook.md", "Playbook", "reference"), "m3")),
            new ScriptStep(8300, AgentEvent.ToolStart("memory__vault_read",
                "{\"path\": \"Engineering/Roadmap.md\"}", "m4")),
            new ScriptStep(8800, AgentEvent.ToolEnd("memory__vault_read", ReadOutput(
                "Engineering/Roadmap.md", "Roadmap", "moc"), "m4")),
            new ScriptStep(9250, AgentEvent.ToolStart("memory__vault_write_note",
                "{\"path\": \"Trading/Reviews/2026-11-02 Positions Review.md\", \"content\": \"# Positions review\"}", "m5")),
            new ScriptStep(9450, AgentEvent.ToolEnd("memory__vault_write_note", WrittenOutput(
                "Trading/Reviews/2026-11-02 Positions Review.md"), "m5"))
        };

        private static string Wrap(string json) => "content=[{'type': 'text', 'text': '" + json + "'}]";

        private static string SearchOutput(string query, params string[] paths)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("{\"query\": \"").Append(query).Append("\", \"count\": ").Append(paths.Length).Append(", \"results\": [");
            for (int i = 0; i < paths.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                string file = paths[i].Substring(paths[i].LastIndexOf('/') + 1);
                sb.Append("{\"path\": \"").Append(paths[i]).Append("\", \"title\": \"").Append(file.Substring(0, file.Length - 3))
                  .Append("\", \"score\": ").Append((0.91 - i * 0.07).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("]}");
            return Wrap(sb.ToString());
        }

        private static string ReadOutput(string path, string title, string type) =>
            Wrap("{\"path\": \"" + path + "\", \"title\": \"" + title + "\", \"frontmatter\": {\"type\": \"" + type +
                 "\", \"owner\": \"eva\", \"status\": \"active\", \"created\": \"2026-08-04\"}, \"content\": \"# " + title + "\"}");

        private static string WrittenOutput(string path) => Wrap("{\"path\": \"" + path + "\", \"written\": true}");

        private sealed class Run
        {
            public readonly object Gate = new object();
            public CancellationTokenSource Cts;
            public bool Ended;

            // Pumped mode only: the clock reading at the first Pump after the send, and the next step to emit.
            public double StartSeconds = double.NaN;
            public int Next;
        }

        private readonly double _speed;
        private readonly IReadOnlyList<ScriptStep> _steps;
        private readonly bool _pumped;
        private readonly object _gate = new object();
        private Run _current;

        public override IReadOnlyCollection<string> Capabilities => CapabilityList;

        /// <param name="speed">Playback speed multiplier. 1 is the prototype's real timing.</param>
        /// <param name="memoryTree">Mix <see cref="MemoryScript"/> into the run. Off by default, so the run is the prototype's script unless asked.</param>
        /// <param name="pumped">
        /// Play without any thread: <see cref="SendAsync"/> only queues the run and the host drives it by calling
        /// <see cref="Pump"/> every frame. Needed where there is no thread pool (Unity WebGL: Task.Run and Task.Delay
        /// never complete there). Off by default, so desktop keeps the threaded schedule it always had.
        /// Rejected: a MonoBehaviour coroutine in App that walks <see cref="Script"/>. It would either pull UnityEngine
        /// into Transport or duplicate the script, merge and speed handling in App.
        /// </param>
        public ReplayTransport(double speed = 1.0, bool memoryTree = false, bool pumped = false)
        {
            _speed = speed > 0 ? speed : 1.0;
            _pumped = pumped;
            _steps = memoryTree ? Merge(Script, MemoryScript) : Script;
        }

        /// <summary>Both lists in time order. A tie keeps the first list's event first, so the prototype's script never loses its place.</summary>
        public static IReadOnlyList<ScriptStep> Merge(IReadOnlyList<ScriptStep> first, IReadOnlyList<ScriptStep> second)
        {
            List<ScriptStep> merged = new List<ScriptStep>(first.Count + second.Count);
            int a = 0, b = 0;
            while (a < first.Count || b < second.Count)
            {
                bool takeFirst = b >= second.Count || (a < first.Count && first[a].AtMs <= second[b].AtMs);
                merged.Add(takeFirst ? first[a++] : second[b++]);
            }
            return merged;
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

            if (!_pumped)
            {
                // Task.Run so the schedule never sits on the caller's (main) thread or its sync context.
                Task.Run(() => PlayAsync(run));
            }
            return Task.FromResult(ReplayJobId);
        }

        /// <summary>
        /// Pumped mode: emit every scripted step whose time has come, then the clean end once the script is done.
        /// Call once per frame from the main thread with a monotonic clock in seconds. The run's time zero is the
        /// first call after the send, so the schedule is the threaded one shifted by at most a frame. A no-op in the
        /// default threaded mode, or with no run in flight.
        /// </summary>
        /// <returns>How many events were emitted by this call.</returns>
        public int Pump(double nowSeconds)
        {
            if (!_pumped) return 0;
            Run run;
            lock (_gate)
            {
                run = _current;
            }
            if (run == null) return 0;

            int emitted = 0;
            lock (run.Gate)
            {
                if (run.Ended) return 0;
                if (run.Cts.IsCancellationRequested)
                {
                    // The caller's token was cancelled: end quietly, as the threaded path does.
                    run.Ended = true;
                    run.Cts.Dispose();
                    return 0;
                }
                if (double.IsNaN(run.StartSeconds)) run.StartSeconds = nowSeconds;
                double elapsedMs = (nowSeconds - run.StartSeconds) * 1000.0 * _speed;
                while (run.Next < _steps.Count && _steps[run.Next].AtMs <= elapsedMs)
                {
                    Emit(_steps[run.Next++].Event);
                    emitted++;
                }
                if (run.Next >= _steps.Count)
                {
                    Emit(AgentEvent.RunEnded());
                    emitted++;
                    run.Ended = true;
                    run.Cts.Dispose();
                }
            }
            return emitted;
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
                foreach (ScriptStep step in _steps)
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
