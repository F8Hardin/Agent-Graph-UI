using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    public enum InventoryPhase
    {
        /// <summary>No source. The graph is whatever it was built with.</summary>
        Unavailable,

        /// <summary>A fetch is in flight.</summary>
        Loading,

        /// <summary>The last fetch worked. <see cref="InventoryController.Snapshot"/> is current.</summary>
        Ready,

        /// <summary>The last fetch failed. <see cref="InventoryController.Error"/> says why; the graph and the last snapshot are kept.</summary>
        Failed
    }

    /// <summary>
    /// Keeps the graph and the Settings list in step with what the agent can actually use, and sends the user's pause
    /// toggles back to it. Same hand-off as <see cref="ModelController"/> (STANDARDS section 2 and 4): requests run on the
    /// thread pool, their results are queued, and the main thread applies them in <see cref="Pump"/>, so nothing here
    /// waits for the network or needs a synchronisation context.
    ///
    /// The server owns the truth (STANDARDS section 4). A toggle is shown at once (an optimistic overlay on the last
    /// reading, with a per-item pending flag) but only the agent's own answer to the request is believed: success replaces
    /// the reading, failure drops the overlay, which IS the revert, and says why. Requests are serialised: the agent
    /// applies a change under a gate that can wait for running streams, so a second POST racing the first would only queue
    /// behind it on the server anyway. Toggles made meanwhile are coalesced (last write per item wins) into the next one.
    /// A periodic reading never overwrites an item that is still pending, because the overlay sits on top of every
    /// reading, and a reading that began before a toggle landed is thrown away and asked for again.
    ///
    /// The graph follows the live set (<see cref="GraphModel.Reconcile"/>): only the difference is added and removed, so a
    /// toggle does not reset the force layout, the nodes the user dragged, or the session memory tree. Only the very first
    /// reading builds the graph in one go. The change is held back while a run is in flight, so nothing is pulled out from
    /// under a tool call that is lighting it.
    /// </summary>
    public sealed class InventoryController : IDisposable
    {
        /// <summary>The slowest poll while idle. A run going idle asks for a fetch right away instead (see <see cref="RequestRefresh"/>).</summary>
        public const double IdleRefreshSeconds = 60.0;

        private readonly IInventorySource _source;
        private readonly GraphModel _graph;
        private readonly Func<double> _clock;
        private readonly ConcurrentQueue<Action> _done = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly TaskScheduler _scheduler;

        // Pause requests. _queued* are waiting, _sent* are in the request now, _opt* are both together: the overlay.
        private readonly Dictionary<string, bool> _queuedServers = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _queuedTools = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _optServers = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _optTools = new Dictionary<string, bool>();
        private Dictionary<string, bool> _sentServers;
        private Dictionary<string, bool> _sentTools;
        private bool _setInFlight;
        private int _epoch; // bumps whenever a pause request has been answered

        private IReadOnlyList<ServerInfo> _display = Array.Empty<ServerInfo>();
        private bool _inFlight;
        private int _fetchEpoch;
        private bool _wanted;
        private double _lastStart = double.NegativeInfinity;
        private InventorySnapshot _pending;
        private bool _built;
        private string _appliedSignature; // null until the first reading is applied, so a prebuilt (sample) graph is reconciled once

        public InventoryPhase Phase { get; private set; }

        /// <summary>The last good reading, applied or not. Null until one arrives.</summary>
        public InventorySnapshot Snapshot { get; private set; }

        /// <summary>
        /// Every server, with the user's pending toggles already applied: connected, paused and offline ones, in the agent's
        /// order. This is what the Settings list draws. Empty until a reading arrives.
        /// </summary>
        public IReadOnlyList<ServerInfo> Servers => _display;

        /// <summary>Servers the last good reading left out as not connected, with their error text. Empty until one arrives.</summary>
        public IReadOnlyList<OmittedServer> Omitted => Snapshot != null ? Snapshot.Omitted : Array.Empty<OmittedServer>();

        /// <summary>Why the last fetch failed. Null unless <see cref="Phase"/> is Failed.</summary>
        public string Error { get; private set; }

        /// <summary>Why the last toggle did not stick (or part of it was not recognised). Null otherwise. Cleared by the next toggle.</summary>
        public string ToggleError { get; private set; }

        /// <summary>Counts up whenever <see cref="Servers"/>, a pending flag or <see cref="ToggleError"/> changed, so a list can rebuild only then.</summary>
        public int ListVersion { get; private set; }

        /// <summary>How many times the graph was built from scratch. Pause and reconnect changes do not count: they are incremental.</summary>
        public int RebuildCount { get; private set; }

        public bool IsAvailable => _source != null;

        public bool HasPending => _optServers.Count > 0 || _optTools.Count > 0;

        public event Action Changed;

        /// <param name="source">Null when there is nothing to ask. The graph then keeps whatever it was built with.</param>
        /// <param name="clockSeconds">Injectable so tests need no sleeping. Defaults to a monotonic clock.</param>
        /// <param name="asyncMode">CallerContext keeps every continuation on the constructing thread's context (WebGL). Construct on the main thread.</param>
        public InventoryController(IInventorySource source, GraphModel graph, Func<double> clockSeconds = null,
            AsyncMode asyncMode = AsyncMode.ThreadPool)
        {
            _scheduler = AsyncModes.SchedulerFor(asyncMode);
            _source = source;
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _clock = clockSeconds ?? DefaultClock;
            Phase = source == null ? InventoryPhase.Unavailable : InventoryPhase.Ready;
        }

        private static double DefaultClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // ------------------------------------------------------------------ reading

        /// <summary>Start a fetch now. If one is already running, a second is queued behind it instead of racing it.</summary>
        public void Refresh()
        {
            if (_source == null) return;
            if (_inFlight)
            {
                _wanted = true;
                return;
            }
            _wanted = false;
            _inFlight = true;
            _fetchEpoch = _epoch;
            _lastStart = _clock();
            Phase = InventoryPhase.Loading;
            Changed?.Invoke();

            Task<InventorySnapshot> task;
            try
            {
                task = _source.FetchAsync(_cts.Token);
            }
            catch (Exception e)
            {
                task = Task.FromException<InventorySnapshot>(e);
            }
            Hand(task, () => OnFetched(task));
        }

        /// <summary>A run went idle, so tools may have appeared. The fetch starts at the next <see cref="Update"/> that finds the agent idle.</summary>
        public void RequestRefresh() => _wanted = true;

        /// <summary>
        /// Call every frame on the main thread. Applies a finished fetch, then starts one when it is due: at the first
        /// idle frame, right after <see cref="RequestRefresh"/>, and at most every <see cref="IdleRefreshSeconds"/>
        /// after that. No polling while a run is in flight.
        /// </summary>
        public void Update(bool runInFlight)
        {
            Pump(runInFlight);
            if (_source == null || runInFlight || _inFlight) return;
            if (_wanted || _clock() - _lastStart >= IdleRefreshSeconds) Refresh();
        }

        /// <summary>Apply finished requests on the main thread. Returns true if anything changed state.</summary>
        public bool Pump(bool runInFlight = false)
        {
            bool any = false;
            while (_done.TryDequeue(out Action apply))
            {
                apply();
                any = true;
            }
            if (_pending != null && !runInFlight)
            {
                Apply(_pending);
                _pending = null;
                any = true;
            }
            return any;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        // ------------------------------------------------------------------ toggles

        /// <summary>The server as the list shows it now (pending toggles applied), or null when there is no such server.</summary>
        public ServerInfo FindServer(string name)
        {
            for (int i = 0; i < _display.Count; i++)
            {
                if (_display[i].Name == name) return _display[i];
            }
            return null;
        }

        public bool IsServerPending(string name) => name != null && _optServers.ContainsKey(name);

        /// <summary>Pending state of a tool, by wire name (<see cref="ToolInfo.WireName"/>).</summary>
        public bool IsToolPending(string wireName) => wireName != null && _optTools.ContainsKey(wireName);

        /// <summary>
        /// Pause or resume a server. Shown at once, confirmed or reverted when the agent answers. Ignored for a server
        /// that is not connected (there is nothing to toggle) or unknown, and when it already is that way.
        /// </summary>
        public void SetServerPaused(string name, bool paused)
        {
            if (_source == null || string.IsNullOrEmpty(name)) return;
            ServerInfo s = FindServer(name);
            if (s == null || !s.IsConnected || s.Paused == paused) return;
            _queuedServers[name] = paused;
            _optServers[name] = paused;
            Touched();
        }

        /// <summary>Pause or resume one tool, by the wire name the agent listed it under. Same rules as <see cref="SetServerPaused"/>.</summary>
        public void SetToolPaused(string wireName, bool paused)
        {
            if (_source == null || string.IsNullOrEmpty(wireName)) return;
            ServerInfo owner = null;
            ToolInfo tool = null;
            for (int i = 0; i < _display.Count && tool == null; i++)
            {
                for (int t = 0; t < _display[i].ToolDetails.Count; t++)
                {
                    if (_display[i].ToolDetails[t].WireName != wireName) continue;
                    owner = _display[i];
                    tool = _display[i].ToolDetails[t];
                    break;
                }
            }
            if (tool == null || !owner.IsConnected || tool.Paused == paused) return;
            _queuedTools[wireName] = paused;
            _optTools[wireName] = paused;
            Touched();
        }

        private void Touched()
        {
            ToggleError = null;
            RebuildDisplay();
            Changed?.Invoke();
            SendNext();
        }

        private void SendNext()
        {
            if (_setInFlight || (_queuedServers.Count == 0 && _queuedTools.Count == 0)) return;
            _sentServers = new Dictionary<string, bool>(_queuedServers);
            _sentTools = new Dictionary<string, bool>(_queuedTools);
            _queuedServers.Clear();
            _queuedTools.Clear();
            _setInFlight = true;

            Task<InventorySnapshot> task;
            try
            {
                task = _source.SetPausedAsync(_sentServers, _sentTools, _cts.Token);
            }
            catch (Exception e)
            {
                task = Task.FromException<InventorySnapshot>(e);
            }
            Hand(task, () => OnSet(task));
        }

        private void OnSet(Task<InventorySnapshot> t)
        {
            _setInFlight = false;
            _epoch++;
            Dictionary<string, bool> sentServers = _sentServers;
            Dictionary<string, bool> sentTools = _sentTools;
            _sentServers = null;
            _sentTools = null;

            // Whatever was sent is settled either way. An item the user changed AGAIN meanwhile is queued, and stays pending.
            foreach (string k in sentServers.Keys)
            {
                if (!_queuedServers.ContainsKey(k)) _optServers.Remove(k);
            }
            foreach (string k in sentTools.Keys)
            {
                if (!_queuedTools.ContainsKey(k)) _optTools.Remove(k);
            }

            if (t.IsFaulted || t.IsCanceled || t.Result == null)
            {
                // Dropping the overlay is the revert: the list falls back to the last reading, which is the agent's truth.
                ToggleError = "Change not applied: " + Describe(t, "EVA did not accept it.");
                _wanted = true; // a failed request may still have applied part of itself; look
            }
            else
            {
                InventorySnapshot snap = t.Result;
                Snapshot = snap;
                Error = null;
                Phase = InventoryPhase.Ready;
                _pending = snap; // the graph follows in Pump, which knows whether a run is in flight
                ToggleError = snap.Unknown.Count > 0 ? "EVA did not recognise: " + string.Join(", ", snap.Unknown) : null;
            }
            RebuildDisplay();
            Changed?.Invoke();
            SendNext();
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Queue a finished task's follow-up for the main thread. A task that is already complete (the in-memory source
        /// returns only those) is queued directly: a continuation would go through the thread pool, which the WebGL player
        /// does not have.
        /// </summary>
        private void Hand(Task task, Action apply)
        {
            if (task.IsCompleted)
            {
                _done.Enqueue(apply);
                return;
            }
            task.ContinueWith(
                _ => _done.Enqueue(apply),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, _scheduler);
        }

        private void OnFetched(Task<InventorySnapshot> t)
        {
            _inFlight = false;
            if (t.IsFaulted || t.IsCanceled || t.Result == null)
            {
                Phase = InventoryPhase.Failed;
                Error = Describe(t, "Could not read EVA's tool list.");
                Changed?.Invoke();
                return;
            }
            if (_fetchEpoch != _epoch)
            {
                // A toggle was answered while this read was on the wire, so it may predate it. Its newer twin is the truth.
                Phase = InventoryPhase.Ready;
                _wanted = true;
                Changed?.Invoke();
                return;
            }
            bool changed = Snapshot == null || Snapshot.Fingerprint != t.Result.Fingerprint;
            Snapshot = t.Result;
            Error = null;
            Phase = InventoryPhase.Ready;
            _pending = t.Result; // applied by Pump, which knows whether a run is in flight
            if (changed) RebuildDisplay();
            Changed?.Invoke();
        }

        /// <summary>The last reading with the pending toggles laid over it.</summary>
        private void RebuildDisplay()
        {
            ListVersion++;
            if (Snapshot == null)
            {
                _display = Array.Empty<ServerInfo>();
                return;
            }
            List<ServerInfo> list = new List<ServerInfo>(Snapshot.All.Count);
            for (int i = 0; i < Snapshot.All.Count; i++)
            {
                ServerInfo s = Snapshot.All[i];
                if (_optServers.TryGetValue(s.Name, out bool sp)) s = s.WithPaused(sp);
                if (_optTools.Count > 0)
                {
                    foreach (KeyValuePair<string, bool> kv in _optTools) s = s.WithToolPaused(kv.Key, kv.Value);
                }
                list.Add(s);
            }
            _display = list;
        }

        private void Apply(InventorySnapshot snap)
        {
            if (_built || _graph.HasInventoryNodes)
            {
                if (snap.Signature != _appliedSignature) _graph.Reconcile(snap.All);
            }
            else if (snap.Signature != InventorySnapshot.Empty.Signature)
            {
                _graph.Build(snap.All);
                RebuildCount++;
                _built = true;
            }
            _appliedSignature = snap.Signature;
            Changed?.Invoke();
        }

        private static string Describe(Task t, string fallback)
        {
            Exception e = t.Exception != null ? t.Exception.GetBaseException() : null;
            return e == null || string.IsNullOrWhiteSpace(e.Message) ? fallback : e.Message;
        }
    }
}
