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
        /// <summary>EVA on the home network. Used only when the transport is EvaSse; the default transport stays Replay.</summary>
        public const string DefaultEvaBaseUrl = "http://10.0.0.219:8000";

        public const string ReplayPrompt = "Cross-check last week’s trading notes against my open positions.";

        [SerializeField] private ConsoleView view;

        [Header("Transport")]
        [SerializeField] private TransportKind transportKind = TransportKind.Replay;
        [SerializeField] private string evaBaseUrl = DefaultEvaBaseUrl;
        [Tooltip("Replay speed multiplier. 1 is the prototype's real timing.")]
        [SerializeField, Min(0.1f)] private float replaySpeed = 1f;
        [Tooltip("Replay only: start the scripted run by itself after a short beat, as the prototype does.")]
        [SerializeField] private bool autoStartReplay = true;
        [SerializeField] private float autoStartDelay = 0.9f;

        [Header("Agent")]
        [SerializeField] private string agentLabel = "EVA";
        [SerializeField, Min(16)] private int maxEventsPerFrame = 256;

        private IAgentTransport _transport;
        private IChatStore _store;
        private ConsoleReducer _reducer;
        private ModelController _models;
        private ChatSession _session;
        private MemoryTracker _memory;

        public ConsoleReducer Reducer => _reducer;
        public IAgentTransport Transport => _transport;
        public ChatSession Session => _session;

        private void Start()
        {
            if (view == null) view = FindAnyObjectByType<ConsoleView>();

            // A built player is pointed at an agent by config.json, not by a rebuild.
            ConsoleConfig cfg = ConsoleConfig.Load();
            if (cfg != null)
            {
                transportKind = cfg.Kind;
                evaBaseUrl = cfg.evaBaseUrl;
            }
            else
            {
                new ConsoleConfig { transport = transportKind.ToString(), evaBaseUrl = evaBaseUrl }.WriteIfAbsent();
            }

            GraphModel graph = new GraphModel(agentLabel, SampleInventory.Eva());
            _reducer = new ConsoleReducer(graph);

            if (transportKind == TransportKind.Replay)
            {
                // memoryTree: the demo run includes a memory sequence, so the session memory tree can be watched growing offline.
                _transport = new ReplayTransport(replaySpeed, memoryTree: true);
                _store = SampleChats();
                view.DefaultPrompt = ReplayPrompt;
            }
            else
            {
                _transport = new EvaSseTransport(evaBaseUrl);
                _store = new EvaChatStore(evaBaseUrl);
            }

            // The model control is gated by capability, never by which agent this is: a transport that cannot list
            // models does not advertise it, and the control then degrades to a read-only readout.
            IModelSelector selector = _transport.Supports(AgentCapability.Models) ? _transport as IModelSelector : null;
            _models = new ModelController(selector);

            // The send pipeline, queue and chat list: the one object the View talks to, so it never sees a transport.
            _session = new ChatSession(_reducer, _transport, _store);
            view.Bind(_reducer, _models, _session);
            _memory = new MemoryTracker(_reducer); // the session memory tree: grown from successful vault calls, cleared per chat
            view.BindMemory(_memory);
            if (_models.IsAvailable) _models.Refresh(); // async: a slow /models never holds a frame
            _session.SelectionChanged += id => view.LastChatId = id;
            view.SetConnection(transportKind.ToString(), evaBaseUrl);
            view.ConnectionSaved += (t, u) =>
            {
                // Settings edits the same two fields config.json holds. Empty keeps the old URL rather than blanking it.
                new ConsoleConfig { transport = t, evaBaseUrl = string.IsNullOrWhiteSpace(u) ? evaBaseUrl : u }.Save();
            };
            _session.Refresh(view.LastChatId); // reopens the chat the user was in, as the web client does

            if (transportKind == TransportKind.Replay && autoStartReplay)
            {
                StartCoroutine(AutoStart());
            }
        }

        private IEnumerator AutoStart()
        {
            yield return new WaitForSeconds(autoStartDelay);
            _session.Submit(ReplayPrompt);
        }

        /// <summary>The Replay transport has no server, so its chats live in memory: enough for the chat islands to be real in the demo.</summary>
        private static MemoryChatStore SampleChats()
        {
            MemoryChatStore store = new MemoryChatStore();
            store.Seed("Market open checklist",
                new StoredMessage("user", "What should I check before the open?"),
                new StoredMessage("assistant", "Futures, the overnight news on your holdings, and any earnings on today's calendar."));
            store.Seed("Weekend reading list",
                new StoredMessage("user", "Summarise the papers I saved on state space models."),
                new StoredMessage("assistant", "Three of them extend Mamba; the other two are about long-context cost."));
            return store;
        }

        private void Update()
        {
            _models?.Pump();
            _session?.Pump();
            if (_transport == null) return;

            // The one hand-off from the worker thread to the main thread. Nothing else touches the reducer.
            int n = 0;
            while (n < maxEventsPerFrame && _transport.TryDequeue(out AgentEvent ev))
            {
                _reducer.Apply(ev);
                n++;
            }
        }

        private void OnDestroy()
        {
            _memory?.Dispose();
            _session?.Dispose();
            _models?.Dispose();
            (_transport as IDisposable)?.Dispose();
            (_store as IDisposable)?.Dispose();
        }
    }
}
