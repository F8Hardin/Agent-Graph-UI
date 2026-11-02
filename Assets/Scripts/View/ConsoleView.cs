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

        /// <summary>The user sent a prompt (typed, or the default when the box was empty).</summary>
        public event Action<string> PromptSubmitted;

        /// <summary>The user pressed Stop on a live run.</summary>
        public event Action StopRequested;

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
        public void Bind(ConsoleReducer reducer, ModelController models = null)
        {
            if (graph == null) graph = FindAnyObjectByType<GraphView>();
            if (rig == null) rig = FindAnyObjectByType<CameraRig>();
            if (ui == null) ui = FindAnyObjectByType<ConsoleUI>();

            graph.Bind(reducer);
            rig.Bind(graph);
            ui.Bind(reducer, rig, models);
            ui.PromptSubmitted += s => PromptSubmitted?.Invoke(s);
            ui.StopRequested += () => StopRequested?.Invoke();
        }

        public string ThemeKey => theme != null ? theme.PresetKey : ThemePresets.DefaultKey;
        public void SetTheme(string key) { if (theme != null) theme.Apply(key); }
        public void SetCameraLocked(bool locked) { if (rig != null) rig.SetLocked(locked); }
    }
}
