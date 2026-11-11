using System;
using System.Collections.Generic;

namespace AgentConsole.Domain
{
    /// <summary>One tool of a server: its bare name plus what a live inventory knows about it.</summary>
    public sealed class ToolInfo
    {
        /// <summary>The bare name. This is what the graph keys a tool node on.</summary>
        public string Name { get; }

        /// <summary>
        /// The name exactly as the agent's tool list gave it (for example <c>fates_desktop__screenshot</c>). It is the key a
        /// pause request must use, and it is NOT always server + "__" + <see cref="Name"/> (the built-in server's tools are
        /// bare). Rejected: rebuilding the key from the server name, which gets the built-in server wrong.
        /// </summary>
        public string WireName { get; }

        /// <summary>The tool is switched off for the model (the web app's pause toggle). A paused tool is not in the graph.</summary>
        public bool Paused { get; }

        /// <summary>The server's estimate of what the tool's schema costs in prompt tokens. Null when not reported.</summary>
        public int? TokensEst { get; }

        public ToolInfo(string name, bool paused = false, int? tokensEst = null, string wireName = null)
        {
            Name = name;
            WireName = string.IsNullOrEmpty(wireName) ? name : wireName;
            Paused = paused;
            TokensEst = tokensEst;
        }

        public ToolInfo WithPaused(bool paused) => new ToolInfo(Name, paused, TokensEst, WireName);
    }

    /// <summary>One MCP server in the inventory, with its tools.</summary>
    public sealed class ServerInfo
    {
        public const string KindMcp = "mcp";
        public const string KindMemory = "memory";
        public const string StatusConnected = "connected";
        public const string StatusError = "error";

        public string Name { get; }
        public string Kind { get; }
        public string Status { get; }

        /// <summary>The bare tool names, in order. Derived from <see cref="ToolDetails"/>; this is what the graph keys on.</summary>
        public IReadOnlyList<string> Tools { get; }

        /// <summary>The same tools with their live flags. Tools given as bare names get default flags.</summary>
        public IReadOnlyList<ToolInfo> ToolDetails { get; }

        /// <summary>The whole server is switched off for the model. A paused server is not in the graph.</summary>
        public bool Paused { get; }

        /// <summary>The server's own explanation when it is not connected. Null otherwise.</summary>
        public string Error { get; }

        /// <summary>Estimated prompt tokens for all the server's tools, and for the ones not paused, as the agent reported them. Null when not reported.</summary>
        public int? TokensEst { get; }
        public int? ActiveTokensEst { get; }

        /// <summary>The prototype's form: bare names, no live data. Kept so the sample inventory and every existing caller compile unchanged.</summary>
        public ServerInfo(string name, string kind, string status, params string[] tools)
            : this(name, kind, status, ToInfos(tools), false, null, null, null)
        {
        }

        public ServerInfo(
            string name, string kind, string status, IReadOnlyList<ToolInfo> tools,
            bool paused, string error, int? tokensEst, int? activeTokensEst)
        {
            Name = name;
            Kind = string.IsNullOrEmpty(kind) ? KindMcp : kind;
            Status = string.IsNullOrEmpty(status) ? StatusConnected : status;
            List<ToolInfo> details = new List<ToolInfo>();
            List<string> names = new List<string>();
            if (tools != null)
            {
                for (int i = 0; i < tools.Count; i++)
                {
                    if (tools[i] == null || string.IsNullOrEmpty(tools[i].Name)) continue;
                    details.Add(tools[i]);
                    names.Add(tools[i].Name);
                }
            }
            ToolDetails = details;
            Tools = names;
            Paused = paused;
            Error = error;
            TokensEst = tokensEst;
            ActiveTokensEst = activeTokensEst;
        }

        public bool IsConnected => string.Equals(Status, StatusConnected, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The one rule for what the graph draws: a server is live when it is connected AND not paused. The graph is read
        /// as "what the agent can use right now", the same thing the agent's own tool list is built from. It used to keep
        /// paused servers (greyed in spirit, drawn in fact), which showed a full green tree for an MCP that was off.
        /// </summary>
        public bool IsLive => IsConnected && !Paused;

        public int ToolCount => ToolDetails.Count;

        /// <summary>How many tools the model can use through this server: none if it is paused or offline, else the tools not paused.</summary>
        public int ActiveToolCount
        {
            get
            {
                if (!IsLive) return 0;
                int n = 0;
                for (int i = 0; i < ToolDetails.Count; i++)
                {
                    if (!ToolDetails[i].Paused) n++;
                }
                return n;
            }
        }

        /// <summary>The server's own total when it gave one, else the sum of its tools'. Null when nothing was reported.</summary>
        public int? TotalTokens => TokensEst ?? SumTokens(false);

        /// <summary>
        /// Tokens the model pays for this server now: zero when it is paused or offline. Summed from the tools whenever they
        /// carry estimates, so an optimistic toggle changes it at once instead of waiting for the next reading; the
        /// agent's own figure is only the fallback for a server whose tools report none.
        /// </summary>
        public int? ActiveTokens
        {
            get
            {
                if (!IsLive) return 0;
                int? sum = SumTokens(true);
                return sum ?? ActiveTokensEst;
            }
        }

        public ServerInfo WithPaused(bool paused) =>
            new ServerInfo(Name, Kind, Status, ToolDetails, paused, Error, TokensEst, ActiveTokensEst);

        /// <summary>A copy with one tool's paused flag set, found by its wire name. The same instance when there is no such tool or nothing changes.</summary>
        public ServerInfo WithToolPaused(string wireName, bool paused)
        {
            List<ToolInfo> tools = null;
            for (int i = 0; i < ToolDetails.Count; i++)
            {
                ToolInfo t = ToolDetails[i];
                if (t.WireName != wireName || t.Paused == paused) continue;
                if (tools == null) tools = new List<ToolInfo>(ToolDetails);
                tools[i] = t.WithPaused(paused);
            }
            return tools == null ? this : new ServerInfo(Name, Kind, Status, tools, Paused, Error, TokensEst, ActiveTokensEst);
        }

        public bool HasTool(string wireName)
        {
            for (int i = 0; i < ToolDetails.Count; i++)
            {
                if (ToolDetails[i].WireName == wireName) return true;
            }
            return false;
        }

        /// <summary>
        /// Memory is decided by the server's NAME, the same rule <see cref="GraphModel.IsMemoryServer"/> applies to a
        /// grafted server: a server literally called "memory". Rejected: calling any server that has vault_* tools
        /// memory. EVA serves its vault tools from fates_desktop, a general desktop server, and that would turn the
        /// whole desktop cluster into the memory colour. The memory tree does not need the flag anyway: it finds its
        /// server from the wire prefix of the first successful vault call.
        /// </summary>
        public static string KindFor(string serverName) =>
            serverName == GraphModel.DefaultMemoryServerName ? KindMemory : KindMcp;

        private int? SumTokens(bool activeOnly)
        {
            int total = 0;
            bool any = false;
            for (int i = 0; i < ToolDetails.Count; i++)
            {
                ToolInfo t = ToolDetails[i];
                if (!t.TokensEst.HasValue || (activeOnly && t.Paused)) continue;
                total += t.TokensEst.Value;
                any = true;
            }
            return any ? (int?)total : null;
        }

        private static IReadOnlyList<ToolInfo> ToInfos(string[] names)
        {
            List<ToolInfo> list = new List<ToolInfo>(names == null ? 0 : names.Length);
            if (names != null)
            {
                for (int i = 0; i < names.Length; i++) list.Add(new ToolInfo(names[i]));
            }
            return list;
        }
    }
}
