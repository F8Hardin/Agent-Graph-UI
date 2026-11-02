using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using AgentConsole.Domain;
using AgentConsole.Transport;
using AgentConsole.View;

namespace AgentConsole.App
{
    public enum TransportKind
    {
        /// <summary>The prototype's scripted run. No server, no network.</summary>
        Replay,

        /// <summary>EVA's /submit_prompt SSE stream.</summary>
        EvaSse
    }

    /// <summary>
    /// The composition root, and the only place that knows about both a transport and the View. It wires
    /// a transport into the reducer (INTERFACE.md section 7: the worker thread queues, the main thread
    /// drains in Update) and hands the reducer to the View. That is why it lives in its own assembly:
    /// View references Domain only, and this assembly is what lets Transport and View meet.
    /// </summary>
    public sealed class ConsoleBootstrap : MonoBehaviour
    {
        public const string ReplayPrompt = "Cross-check last week’s trading notes against my open positions.";

        [SerializeField] private ConsoleView view;

        [Header("Transport")]
        [SerializeField] private TransportKind transportKind = TransportKind.Replay;
        [SerializeField] private string evaBaseUrl = EvaSseTransport.DefaultBaseUrl;
        [Tooltip("Replay speed multiplier. 1 is the prototype's real timing.")]
        [SerializeField, Min(0.1f)] private float replaySpeed = 1f;
        [Tooltip("Replay only: start the scripted run by itself after a short beat, as the prototype does.")]
        [SerializeField] private bool autoStartReplay = true;
        [SerializeField] private float autoStartDelay = 0.9f;

        [Header("Agent")]
        [SerializeField] private string agentLabel = "EVA";
        [SerializeField, Min(16)] private int maxEventsPerFrame = 256;

        private readonly ConcurrentQueue<string> _sendErrors = new ConcurrentQueue<string>();
        private IAgentTransport _transport;
        private ConsoleReducer _reducer;
        private CancellationTokenSource _cts;

        public ConsoleReducer Reducer => _reducer;
        public IAgentTransport Transport => _transport;

        private void Start()
        {
            if (view == null) view = FindAnyObjectByType<ConsoleView>();

            GraphModel graph = new GraphModel(agentLabel, SampleInventory.Eva());
            _reducer = new ConsoleReducer(graph);
            _cts = new CancellationTokenSource();

            if (transportKind == TransportKind.Replay)
            {
                _transport = new ReplayTransport(replaySpeed);
                view.DefaultPrompt = ReplayPrompt;
            }
            else
            {
                _transport = new EvaSseTransport(evaBaseUrl);
            }

            view.Bind(_reducer);
            view.PromptSubmitted += OnPrompt;
            view.StopRequested += OnStop;

            if (transportKind == TransportKind.Replay && autoStartReplay)
            {
                StartCoroutine(AutoStart());
            }
        }

        private IEnumerator AutoStart()
        {
            yield return new WaitForSeconds(autoStartDelay);
            OnPrompt(ReplayPrompt);
        }

        private void OnPrompt(string prompt)
        {
            if (_transport == null || string.IsNullOrWhiteSpace(prompt)) return;
            _reducer.Reset();
            _reducer.AddUserMessage(prompt);

            // IDs are server-owned: no chat id is invented here. The transport returns the job id.
            Task<string> send = _transport.SendAsync(null, prompt, _cts.Token);
            send.ContinueWith(
                t => _sendErrors.Enqueue(t.Exception != null ? t.Exception.GetBaseException().Message : "Send failed."),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private void OnStop()
        {
            string job = _reducer.State.JobId;
            if (_transport == null || string.IsNullOrEmpty(job)) return;
            _transport.StopAsync(job).ContinueWith(
                t => _sendErrors.Enqueue(t.Exception != null ? t.Exception.GetBaseException().Message : "Stop failed."),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private void Update()
        {
            if (_transport == null) return;

            // The one hand-off from the worker thread to the main thread. Nothing else touches the reducer.
            int n = 0;
            while (n < maxEventsPerFrame && _transport.TryDequeue(out AgentEvent ev))
            {
                _reducer.Apply(ev);
                n++;
            }
            while (_sendErrors.TryDequeue(out string message))
            {
                _reducer.Apply(AgentEvent.Error(message));
            }
        }

        private void OnDestroy()
        {
            if (view != null)
            {
                view.PromptSubmitted -= OnPrompt;
                view.StopRequested -= OnStop;
            }
            _cts?.Cancel();
            (_transport as IDisposable)?.Dispose();
            _cts?.Dispose();
        }
    }
}
