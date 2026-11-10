namespace AgentConsole.Domain
{
    /// <summary>
    /// The offline demo's graph, used by the Replay transport only. A live agent never sees it: the graph is built
    /// from EVA's GET /tools (<see cref="InventoryController"/>). It is a believable "everything is up" picture and
    /// stays consistent with the replay script, whose calls name the servers memory, robinhood and fate_ubuntu_server.
    /// It used to carry fates_desktop and github as errors and fates_macbook as connected, which showed the real
    /// machines the wrong way round and is what UNI-20 was filed about. <c>memory</c> is its own cluster, not three
    /// ordinary tools.
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
                // After fate_ubuntu_server: a bare run_command resolves to the first server that lists it.
                new ServerInfo("fates_desktop", null, ServerInfo.StatusConnected,
                    "run_command", "run_command_bg", "python_exec", "read_file", "write_file", "list_dir",
                    "http_request", "web_fetch", "screenshot", "get_datetime"),
                new ServerInfo("robinhood", null, ServerInfo.StatusConnected,
                    "get_equity_positions", "get_equity_quotes", "get_equity_orders", "get_accounts", "get_realized_pnl"),
                new ServerInfo("email", null, ServerInfo.StatusConnected,
                    "search_threads", "get_message", "create_draft", "send_message"),
                new ServerInfo("spotify", null, ServerInfo.StatusConnected,
                    "generate_playlist", "get_currently_playing", "search", "save_to_library"),
                new ServerInfo("claude-artifacts", null, ServerInfo.StatusConnected,
                    "list_my_artifacts", "upload_artifact", "update_artifact")
            };
        }
    }
}
