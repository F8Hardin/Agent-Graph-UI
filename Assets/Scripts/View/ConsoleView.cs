using System;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The View's single entry point. A host (the composition root) hands it a reducer and listens for the
    /// user's intents. The View depends on Domain only: it never sees a transport, an HTTP type or the wire.
    /// </summary>
    public sealed class ConsoleView : MonoBehaviour
    {
        [SerializeField] private GraphView graph;
        [SerializeField] private CameraRig rig;
        [SerializeField] private ConsoleUI ui;
        [SerializeField] private ThemeController theme;

        /// <summary>The user saved a connection in Settings (transport name, base URL). The host persists it.</summary>
        public event Action<string, string> ConnectionSaved;

        /// <summary>Show Settings the connection that is live.</summary>
        public void SetConnection(string transport, string url) { if (ui != null) ui.SetConnection(transport, url); }

        /// <summary>The chat to reopen on launch, remembered across runs (the web client's eva.lastChatId).</summary>
        public string LastChatId
        {
            get => ConsoleSettings.LastChatId;
            set => ConsoleSettings.LastChatId = value;
        }

        /// <summary>Prompt used when the box is empty. Leave null for a transport where an empty prompt means nothing.</summary>
        public string DefaultPrompt
        {
            get => ui != null ? ui.DefaultPrompt : null;
            set { if (ui != null) ui.DefaultPrompt = value; }
        }

        private void Reset()
        {
            graph = FindAnyObjectByType<GraphView>();
            rig = FindAnyObjectByType<CameraRig>();
            ui = FindAnyObjectByType<ConsoleUI>();
            theme = FindAnyObjectByType<ThemeController>();
        }

        /// <param name="models">The model control's state, or null when the agent has no model capability.</param>
        /// <param name="session">The chat list and send pipeline. The View sends and stops through it and never sees a transport.</param>
        public void Bind(ConsoleReducer reducer, ModelController models = null, ChatSession session = null)
        {
            if (graph == null) graph = FindAnyObjectByType<GraphView>();
            if (rig == null) rig = FindAnyObjectByType<CameraRig>();
            if (ui == null) ui = FindAnyObjectByType<ConsoleUI>();

            graph.Bind(reducer);
            rig.Bind(graph);
            ui.Bind(reducer, rig, models, session);
            ui.ConnectionSaved += (t, u) => ConnectionSaved?.Invoke(t, u);
        }

        private MemoryTreeView _memoryTree;

        /// <summary>
        /// Show the session memory tree. The host owns the tracker (it is built from the reducer, which the host also owns),
        /// the View only draws what the tracker's tree says. Call after <see cref="Bind"/>; calling again replaces the view.
        /// </summary>
        public void BindMemory(MemoryTracker tracker)
        {
            if (graph == null) graph = FindAnyObjectByType<GraphView>();
            if (_memoryTree != null) Destroy(_memoryTree.gameObject);
            _memoryTree = tracker != null ? MemoryTreeView.Attach(graph, tracker) : null;
        }

        public string ThemeKey => theme != null ? theme.PresetKey : ThemePresets.DefaultKey;
        public void SetTheme(string key) { if (theme != null) theme.Apply(key); }
        public void SetCameraLocked(bool locked) { if (rig != null) rig.SetLocked(locked); }
    }
}
