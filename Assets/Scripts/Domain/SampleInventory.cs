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
        /// <summary>
        /// What the offline demo's Settings list shows: <see cref="Eva"/> plus two servers that are not live, so the
        /// toggles, the paused state and the offline row can all be tried without EVA. <c>github</c> is paused and
        /// <c>fates_macbook</c> is offline with an error, which is how the real ones stand. Neither is in the graph (the
        /// graph shows only what is live), and the replay script calls neither.
        /// </summary>
        public static ServerInfo[] EvaWithSettings()
        {
            ServerInfo[] live = Eva();
            ServerInfo[] all = new ServerInfo[live.Length + 2];
            System.Array.Copy(live, all, live.Length);
            all[live.Length] = new ServerInfo("github", null, ServerInfo.StatusConnected,
                new[]
                {
                    new ToolInfo("create_issue", false, 410, "github__create_issue"), new ToolInfo("get_me", false, 120, "github__get_me"),
                    new ToolInfo("list_pull_requests", false, 380, "github__list_pull_requests"), new ToolInfo("search_code", false, 260, "github__search_code")
                },
                true, null, 1170, 0);
            all[live.Length + 1] = new ServerInfo("fates_macbook", null, ServerInfo.StatusError,
                new ToolInfo[0], false, "unhandled errors in a TaskGroup (1 sub-exception)", 0, 0);
            return all;
        }

        /// <summary>
        /// A connected server whose tools carry wire names (<c>server__tool</c>) like the live ones do. The params constructor
        /// would give bare wire names, and two sample servers list run_command: a pause request keyed on the bare name would
        /// switch off BOTH servers' tool in the offline demo, which EVA never does.
        /// </summary>
        private static ServerInfo Srv(string name, string kind, params string[] tools)
        {
            ToolInfo[] infos = new ToolInfo[tools.Length];
            for (int i = 0; i < tools.Length; i++) infos[i] = new ToolInfo(tools[i], false, null, name + "__" + tools[i]);
            return new ServerInfo(name, kind, ServerInfo.StatusConnected, infos, false, null, null, null);
        }

        public static ServerInfo[] Eva()
        {
            return new[]
            {
                Srv("memory", ServerInfo.KindMemory,
                    "vault_search", "vault_read", "vault_write_note", "vault_append", "kv_get", "kv_set"),
                Srv("fate_ubuntu_server", null,
                    "run_command", "run_command_bg", "python_exec", "read_file", "write_file", "list_dir",
                    "http_request", "web_fetch", "get_datetime"),
                // After fate_ubuntu_server: a bare run_command resolves to the first server that lists it.
                Srv("fates_desktop", null,
                    "run_command", "run_command_bg", "python_exec", "read_file", "write_file", "list_dir",
                    "http_request", "web_fetch", "screenshot", "get_datetime"),
                Srv("robinhood", null,
                    "get_equity_positions", "get_equity_quotes", "get_equity_orders", "get_accounts", "get_realized_pnl"),
                Srv("email", null,
                    "search_threads", "get_message", "create_draft", "send_message"),
                Srv("spotify", null,
                    "generate_playlist", "get_currently_playing", "search", "save_to_library"),
                Srv("claude-artifacts", null,
                    "list_my_artifacts", "upload_artifact", "update_artifact")
            };
        }
    }
}
