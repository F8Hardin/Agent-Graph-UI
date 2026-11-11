using System.Collections.Generic;
using System.Globalization;

namespace AgentConsole.Domain
{
    /// <summary>The headline numbers of the Settings list: tools the model can use and what they cost in prompt tokens.</summary>
    public readonly struct InventoryTotals
    {
        public int ActiveTools { get; }
        public int TotalTools { get; }
        public int ActiveTokens { get; }
        public int TotalTokens { get; }

        public InventoryTotals(int activeTools, int totalTools, int activeTokens, int totalTokens)
        {
            ActiveTools = activeTools;
            TotalTools = totalTools;
            ActiveTokens = activeTokens;
            TotalTokens = totalTokens;
        }

        /// <summary>
        /// Computed from the list, not read from the agent's own "totals": the list already carries the user's pending
        /// toggles, and the numbers must move with them. Rejected: trusting the agent's totals, which would lag a toggle
        /// by a whole request.
        /// </summary>
        public static InventoryTotals Of(IReadOnlyList<ServerInfo> servers)
        {
            int at = 0, tt = 0, ak = 0, tk = 0;
            if (servers != null)
            {
                for (int i = 0; i < servers.Count; i++)
                {
                    ServerInfo s = servers[i];
                    at += s.ActiveToolCount;
                    tt += s.ToolCount;
                    ak += s.ActiveTokens ?? 0;
                    tk += s.TotalTokens ?? 0;
                }
            }
            return new InventoryTotals(at, tt, ak, tk);
        }
    }

    /// <summary>Plain-text forms for the Settings list. Pure and engine-free, so the numbers can be tested.</summary>
    public static class InventoryFormat
    {
        /// <summary>94 -> "~94 tok", 2150 -> "~2.2k tok", 14715 -> "~14.7k tok". Empty when the agent gave no estimate.</summary>
        public static string Tokens(int? n)
        {
            if (!n.HasValue) return string.Empty;
            return "~" + Compact(n.Value) + " tok";
        }

        /// <summary>The number part of <see cref="Tokens"/> without the unit, for the totals line.</summary>
        public static string Compact(int n)
        {
            if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
            return (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }

        /// <summary>The word for a server's state: "on", "paused" or "offline".</summary>
        public static string StatusWord(ServerInfo s)
        {
            if (!s.IsConnected) return "offline";
            return s.Paused ? "paused" : "on";
        }
    }
}
