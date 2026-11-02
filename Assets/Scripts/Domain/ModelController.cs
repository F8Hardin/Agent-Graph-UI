using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    public enum ModelControlPhase
    {
        /// <summary>The agent has no model capability. The control degrades to a read-only readout.</summary>
        Unavailable,

        /// <summary>Fetching the model list.</summary>
        Loading,

        Ready,

        /// <summary>A switch is in flight. The server is unloading and loading, which is slow.</summary>
        Switching,

        /// <summary>The last list or switch failed. <see cref="ModelController.Error"/> says why; the old catalog is kept.</summary>
        Failed
    }

    /// <summary>
    /// The state of the model control, and the only thing that talks to an <see cref="IModelSelector"/>.
    ///
    /// Same hand-off as the event path (STANDARDS section 2 and 4): requests run on the thread pool, their
    /// results are queued, and the main thread applies them in <see cref="Pump"/>. So this never depends on a
    /// synchronisation context, never blocks a frame, and the UI is only ever written by one thread. Each request
    /// carries a generation number; a result that arrives after a newer request started is dropped, so a slow
    /// list cannot overwrite the outcome of a switch the user made after it.
    /// </summary>
    public sealed class ModelController : IDisposable
    {
        private readonly IModelSelector _selector;
        private readonly ConcurrentQueue<Action> _done = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _generation;

        public ModelControlPhase Phase { get; private set; }
        public ModelCatalog Catalog { get; private set; }

        /// <summary>Why the last request failed. Null unless <see cref="Phase"/> is Failed.</summary>
        public string Error { get; private set; }

        /// <summary>The model being switched to while <see cref="Phase"/> is Switching.</summary>
        public string PendingModelId { get; private set; }

        public bool IsAvailable => _selector != null;

        public event Action Changed;

        /// <param name="selector">Null when the agent has no model capability.</param>
        public ModelController(IModelSelector selector)
        {
            _selector = selector;
            Phase = selector == null ? ModelControlPhase.Unavailable : ModelControlPhase.Ready;
        }

        /// <summary>Fetch the list. Safe to call repeatedly; the newest call wins.</summary>
        public void Refresh()
        {
            // A refresh would bump the generation and drop the switch's result: the switch finishes first.
            if (Phase == ModelControlPhase.Switching) return;
            Fetch(quiet: false);
        }

        /// <summary>Switch to a model. Ignored while another switch is running, or when it is already current.</summary>
        public void Select(string modelId)
        {
            if (_selector == null || string.IsNullOrEmpty(modelId) || Phase == ModelControlPhase.Switching) return;
            if (Catalog != null && Catalog.CurrentModelId == modelId && Phase != ModelControlPhase.Failed) return;

            int gen = ++_generation;
            PendingModelId = modelId;
            Phase = ModelControlPhase.Switching;
            Error = null;
            Changed?.Invoke();

            int? ctx = ChooseContext(Catalog, modelId);
            Task task;
            try
            {
                task = _selector.SetModelAsync(modelId, ctx, _cts.Token);
            }
            catch (Exception e)
            {
                task = Task.FromException(e);
            }
            task.ContinueWith(
                t => _done.Enqueue(() => OnSwitched(gen, t)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>
        /// Apply finished requests. Call from Update() on the main thread. Returns true if anything changed state.
        /// </summary>
        public bool Pump()
        {
            bool any = false;
            while (_done.TryDequeue(out Action apply))
            {
                apply();
                any = true;
            }
            return any;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        /// <summary>
        /// The context to ask for when switching: what the server already uses (so a switch is not a hidden change
        /// to memory use), capped by what the target model supports. Null means "let the server choose".
        /// </summary>
        public static int? ChooseContext(ModelCatalog catalog, string modelId)
        {
            if (catalog == null) return null;
            int? want = catalog.ConfiguredContextLength ?? catalog.ActiveContextLength;
            if (!want.HasValue) return null;
            ModelInfo m = catalog.Find(modelId);
            if (m != null && m.MaxContextLength.HasValue && m.MaxContextLength.Value < want.Value) return m.MaxContextLength;
            return want;
        }

        /// <param name="quiet">
        /// Refresh the catalog without touching <see cref="Phase"/> or <see cref="Error"/>: used after a failed
        /// switch, where the failure message must stay on screen while the list catches up with what the server
        /// actually did (it may have restored the old model).
        /// </param>
        private void Fetch(bool quiet)
        {
            if (_selector == null) return;
            int gen = ++_generation;
            if (!quiet)
            {
                Phase = ModelControlPhase.Loading;
                Error = null;
                Changed?.Invoke();
            }

            Task<ModelCatalog> task;
            try
            {
                task = _selector.GetModelsAsync(_cts.Token);
            }
            catch (Exception e)
            {
                task = Task.FromException<ModelCatalog>(e);
            }
            task.ContinueWith(
                t => _done.Enqueue(() => OnListed(gen, t, quiet)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void OnListed(int gen, Task<ModelCatalog> t, bool quiet)
        {
            if (gen != _generation) return;
            if (t.IsFaulted || t.IsCanceled || t.Result == null)
            {
                if (!quiet)
                {
                    Phase = ModelControlPhase.Failed;
                    Error = Describe(t, "Could not read the model list.");
                    Changed?.Invoke();
                }
                return;
            }
            Catalog = t.Result;
            if (!quiet) Phase = ModelControlPhase.Ready;
            Changed?.Invoke();
        }

        private void OnSwitched(int gen, Task t)
        {
            if (gen != _generation) return;
            PendingModelId = null;
            if (t.IsFaulted || t.IsCanceled)
            {
                Phase = ModelControlPhase.Failed;
                Error = Describe(t, "The model switch failed.");
                Changed?.Invoke();
                Fetch(quiet: true);
                return;
            }
            Fetch(quiet: false);
        }

        private static string Describe(Task t, string fallback)
        {
            Exception e = t.Exception != null ? t.Exception.GetBaseException() : null;
            return e == null || string.IsNullOrWhiteSpace(e.Message) ? fallback : e.Message;
        }
    }
}
