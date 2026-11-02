namespace AgentConsole.Domain
{
    /// <summary>
    /// The prototype's INVENTORY, ported verbatim. The default graph for the replay run, and a
    /// stand-in until the bridge returns tool names per server (the live endpoint only returns tool_count).
    /// <c>memory</c> is its own cluster, not three ordinary tools.
    /// </summary>
    public static class SampleInventory
    {
        public static ServerInfo[] Eva()
        {
            return new[]
            {
                new ServerInfo("memory", ServerInfo.KindMemory, ServerInfo.StatusConnected,
                    "vault_search", "vault_read", "vault_write_note", "vault_append", "kv_get", "kv_set"),
                new ServerInfo("fate_ubuntu_server", null, ServerInfo.StatusConnected,
                    "run_command", "run_command_bg", "python_exec", "read_file", "write_file", "list_dir",
                    "http_request", "web_fetch", "get_datetime"),
                new ServerInfo("fates_macbook", null, ServerInfo.StatusConnected,
                    "run_command", "read_file", "write_file", "list_dir", "get_images", "python_exec"),
                new ServerInfo("robinhood", null, ServerInfo.StatusConnected,
                    "get_equity_positions", "get_equity_quotes", "get_equity_orders", "get_accounts", "get_realized_pnl"),
                new ServerInfo("email", null, ServerInfo.StatusConnected,
                    "search_threads", "get_message", "create_draft", "send_message"),
                new ServerInfo("spotify", null, ServerInfo.StatusConnected,
                    "generate_playlist", "get_currently_playing", "search", "save_to_library"),
                new ServerInfo("claude-artifacts", null, ServerInfo.StatusConnected,
                    "list_my_artifacts", "upload_artifact", "update_artifact"),
                new ServerInfo("fates_desktop", null, ServerInfo.StatusError),
                new ServerInfo("github", null, ServerInfo.StatusError)
            };
        }
    }
}
