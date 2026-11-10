using System;
using System.Collections.Generic;
using System.Text;

namespace AgentConsole.Domain
{
    /// <summary>A server the agent knows about but that is not reachable, kept so a UI can say why it is missing.</summary>
    public sealed class OmittedServer
    {
        public string Name { get; }
        public string Status { get; }

        /// <summary>The server's own error text. Null when it gave none.</summary>
        public string Error { get; }

        public OmittedServer(string name, string status, string error)
        {
            Name = name;
            Status = status;
            Error = error;
        }
    }

    /// <summary>
    /// One reading of the agent's inventory, already split by the rule the graph follows: a CONNECTED server is in,
    /// with every tool it lists (paused or not), and anything else is left out. Rejected: drawing an unreachable server
    /// red. That is what made the offline MacBook look like a bug and sent the user hunting, when the right picture of
    /// an offline machine is that it is not there.
    /// </summary>
    public sealed class InventorySnapshot
    {
        public static readonly InventorySnapshot Empty = From(null);

        /// <summary>The servers to draw, in the order the agent listed them (the first server to list a bare tool name wins).</summary>
        public IReadOnlyList<ServerInfo> Connected { get; }

        public IReadOnlyList<OmittedServer> Omitted { get; }

        /// <summary>
        /// A stable fingerprint of the SHAPE of the graph this snapshot builds: which servers, which tools. Sorted, so a
        /// server list that merely reorders does not count as a change. Paused flags and token estimates are left out on
        /// purpose: they do not change the shape, and a rebuild resets the layout the user arranged.
        /// </summary>
        public string Signature { get; }

        private InventorySnapshot(List<ServerInfo> connected, List<OmittedServer> omitted)
        {
            Connected = connected;
            Omitted = omitted;
            Signature = Sign(connected);
        }

        public static InventorySnapshot From(IEnumerable<ServerInfo> servers)
        {
            List<ServerInfo> connected = new List<ServerInfo>();
            List<OmittedServer> omitted = new List<OmittedServer>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (servers != null)
            {
                foreach (ServerInfo s in servers)
                {
                    if (s == null || string.IsNullOrEmpty(s.Name) || !seen.Add(s.Name)) continue;
                    if (s.IsConnected) connected.Add(s);
                    else omitted.Add(new OmittedServer(s.Name, s.Status, s.Error));
                }
            }
            return new InventorySnapshot(connected, omitted);
        }

        private static string Sign(List<ServerInfo> connected)
        {
            List<string> lines = new List<string>(connected.Count);
            for (int i = 0; i < connected.Count; i++)
            {
                ServerInfo s = connected[i];
                List<string> tools = new List<string>(s.Tools);
                tools.Sort(StringComparer.Ordinal);
                StringBuilder sb = new StringBuilder();
                sb.Append(s.Name).Append('|').Append(s.Kind).Append('|');
                for (int t = 0; t < tools.Count; t++)
                {
                    if (t > 0) sb.Append(',');
                    sb.Append(tools[t]);
                }
                lines.Add(sb.ToString());
            }
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }
    }
}
