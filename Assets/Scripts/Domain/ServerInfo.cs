using System;
using System.Collections.Generic;

namespace AgentConsole.Domain
{
    /// <summary>One tool of a server: its bare name plus what a live inventory knows about it.</summary>
    public sealed class ToolInfo
    {
        public string Name { get; }

        /// <summary>The server has switched this tool off for the model (the web app's pause toggle). Still listed, still drawn.</summary>
        public bool Paused { get; }

        /// <summary>The server's estimate of what the tool's schema costs in prompt tokens. Null when not reported.</summary>
        public int? TokensEst { get; }

        public ToolInfo(string name, bool paused = false, int? tokensEst = null)
        {
            Name = name;
            Paused = paused;
            TokensEst = tokensEst;
        }
    }

    /// <summary>One MCP server in the inventory, with the names of its tools.</summary>
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

        /// <summary>The whole server is paused. Its tools are still listed and still in the graph.</summary>
        public bool Paused { get; }

        /// <summary>The server's own explanation when it is not connected. Null otherwise.</summary>
        public string Error { get; }

        /// <summary>Estimated prompt tokens for all the server's tools, and for the ones not paused. Null when not reported.</summary>
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
        /// Memory is decided by the server's NAME, the same rule <see cref="GraphModel.IsMemoryServer"/> applies to a
        /// grafted server: a server literally called "memory". Rejected: calling any server that has vault_* tools
        /// memory. EVA serves its vault tools from fates_desktop, a general desktop server, and that would turn the
        /// whole desktop cluster into the memory colour. The memory tree does not need the flag anyway: it finds its
        /// server from the wire prefix of the first successful vault call.
        /// </summary>
        public static string KindFor(string serverName) =>
            serverName == GraphModel.DefaultMemoryServerName ? KindMemory : KindMcp;

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
