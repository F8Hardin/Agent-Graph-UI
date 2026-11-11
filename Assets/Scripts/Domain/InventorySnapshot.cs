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
    /// One reading of the agent's inventory: EVERY server it listed, in the order it listed them, whatever its state.
    /// The Settings list shows all of them (connected, paused, offline). The graph shows only the live ones, a
    /// connected server that is not paused with its tools that are not paused, and applies that rule itself
    /// (<see cref="ServerInfo.IsLive"/>), so the one rule is not repeated at each caller. Rejected: drawing an unreachable
    /// server red. That is what made the offline MacBook look like a bug and sent the user hunting, when the right
    /// picture of a machine that is off is that it is not there. The same goes for a paused one (UNI-12).
    /// </summary>
    public sealed class InventorySnapshot
    {
        public static readonly InventorySnapshot Empty = From(null);

        private static readonly string[] NoNames = new string[0];

        /// <summary>Every server, in the agent's order (the first server to list a bare tool name wins). Names are unique.</summary>
        public IReadOnlyList<ServerInfo> All { get; }

        /// <summary>The servers that are not connected, with their error text. A subset of <see cref="All"/> kept for the offline list.</summary>
        public IReadOnlyList<OmittedServer> Omitted { get; }

        /// <summary>
        /// Names the agent did not recognise in a pause request (the "unknown" list of its answer). Empty for a plain reading.
        /// </summary>
        public IReadOnlyList<string> Unknown { get; }

        /// <summary>
        /// A stable fingerprint of the SHAPE of the graph this reading builds: which live servers, which live tools. Sorted,
        /// so a server list that merely reorders does not count as a change. Token estimates are left out: they do not
        /// change the shape.
        /// </summary>
        public string Signature { get; }

        /// <summary>
        /// A fingerprint of everything the Settings list shows: every server's status, error, paused flags and estimates,
        /// in order. A poll that finds the same thing leaves the list alone, so the rows are only rebuilt when something
        /// changed. The graph uses <see cref="Signature"/>, which is coarser.
        /// </summary>
        public string Fingerprint { get; }

        private InventorySnapshot(List<ServerInfo> all, List<OmittedServer> omitted, IReadOnlyList<string> unknown)
        {
            All = all;
            Omitted = omitted;
            Unknown = unknown ?? NoNames;
            Signature = Sign(all);
            Fingerprint = Print(all);
        }

        public static InventorySnapshot From(IEnumerable<ServerInfo> servers, IReadOnlyList<string> unknown = null)
        {
            List<ServerInfo> all = new List<ServerInfo>();
            List<OmittedServer> omitted = new List<OmittedServer>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (servers != null)
            {
                foreach (ServerInfo s in servers)
                {
                    if (s == null || string.IsNullOrEmpty(s.Name) || !seen.Add(s.Name)) continue;
                    all.Add(s);
                    if (!s.IsConnected) omitted.Add(new OmittedServer(s.Name, s.Status, s.Error));
                }
            }
            return new InventorySnapshot(all, omitted, unknown);
        }

        /// <summary>The same servers with the unknown list set. Used by the transport, which reads the list after the servers.</summary>
        public InventorySnapshot WithUnknown(IReadOnlyList<string> unknown) => From(All, unknown);

        private static string Sign(List<ServerInfo> all)
        {
            List<string> lines = new List<string>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                ServerInfo s = all[i];
                if (!s.IsLive) continue;
                List<string> tools = new List<string>(s.ToolDetails.Count);
                for (int t = 0; t < s.ToolDetails.Count; t++)
                {
                    if (!s.ToolDetails[t].Paused) tools.Add(s.ToolDetails[t].Name);
                }
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

        private static string Print(List<ServerInfo> all)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                ServerInfo s = all[i];
                sb.Append(s.Name).Append('|').Append(s.Status).Append('|').Append(s.Paused ? 'p' : 'a').Append('|')
                    .Append(s.Error).Append('|').Append(s.TokensEst).Append('|').Append(s.ActiveTokensEst).Append('|');
                for (int t = 0; t < s.ToolDetails.Count; t++)
                {
                    ToolInfo tool = s.ToolDetails[t];
                    sb.Append(tool.WireName).Append(tool.Paused ? "!" : "=").Append(tool.TokensEst).Append(',');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
