using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using AgentConsole.Domain;
using AgentConsole.Transport;
using AgentConsole.View;
using AgentConsole.WebTransport;

namespace AgentConsole.App
{
    public enum TransportKind
    {
        /// <summary>The prototype's scripted run. No server, no network.</summary>
        Replay,

        /// <summary>EVA's /submit_prompt SSE stream over HttpClient and worker threads. The desktop default.</summary>
        EvaSse,

        /// <summary>
        /// The same EVA contract over UnityWebRequest, with every continuation on the main thread (AsyncMode.CallerContext).
        /// The only live transport the browser build can run; selectable in the editor and on desktop too, which is how it is tested.
        /// </summary>
        EvaWeb
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

        private const string PrefTransport = "agentconsole.transport";
        private const string PrefUrl = "agentconsole.evaBaseUrl";

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
        private InventoryController _inventory; // keeps the graph and the Settings list in step with EVA's /tools (or the demo's sample)
        private IInventorySource _inventorySource;
        private bool _webChatsSawLoading;
        private bool _webChatsLogged;
        private bool _webInventoryLogged;
        private string _lastInventoryError;
        private ChatSession _session;
        private MemoryTracker _memory;
        private ReplayTransport _pumpedReplay; // set only when the replay is driven from Update (no threads: WebGL)

        public ConsoleReducer Reducer => _reducer;
        public IAgentTransport Transport => _transport;
        public ChatSession Session => _session;

        /// <summary>The MCP server list and its toggles: EVA's live /tools, or the in-memory sample for the offline replay.</summary>
        public InventoryController Inventory => _inventory;

        private void Start()
        {
            if (view == null) view = FindAnyObjectByType<ConsoleView>();

            if (NoThreads)
            {
                // The browser has no config.json and no HttpClient. It talks to EVA through UnityWebRequest, at the host the
                // page came from on EVA's port, unless the user chose the demo or another URL in Settings (kept in PlayerPrefs).
                transportKind = TransportKind.EvaWeb;
                evaBaseUrl = WebBackendUrl.FromPageUrl(Application.absoluteURL, DefaultEvaBaseUrl);
                LoadWebPrefs();
            }
            else
            {
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
            }

            // A live agent starts with just the agent node and is filled from EVA's /tools below. Rejected: starting
            // from SampleInventory for every transport, which drew the demo's machines (a red desktop, a healthy
            // MacBook) over the real ones. Only the offline replay, which has no server to ask, keeps the sample.
            // The demo's list is the sample plus a paused and an offline server (they are listed, not drawn: the graph shows
            // only what is live), and it is served by an in-memory source so the Settings toggles work with no EVA.
            ServerInfo[] sample = SampleInventory.EvaWithSettings();
            GraphModel graph = transportKind == TransportKind.Replay
                ? new GraphModel(agentLabel, sample)
                : new GraphModel(agentLabel);
            _reducer = new ConsoleReducer(graph);
            bool web = transportKind == TransportKind.EvaWeb;
            // Everything on the constructing (main) thread's context: UnityWebRequest completes later, with no thread pool behind it.
            AsyncMode mode = web ? AsyncMode.CallerContext : AsyncMode.ThreadPool;

            if (transportKind == TransportKind.Replay)
            {
                // memoryTree: the demo run includes a memory sequence, so the session memory tree can be watched growing offline.
                // pumped: with no threads the schedule is advanced by Update instead of Task.Run + Task.Delay.
                _pumpedReplay = new ReplayTransport(replaySpeed, memoryTree: true, pumped: NoThreads);
                _transport = _pumpedReplay;
                _store = SampleChats();
                view.DefaultPrompt = ReplayPrompt;
                // Completed tasks only: the browser build has no thread pool (see InMemoryInventory). A toggle made while the
                // scripted run is playing changes the graph when the run ends, like a toggle against EVA does.
                _inventory = new InventoryController(new InMemoryInventory(sample), graph);
            }
            else
            {
                if (web)
                {
                    _transport = new UwrEvaTransport(evaBaseUrl);
                    _store = new UwrChatStore(evaBaseUrl);
                    _inventorySource = new UwrInventory(evaBaseUrl);
                }
                else
                {
                    _transport = new EvaSseTransport(evaBaseUrl);
                    _store = new EvaChatStore(evaBaseUrl);
                    _inventorySource = new EvaInventory(evaBaseUrl);
                }
                _inventory = new InventoryController(_inventorySource, graph, asyncMode: mode);
                _inventory.Changed += OnInventoryChanged;
                // A finished run may have used a server the last reading did not know about. Ask again, once idle.
                _reducer.RunFinished += _ => _inventory.RequestRefresh();
            }

            // The model control is gated by capability, never by which agent this is: a transport that cannot list
            // models does not advertise it, and the control then degrades to a read-only readout.
            IModelSelector selector = _transport.Supports(AgentCapability.Models) ? _transport as IModelSelector : null;
            _models = new ModelController(selector, mode);

            // The send pipeline, queue and chat list: the one object the View talks to, so it never sees a transport.
            _session = new ChatSession(_reducer, _transport, _store, runInline: NoThreads && !web, asyncMode: mode);
            if (web) _session.Changed += LogWebChats;
            view.Bind(_reducer, _models, _session, _inventory);
            _memory = new MemoryTracker(_reducer); // the session memory tree: grown from successful vault calls, cleared per chat
            view.BindMemory(_memory);
            if (_models.IsAvailable) _models.Refresh(); // async: a slow /models never holds a frame
            _session.SelectionChanged += id => view.LastChatId = id;
            view.SetConnection(transportKind.ToString(), evaBaseUrl);
            view.ConnectionSaved += (t, u) =>
            {
                // Settings edits the same two fields config.json holds. Empty keeps the old URL rather than blanking it.
                string url = string.IsNullOrWhiteSpace(u) ? evaBaseUrl : u;
                if (NoThreads)
                {
                    SaveWebPrefs(t, url); // no file beside a browser build; PlayerPrefs is its storage
                    return;
                }
                // The Settings buttons only know Replay and EVA; do not turn a chosen EvaWeb back into EvaSse by saving.
                string kind = transportKind == TransportKind.EvaWeb && !string.Equals(t, "Replay", StringComparison.OrdinalIgnoreCase) ? "EvaWeb" : t;
                new ConsoleConfig { transport = kind, evaBaseUrl = url }.Save();
            };
            _session.Refresh(view.LastChatId); // reopens the chat the user was in, as the web client does

            if (transportKind == TransportKind.Replay && autoStartReplay)
            {
                StartCoroutine(AutoStart());
            }
        }

        /// <summary>
        /// True where there are no threads and no file system beside the build (the WebGL player). The editor keeps the
        /// normal path even with the WebGL target active, so editing and EditMode tests behave as on desktop.
        /// </summary>
        public static bool NoThreads =>
#if UNITY_WEBGL && !UNITY_EDITOR
            true;
#else
            false;
#endif

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

        private void LoadWebPrefs() // PlayerPrefs can be unavailable in a private window; the defaults then stand
        {
            try
            {
                if (PlayerPrefs.GetString(PrefTransport, string.Empty) == "Replay") transportKind = TransportKind.Replay;
                string u = PlayerPrefs.GetString(PrefUrl, string.Empty);
                if (!string.IsNullOrWhiteSpace(u)) evaBaseUrl = u;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[web] could not read saved connection: " + e.Message);
            }
        }

        private static void SaveWebPrefs(string transport, string url)
        {
            try
            {
                PlayerPrefs.SetString(PrefTransport, string.Equals(transport, "Replay", StringComparison.OrdinalIgnoreCase) ? "Replay" : "EvaWeb");
                PlayerPrefs.SetString(PrefUrl, url);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[web] could not save the connection: " + e.Message);
            }
        }

        /// <summary>One line when the first chat list arrives (or fails): what a browser console shows to tell a CORS or network fault from a working link.</summary>
        private void LogWebChats()
        {
            if (_webChatsLogged) return;
            if (_session.ListPhase == ChatListPhase.Loading) _webChatsSawLoading = true;
            if (!_webChatsSawLoading) return;
            if (_session.ListPhase == ChatListPhase.Idle)
            {
                _webChatsLogged = true;
                Debug.Log("[web] chats: " + _session.Chats.Count);
            }
            else if (_session.ListPhase == ChatListPhase.Failed)
            {
                _webChatsLogged = true;
                Debug.LogWarning("[web] chats failed: " + _session.Error);
            }
        }

        private void OnInventoryChanged()
        {
            if (transportKind == TransportKind.EvaWeb && !_webInventoryLogged)
            {
                if (_inventory.Phase == InventoryPhase.Ready && _inventory.Snapshot != null)
                {
                    _webInventoryLogged = true;
                    int tools = 0;
                    foreach (ServerInfo s in _inventory.Snapshot.All) tools += s.ToolDetails.Count;
                    Debug.Log("[web] inventory: " + _inventory.Snapshot.All.Count + " servers, " + tools + " tools");
                }
                else if (_inventory.Phase == InventoryPhase.Failed)
                {
                    _webInventoryLogged = true;
                    Debug.LogWarning("[web] inventory failed: " + _inventory.Error);
                }
            }
            // There is no connection-status line in the View yet (Settings only shows the transport and URL), so a
            // failed read goes to the console, once per distinct message, not once per 60 s poll.
            if (_inventory.Phase == InventoryPhase.Ready) _lastInventoryError = null; // a later outage is news again
            if (_inventory.Phase != InventoryPhase.Failed || _inventory.Error == _lastInventoryError) return;
            _lastInventoryError = _inventory.Error;
            Debug.LogWarning("EVA tool list unavailable, keeping the current graph: " + _inventory.Error);
        }

        private void Update()
        {
            _models?.Pump();
            _inventory?.Update(_reducer.State.RunInFlight); // fetch, and rebuild the graph only if its shape changed
            _session?.Pump();
            if (_transport == null) return;
            _pumpedReplay?.Pump(Time.realtimeSinceStartupAsDouble); // no-op unless the replay is pumped

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
            _inventory?.Dispose();
            (_inventorySource as IDisposable)?.Dispose();
            (_transport as IDisposable)?.Dispose();
            (_store as IDisposable)?.Dispose();
        }
    }
}
