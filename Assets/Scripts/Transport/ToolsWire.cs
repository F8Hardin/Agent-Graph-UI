using System;
using System.Collections.Generic;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>
    /// Parses EVA's GET /tools, the endpoint the web app's settings page already uses to list MCP servers:
    /// <code>
    /// {"servers":[{"name":"fates_desktop","status":"connected","error":null,"paused":false,"tokens_est":3703,"active_tokens_est":3476,
    ///   "tools":[{"name":"fates_desktop__vault_read","short_name":"vault_read","paused":false,"tokens_est":94}, ...]}, ...],
    ///  "totals":{...},"note":"..."}
    /// </code>
    /// Tolerant like <see cref="ModelsWire"/>: unknown fields are ignored, a server without a name or a tool without
    /// one is skipped, and every number may be missing. A server with NO status is treated as not connected: nothing
    /// proves it is reachable, and drawing a node for a machine that is off is the bug this exists to fix.
    /// </summary>
    public static class ToolsWire
    {
        public const string StatusUnknown = "unknown";

        /// <summary>False when the body is not a JSON object with a "servers" array (a proxy's HTML page, the wrong endpoint). An empty array is a valid answer.</summary>
        public static bool TryParse(string json, out InventorySnapshot snapshot)
        {
            snapshot = null;
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj)) return false;
            if (!(Get(obj, "servers") is List<object> rows)) return false;

            List<ServerInfo> servers = new List<ServerInfo>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                if (!(rows[i] is Dictionary<string, object> row)) continue;
                string name = Get(row, "name") as string;
                if (string.IsNullOrEmpty(name)) continue;
                string status = Get(row, "status") as string;
                servers.Add(new ServerInfo(
                    name, ServerInfo.KindFor(name), string.IsNullOrEmpty(status) ? StatusUnknown : status,
                    ParseTools(name, Get(row, "tools")),
                    Get(row, "paused") is bool paused && paused,
                    Get(row, "error") as string,
                    AsInt(Get(row, "tokens_est")), AsInt(Get(row, "active_tokens_est"))));
            }
            snapshot = InventorySnapshot.From(servers);
            return true;
        }

        /// <summary>
        /// The label a tool node carries. <c>short_name</c> is the bare name. If it is missing, the namespaced
        /// <c>server__tool</c> name loses its OWN server's prefix. The graph keys a tool as server + "__" + this label and
        /// the reducer splits a wire name on its first "__", so the two agree and a live call lights the pre-built node.
        /// A name with some other prefix is kept whole rather than guessed at.
        /// </summary>
        public static string ToolLabel(string server, string name, string shortName)
        {
            if (!string.IsNullOrEmpty(shortName)) return shortName;
            if (string.IsNullOrEmpty(name)) return null;
            string prefix = server + "__";
            return name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)
                ? name.Substring(prefix.Length)
                : name;
        }

        private static List<ToolInfo> ParseTools(string server, object tools)
        {
            List<ToolInfo> list = new List<ToolInfo>();
            if (!(tools is List<object> items)) return list;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                string label;
                bool paused = false;
                int? tokens = null;
                if (items[i] is string bare)
                {
                    label = ToolLabel(server, bare, null);
                }
                else if (items[i] is Dictionary<string, object> t)
                {
                    label = ToolLabel(server, Get(t, "name") as string, Get(t, "short_name") as string);
                    paused = Get(t, "paused") is bool p && p;
                    tokens = AsInt(Get(t, "tokens_est"));
                }
                else
                {
                    continue;
                }
                if (string.IsNullOrEmpty(label) || !seen.Add(label)) continue;
                list.Add(new ToolInfo(label, paused, tokens));
            }
            return list;
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            d.TryGetValue(key, out object v);
            return v;
        }

        private static int? AsInt(object v)
        {
            return v is double d && d >= 0 && d <= int.MaxValue ? (int?)(int)d : null;
        }
    }
}
