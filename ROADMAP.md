# Roadmap

Where this is, what ships next, and what is deferred. Phase 1 is the bar for
"usable": a built game you can chat with EVA through.

## Phase 0 — in this version (done)

- Domain / Transport / View / App assemblies, dependencies one way, 65 tests
- Reducer with `run_id` pairing, per-frame delta coalescing, phase machine
- 3D force layout on shells; spheres with labels inside, damped toward camera
- Edge mesh (one draw call), pooled two-hop pulses in the vertex shader
- Ambient particles, URP fog + bloom, terminal colour engine
- Screen-space transcript / tool rail / input readout / cog
- `ReplayTransport` (runs with no server), `EvaSseTransport` (**unverified live**)
- Lock/unlock camera, Fit

## Phase 1 — a build you can actually talk to EVA with

The gate. Everything here is required; nothing here is polish.

1. **Connection screen on launch.** Pick EVA / Claude CLI / Hermes, fields
   prefilled from the last successful connection, Connect, then the graph
   loads. Failure states visible, not silent.
2. **Persisted connection + settings** (`ConsoleSettings` asset or JSON in
   `persistentDataPath`). Never in the scene, never in a committed file.
3. **`EvaSseTransport` verified against live EVA.** Currently never run
   against a server. Send, stream, cancel, reconnect, rejoin an active job.
4. **Topology from the real server.** `GET /mcp-servers` for the inventory
   instead of the hard-coded sample. Needs one additive EVA change: that
   endpoint returns `tool_count` but not tool *names*.
5. **Scroll wheel = zoom.**
6. **Remove the theme name beside the cog.**
7. **Windows player build** that launches, connects and holds a conversation.

Not in Phase 1: Claude CLI and Hermes. Both need the bridge (below).

## Phase 2 — the graph becomes Obsidian-like

1. **Grab and drag a node**, with the rest following through the springs —
   the thing that makes an Obsidian graph feel alive. Needs the simulation
   re-heated while dragging and settled after, not running forever.
2. **Camera follows the graph as a whole**; click a node to focus it.
3. **Thick node outlines** in a darker shade of the theme colour.
4. **Higher-quality spheres and text** — more segments, better SDF settings,
   possibly a proper rim/outline shader rather than fresnel alone.
5. **Label-by-relevance.** ~50 labelled spheres currently collide. Always
   label agent and MCP servers; fade tool labels in by camera distance, hover
   and lit state. This is the actual fix for crowding; bigger shells alone
   only buys ~40%.
6. **Node settings panel**: agent sphere size, tool sphere size, edge length,
   label fade distance. Live, persisted.
7. **Usage-weighted edge length.** Options considered:
   - *frequency* — all-time call counts. Simple, but ossifies: a tool used
     heavily in March still sits close in November.
   - *recency, exponentially decayed* (**recommended**) — half-life of a few
     days, so the graph is a map of what you are working on now and
     self-corrects when you move on.
   - *latency* — slow tools drift outward. Useful diagnostically, but it
     conflates "far" with "slow" rather than "unused".
   - *error rate* — flaky tools pushed out. Better expressed as colour.
   Recency needs a small persisted counter per tool; the others are free.
8. Error/memory tints decay with the glow instead of persisting after a run.

## Phase 3 — detail, memory, sub-agents

1. **Click the agent sphere** → loaded model, temperature, context size,
   job id. The data already arrives in the `meta` event.
2. **Click a tool sphere** → its MCP server config, zooming the camera into
   the sphere.
3. **Memory subgraph.** Point the app at the vault path. The vault MCP
   already exposes `vault_list`, `vault_links` and `vault_search` — so the
   subgraph can be the *real* Obsidian link graph, not an approximation.
   When `vault_search` or `vault_read` fires, light the specific files that
   were touched. This is the feature that makes the console show *what the
   agent explored*, not just which tool it called.
4. **Sub-agent monitor** — a cluster per running sub-agent, each with its own
   phase and tool calls.

## Phase 4 — XR

Immersive (the graph occupies the whole XR space) or passthrough/AR with the
volume off, as a toggle. Move the *graph*, never the camera. World-space UI
moves from screen-space canvases to TMP on world-space canvases. Bloom comes
off and emissive billboards take over — full-screen post is too expensive per
eye at 72–90 Hz.

## Cross-cutting: the bridge

`bridge/` normalises EVA, Claude Code (`--input-format stream-json`, one
long-lived process) and later Hermes into one `/v1` SSE vocabulary. Required
before Claude CLI or Hermes appear in the connection screen. Testable with
curl before any C# exists. See `docs/INTERFACE.md`.

## Phones

Not a Unity iOS build and not Unity WebGL. The browser prototype already
proves the graph runs well in a browser, and `docs/INTERFACE.md` was written
so a second client is cheap: a three.js graph view inside the existing
eva-chat web app, against the same `/v1`. Unity stays the desktop and headset
client. An iOS build would mean a developer account and provisioning for a
personal tool; WebGL on mobile Safari has no threads and a hard memory
ceiling, which this scene would hit.
