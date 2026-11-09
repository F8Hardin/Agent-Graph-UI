using System;
using UnityEngine;

namespace AgentConsole.View
{
    /// <summary>One island's saved frame. Canvas units, measured from the canvas's top-left (y grows downward).</summary>
    [Serializable]
    internal struct IslandLayout
    {
        /// <summary>False until the user has moved or resized it. An unplaced island follows its code default, so it re-docks when the window changes.</summary>
        public bool placed;
        public float x, y, w, h;
        public bool collapsed;
    }

    /// <summary>
    /// Client settings that persist between launches, kept together so the console has one place for them (the
    /// theme predates this and keeps its own key). Defaults live in code, never in the scene (STANDARDS section 8).
    /// Storage is per machine via PlayerPrefs; a failure to read or write it must never stop the console booting.
    /// </summary>
    internal static class ConsoleSettings
    {
        private const string IslandPrefix = "agc.island.v1.";
        private const string LastChatKey = "agc.lastChatId.v1";
        private const string ViewKey = "agc.view.v1";

        [Serializable]
        private struct ViewJson
        {
            public bool showThinking;
            public bool showRaw;
            public bool showTimings;
            public bool showModel;
            public float uiScale;
            public string nodeStyle;
        }

        // The web client's four display flags, with its defaults (types.ts): raw hidden, the rest shown.
        public static bool ShowThinking = true;
        public static bool ShowRaw;
        public static bool ShowTimings = true;
        public static bool ShowModel = true;

        /// <summary>UI size multiplier, Minecraft-style: 1 is the 1920x1080 reference, below 1 is smaller. Defaults to Small.</summary>
        public static float UiScale = 0.75f;

        /// <summary>The node body style. Persisted by its stable key (NodeStyles.Key), so the enum can be reordered safely.</summary>
        public static NodeStyleKind NodeStyle = NodeStyles.Default;

        /// <summary>Raised when one of the display flags changes, so open panels repaint.</summary>
        public static event Action ViewChanged;

        public static IslandLayout LoadLayout(string kind)
        {
            try
            {
                string json = PlayerPrefs.GetString(IslandPrefix + kind, null);
                if (!string.IsNullOrEmpty(json)) return JsonUtility.FromJson<IslandLayout>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AgentConsole] could not read layout for " + kind + ": " + e.Message);
            }
            return default;
        }

        public static void SaveLayout(string kind, IslandLayout layout)
        {
            try
            {
                PlayerPrefs.SetString(IslandPrefix + kind, JsonUtility.ToJson(layout));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AgentConsole] could not save layout for " + kind + ": " + e.Message);
            }
        }

        /// <summary>Drop the saved frame of an island that no longer exists, so a retired kind does not linger in PlayerPrefs.</summary>
        public static void ForgetLayout(string kind)
        {
            try
            {
                PlayerPrefs.DeleteKey(IslandPrefix + kind);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AgentConsole] could not forget layout for " + kind + ": " + e.Message);
            }
        }

        /// <summary>The selected chat, remembered across launches as the web client does with eva.lastChatId. Null when none.</summary>
        public static string LastChatId
        {
            get
            {
                try
                {
                    string s = PlayerPrefs.GetString(LastChatKey, null);
                    return string.IsNullOrEmpty(s) ? null : s;
                }
                catch (Exception)
                {
                    return null;
                }
            }
            set
            {
                try
                {
                    if (string.IsNullOrEmpty(value)) PlayerPrefs.DeleteKey(LastChatKey);
                    else PlayerPrefs.SetString(LastChatKey, value);
                }
                catch (Exception)
                {
                    // Not worth failing a chat switch over.
                }
            }
        }

        public static void LoadView()
        {
            try
            {
                string json = PlayerPrefs.GetString(ViewKey, null);
                if (string.IsNullOrEmpty(json)) return;
                ViewJson v = JsonUtility.FromJson<ViewJson>(json);
                ShowThinking = v.showThinking;
                ShowRaw = v.showRaw;
                ShowTimings = v.showTimings;
                ShowModel = v.showModel;
                NodeStyle = NodeStyles.Parse(v.nodeStyle); // absent in blobs saved before this existed: the default
                if (v.uiScale > 0f) UiScale = Mathf.Clamp(v.uiScale, 0.5f, 2f); // absent in blobs saved before this existed
            }
            catch (Exception)
            {
                // Keep the defaults.
            }
        }

        public static void SetView(bool? thinking = null, bool? raw = null, bool? timings = null, bool? model = null, float? uiScale = null, NodeStyleKind? nodeStyle = null)
        {
            if (thinking.HasValue) ShowThinking = thinking.Value;
            if (raw.HasValue) ShowRaw = raw.Value;
            if (timings.HasValue) ShowTimings = timings.Value;
            if (model.HasValue) ShowModel = model.Value;
            if (uiScale.HasValue) UiScale = Mathf.Clamp(uiScale.Value, 0.5f, 2f);
            if (nodeStyle.HasValue) NodeStyle = nodeStyle.Value;
            try
            {
                PlayerPrefs.SetString(ViewKey, JsonUtility.ToJson(new ViewJson
                {
                    showThinking = ShowThinking, showRaw = ShowRaw, showTimings = ShowTimings, showModel = ShowModel, uiScale = UiScale, nodeStyle = NodeStyles.Key(NodeStyle)
                }));
            }
            catch (Exception)
            {
                // The flags still apply for this session.
            }
            ViewChanged?.Invoke();
        }
    }
}
