using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The "MCP servers" section of Settings: the web app's MCP toggles. One row per server the agent lists (on / paused /
    /// offline, tools active over total, a token estimate) that opens to its tools, each with its own toggle, and a totals
    /// line over the lot. It reads and writes through <see cref="InventoryController"/> only, the same route
    /// <c>ModelPanel</c> takes to its controller, so it never sees a transport.
    ///
    /// Nothing runs per frame. The rows are rebuilt on the controller's events, and only when its list actually changed
    /// (<see cref="InventoryController.ListVersion"/>), so a poll that finds the same thing costs nothing. Rows take their
    /// colours from the Role system at creation, so every theme works without a repaint hook of this class's own.
    /// Rejected: toggling the rows in place. The list changes shape (a server pauses, a tool is added), and a rebuild of
    /// a few dozen small rows on a click is cheaper to get right than keeping two lists in step.
    /// </summary>
    internal sealed class McpSettingsSection
    {
        private const float ServerRowHeight = 46f;
        private const float ToolRowHeight = 32f;

        private readonly ThemeBinder _tb;
        private readonly Transform _content;
        private readonly TextMeshProUGUI _totals;
        private readonly TextMeshProUGUI _status;
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private RectTransform _list;
        private InventoryController _c;
        private int _version = -1;
        private string _statusShown;
        private Role _statusRole = Role.Muted;

        public McpSettingsSection(Transform content, ThemeBinder tb)
        {
            _tb = tb;
            _content = content;

            TextMeshProUGUI h = UiKit.Text(content, "H MCP servers", "MCP servers", 13, tb, Role.Muted, FontStyles.Bold, TextAlignmentOptions.BottomLeft, false);
            h.gameObject.AddComponent<LayoutElement>().minHeight = 24f;
            _totals = UiKit.Text(content, "MCP Totals", string.Empty, 13, tb, Role.Fg, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);
            _totals.gameObject.AddComponent<LayoutElement>().minHeight = 18f;
            _status = UiKit.Text(content, "MCP Status", string.Empty, 12, tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            _status.gameObject.AddComponent<LayoutElement>().minHeight = 4f;
            // Hold the list's place in Settings now. The rows are first built when the controller is bound, by which
            // time the other sections exist below this one; without a placeholder the list was appended after all
            // of them and the section looked empty.
            _list = UiKit.NewRect("MCP List", content);
            tb.Applied += () => ShowStatus(_statusShown, _statusRole);
            ShowStatus("Tool list not available for this agent.", Role.Muted);
        }

        /// <summary>Attach the controller. Null (or a controller with no source) leaves the section saying so.</summary>
        public void Bind(InventoryController c)
        {
            _c = c;
            if (c == null || !c.IsAvailable) return;
            c.Changed += OnChanged;
            OnChanged();
        }

        /// <summary>Settings was opened: read the list again so what is on screen is current (the web app may have changed it).</summary>
        public void OnOpened()
        {
            if (_c != null && _c.IsAvailable) _c.Refresh();
        }

        // ------------------------------------------------------------------ events

        private void OnChanged()
        {
            RefreshStatus();
            if (_c.ListVersion == _version) return;
            _version = _c.ListVersion;
            RebuildList();
        }

        private void RefreshStatus()
        {
            string text;
            Role role = Role.Muted;
            if (!string.IsNullOrEmpty(_c.ToggleError))
            {
                text = Ascii(_c.ToggleError);
                role = Role.Bad;
            }
            else if (_c.Phase == InventoryPhase.Failed)
            {
                text = "Could not read EVA's tool list: " + Ascii(_c.Error) + (_c.Snapshot != null ? " Showing the last reading." : string.Empty);
                role = Role.Bad;
            }
            else if (_c.HasPending)
            {
                text = "Saving. EVA waits for a running reply to finish before it applies a change, so this can take a while.";
            }
            else if (_c.Snapshot == null)
            {
                text = "Reading EVA's tool list...";
            }
            else
            {
                text = "A paused server or tool is left out of what EVA can use, and out of the graph.";
            }
            ShowStatus(text, role);
        }

        private void ShowStatus(string text, Role role)
        {
            _statusRole = role;
            if (text != _statusShown)
            {
                _statusShown = text;
                _status.text = text ?? string.Empty;
            }
            if (_tb.Tokens != null) _status.color = ThemeBinder.Pick(_tb.Tokens, role);
        }

        // ------------------------------------------------------------------ drawing (events only, never per frame)

        private void RebuildList()
        {
            _tb.Prune(); // the previous rebuild's graphics are gone by now; forget their theme entries
            int at = _content.childCount;
            if (_list != null)
            {
                at = _list.GetSiblingIndex();
                _list.gameObject.SetActive(false); // out of the layout this frame, not at the end of it
                Object.Destroy(_list.gameObject);
            }
            _list = UiKit.NewRect("MCP List", _content);
            _list.SetSiblingIndex(at);
            VerticalLayoutGroup v = _list.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 4f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            IReadOnlyList<ServerInfo> servers = _c.Servers;
            for (int i = 0; i < servers.Count; i++) AddServer(servers[i]);

            InventoryTotals t = InventoryTotals.Of(servers);
            _totals.text = servers.Count == 0
                ? string.Empty
                : "Tools " + t.ActiveTools + " / " + t.TotalTools + " active    ~" + InventoryFormat.Compact(t.ActiveTokens)
                  + " / ~" + InventoryFormat.Compact(t.TotalTokens) + " tok";
        }

        private void AddServer(ServerInfo s)
        {
            bool pending = _c.IsServerPending(s.Name);
            bool offline = !s.IsConnected;
            string error = offline ? Ascii(s.Error) : null;
            bool expandable = s.ToolCount > 0 || !string.IsNullOrEmpty(error);
            bool open = expandable && _expanded.Contains(s.Name);

            Image row = UiKit.Panel(_list, "MCP " + s.Name, _tb, Role.Surface2, 0.7f);
            LayoutElement le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = ServerRowHeight;
            le.preferredHeight = ServerRowHeight;
            if (pending) Dim(row.gameObject);
            if (expandable)
            {
                Button b = row.gameObject.AddComponent<Button>();
                b.targetGraphic = row;
                b.colors = RowColors();
                string name = s.Name;
                b.onClick.AddListener(() => ToggleOpen(name));
            }

            AddSwitch(row.rectTransform, 10f, !offline && !s.Paused, !offline && !pending, () => _c.SetServerPaused(s.Name, !s.Paused));

            string chevron = !expandable ? string.Empty : open ? "▾" : "▸";
            TextMeshProUGUI chev = UiKit.Text(row.transform, "Chevron", chevron, 15, _tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            UiKit.SetAnchors(chev.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -3f), new Vector2(16f, 24f));

            TextMeshProUGUI n = UiKit.Text(row.transform, "Name", s.Name, 14, _tb, Role.Fg,
                s.IsLive ? FontStyles.Bold : FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            n.overflowMode = TextOverflowModes.Ellipsis;
            n.rectTransform.anchorMin = new Vector2(0f, 1f);
            n.rectTransform.anchorMax = new Vector2(1f, 1f);
            n.rectTransform.offsetMin = new Vector2(60f, -27f);
            n.rectTransform.offsetMax = new Vector2(-74f, -3f);

            string word = pending ? "…" : InventoryFormat.StatusWord(s);
            Role wordRole = pending ? Role.Muted : offline ? Role.Bad : s.Paused ? Role.Muted : Role.Accent;
            TextMeshProUGUI st = UiKit.Text(row.transform, "State", word, 13, _tb, wordRole, FontStyles.Bold, TextAlignmentOptions.MidlineRight, false);
            UiKit.SetAnchors(st.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-10f, -3f), new Vector2(64f, 24f));

            string detail = offline
                ? (string.IsNullOrEmpty(error) ? "not connected" : error)
                : s.ActiveToolCount + "/" + s.ToolCount + " tools   " + InventoryFormat.Tokens(s.IsLive ? s.ActiveTokens : s.TotalTokens);
            TextMeshProUGUI d = UiKit.Text(row.transform, "Detail", detail, 11, _tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            d.overflowMode = TextOverflowModes.Ellipsis;
            d.rectTransform.anchorMin = new Vector2(0f, 0f);
            d.rectTransform.anchorMax = new Vector2(1f, 0f);
            d.rectTransform.offsetMin = new Vector2(60f, 4f);
            d.rectTransform.offsetMax = new Vector2(-10f, 22f);

            if (!open) return;
            if (offline && !string.IsNullOrEmpty(error))
            {
                TextMeshProUGUI full = UiKit.Text(_list, "Error", error, 12, _tb, Role.Bad, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);
                full.gameObject.AddComponent<LayoutElement>().minHeight = 20f;
            }
            for (int i = 0; i < s.ToolDetails.Count; i++) AddTool(s, s.ToolDetails[i]);
        }

        private void AddTool(ServerInfo server, ToolInfo tool)
        {
            bool pending = _c.IsToolPending(tool.WireName);
            Image row = UiKit.Panel(_list, "Tool " + tool.WireName, _tb, Role.Surface, 0.6f);
            LayoutElement le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = ToolRowHeight;
            le.preferredHeight = ToolRowHeight;
            // Under a paused server the tool's own flag still reads true, but nothing it offers reaches the model.
            if (pending || !server.IsLive) Dim(row.gameObject, pending);

            AddSwitch(row.rectTransform, 24f, !tool.Paused, server.IsConnected && !pending, () => _c.SetToolPaused(tool.WireName, !tool.Paused));

            TextMeshProUGUI n = UiKit.Text(row.transform, "Name", tool.Name, 13, _tb, Role.Fg, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            n.overflowMode = TextOverflowModes.Ellipsis;
            UiKit.Stretch(n.rectTransform, 56f, 0f, 84f, 0f);
            TextMeshProUGUI k = UiKit.Text(row.transform, "Tokens", pending ? "…" : InventoryFormat.Tokens(tool.TokensEst), 11, _tb, Role.Muted,
                FontStyles.Normal, TextAlignmentOptions.MidlineRight, false);
            UiKit.SetAnchors(k.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-10f, 0f), new Vector2(76f, ToolRowHeight));
        }

        /// <summary>
        /// The on/off box, in the same look as Settings' other toggles (filled accent = on). Its click area is a square
        /// larger than the box, drawn over the row's own expand button so the two never fight. Offline or pending: no
        /// button at all, so the click goes nowhere, and the box is drawn dim.
        /// </summary>
        private void AddSwitch(RectTransform row, float x, bool on, bool clickable, System.Action click)
        {
            RectTransform hit = UiKit.NewRect("Switch", row);
            UiKit.SetAnchors(hit, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x - 8f, 0f), new Vector2(34f, 34f));
            hit.pivot = new Vector2(0f, 0.5f);
            Image area = hit.gameObject.AddComponent<Image>();
            area.color = new Color(0f, 0f, 0f, 0f); // a click target only; no colour of its own to theme
            if (clickable)
            {
                Button b = hit.gameObject.AddComponent<Button>();
                b.targetGraphic = area;
                b.transition = Selectable.Transition.None;
                b.onClick.AddListener(() => click());
            }
            else
            {
                area.raycastTarget = false;
            }
            Image box = UiKit.Panel(hit, "Box", _tb, on ? Role.Accent : Role.Line, on ? (clickable ? 1f : 0.45f) : 0.6f);
            box.raycastTarget = false;
            UiKit.SetAnchors(box.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(18f, 18f));
        }

        private void ToggleOpen(string name)
        {
            if (!_expanded.Remove(name)) _expanded.Add(name);
            RebuildList();
        }

        private static void Dim(GameObject go, bool blockClicks = true)
        {
            CanvasGroup g = go.AddComponent<CanvasGroup>();
            g.alpha = 0.55f;
            g.blocksRaycasts = !blockClicks;
        }

        private static ColorBlock RowColors()
        {
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            cb.selectedColor = Color.white;
            cb.fadeDuration = 0.08f;
            return cb;
        }

        /// <summary>
        /// Error text comes off the wire from servers we do not control. The console font carries Latin-1 and a few
        /// symbols only, and anything else would draw as a missing-glyph box, so other characters become '?'.
        /// </summary>
        private static string Ascii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = c >= ' ' && c <= '~' || c >= ' ' && c <= 'ÿ';
                if (ok) { sb?.Append(c); continue; }
                if (sb == null) sb = new StringBuilder(s, 0, i, s.Length);
                sb.Append(c == '\n' || c == '\r' || c == '\t' ? ' ' : '?');
            }
            return sb == null ? s : sb.ToString();
        }
    }
}
