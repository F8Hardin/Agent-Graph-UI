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
        /// <summary>No source (the offline replay). The graph is whatever it was built with.</summary>
        Unavailable,

        /// <summary>A fetch is in flight.</summary>
        Loading,

        /// <summary>The last fetch worked. <see cref="InventoryController.Snapshot"/> is current.</summary>
        Ready,

        /// <summary>The last fetch failed. <see cref="InventoryController.Error"/> says why; the graph and the last snapshot are kept.</summary>
        Failed
    }

    /// <summary>
    /// Keeps the graph in step with what the agent can actually reach. Same hand-off as <see cref="ModelController"/>
    /// (STANDARDS section 2 and 4): the fetch runs on the thread pool, its result is queued, and the main thread
    /// applies it in <see cref="Pump"/>, so nothing here waits for the network or needs a synchronisation context.
    ///
    /// The graph is rebuilt ONLY when the shape changed (<see cref="InventorySnapshot.Signature"/>). A rebuild resets
    /// the force layout and every node the user dragged into place, and clears the session memory tree, so a poll that
    /// found nothing new must cost nothing. A pause toggle is applied to the existing nodes in place. A rebuild is also
    /// held back while a run is in flight, so a poll cannot pull nodes out from under a tool call that is lighting them.
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
        private bool _inFlight;
        private bool _wanted;
        private double _lastStart = double.NegativeInfinity;
        private InventorySnapshot _pending;
        private string _appliedSignature = InventorySnapshot.Empty.Signature;

        public InventoryPhase Phase { get; private set; }

        /// <summary>The last good reading, applied or not. Null until one arrives.</summary>
        public InventorySnapshot Snapshot { get; private set; }

        /// <summary>Servers the last good reading left out, with their error text. Empty until one arrives.</summary>
        public IReadOnlyList<OmittedServer> Omitted => Snapshot != null ? Snapshot.Omitted : Array.Empty<OmittedServer>();

        /// <summary>Why the last fetch failed. Null unless <see cref="Phase"/> is Failed.</summary>
        public string Error { get; private set; }

        /// <summary>How many times the graph was rebuilt from a snapshot.</summary>
        public int RebuildCount { get; private set; }

        public bool IsAvailable => _source != null;

        public event Action Changed;

        /// <param name="source">Null for the offline replay, which keeps its sample graph.</param>
        /// <param name="clockSeconds">Injectable so tests need no sleeping. Defaults to a monotonic clock.</param>
        public InventoryController(IInventorySource source, GraphModel graph, Func<double> clockSeconds = null)
        {
            _source = source;
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _clock = clockSeconds ?? DefaultClock;
            Phase = source == null ? InventoryPhase.Unavailable : InventoryPhase.Ready;
        }

        private static double DefaultClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

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
            task.ContinueWith(
                t => _done.Enqueue(() => OnFetched(t)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

        /// <summary>Apply finished fetches on the main thread. Returns true if anything changed state.</summary>
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

        private void OnFetched(Task<InventorySnapshot> t)
        {
            _inFlight = false;
            if (t.IsFaulted || t.IsCanceled || t.Result == null)
            {
                Phase = InventoryPhase.Failed;
                Exception e = t.Exception != null ? t.Exception.GetBaseException() : null;
                Error = e == null || string.IsNullOrWhiteSpace(e.Message) ? "Could not read EVA's tool list." : e.Message;
                Changed?.Invoke();
                return;
            }
            Snapshot = t.Result;
            Error = null;
            Phase = InventoryPhase.Ready;
            _pending = t.Result; // applied by Pump, which knows whether a run is in flight
            Changed?.Invoke();
        }

        private void Apply(InventorySnapshot snap)
        {
            if (snap.Signature != _appliedSignature)
            {
                _appliedSignature = snap.Signature;
                RebuildCount++;
                _graph.Build(snap.Connected);
            }
            else
            {
                _graph.ApplyPaused(snap.Connected);
            }
            Changed?.Invoke();
        }
    }
}
