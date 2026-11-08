# Roadmap

Where this is, what ships next, and what is deferred. Phase 1 is the bar for
"usable": a built game you can chat with EVA through.

## Current state - 2026-11-08

Read this first; the phase sections below describe intent, this describes fact.

`main` is at `d46b596`, clean, pushed. 119 Domain tests pass.

Landed since v1:

- **Islands.** Chat, transcript, tool rail and tool feed are movable,
  resizable surfaces. Corners resize, a six-dot grip at the top right moves,
  a caret collapses. Position/size/collapsed persist per island kind.
- **Real multi-chat.** Chat list, select to open, history sent on the
  request, messages posted back to `/chats/{id}/messages`. No EVA change was
  needed - every endpoint already existed.
- **Send pipeline.** The button sends, queues or stops, matching the web app.
  `PromptQueue` in Domain holds queued prompts.
- **Tool feed above the prompt bar.** A vertical stack, newest at the bottom
  and most visible, older entries fading upward - not a separate box.
- **UI scale.** Minecraft-style size options, default 0.75 (Small).
  `CanvasScaler.ScaleWithScreenSize` ignores `scaleFactor`, so the knob is
  `referenceResolution`: a larger reference makes the UI smaller.
- **Model switching** in-app, and a JetBrains Mono font asset.

Known gaps, in priority order:

1. **The chat path has never run against live EVA.** `EvaSseTransport` and
   every chat call are unverified outside tests. This is the top risk.
2. **Tool duration is still computed client-side.** EVA now emits an
   authoritative `duration_ms` on `tool_end`/`tool_error` (EVA `8007cf7`),
   because the old client-side measurement was reading queue latency, not
   tool time - it reported ~5000 ms for a one-second call. `ConsoleReducer`
   should prefer `duration_ms` when present and keep its `Stopwatch` only as
   the fallback.
3. **Settings parity with the web app** is incomplete: context length,
   temperature, the detected-MCP list, custom theme colour pickers, and the
   "loaded and saved models differ" warning are all missing.
4. **Pausable tools** are not built on either side. Decided shape: EVA owns
   it (`GET`/`POST /tools`), filtering before `ToolNode` and `bind_tools` so
   a paused tool's schema never reaches the model, and server-level toggles
   ship first - 8 servers beats 83 tools, and the schemas cluster by server
   at roughly 12-20k tokens a turn.
5. **The server-side repo has no git remote.** `CloudSystems/AI_Graph_interactions`
   holds `bridge/` and `docs/INTERFACE.md` and is drifting with nowhere to
   push. Fold it in here as `bridge/`, or give it a remote.

## v1 — shipped 2026-11-02

A Windows build that talks to EVA: `Builds/Windows/AgentGraphConsole.exe`,
pointed at an agent by `config.json` beside the exe, so the address changes
without a rebuild. `EvaSseTransport` is wired but was first exercised against
a live server by the user, not by a test.

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

## Phase 2.5 — movable islands

The chat and tool views stop being fixed panels and become **islands**: small
movable, resizable surfaces the user arranges around the graph. The point is
not window chrome for its own sake — it is that each island is a *dynamic
view onto one part of the agent's state*, so new ones can be added as the
system grows rather than the UI being redesigned each time.

Design consequences, decided now because they are expensive to retrofit:

- An island is a **view over `ConsoleState`**, registered by kind, not a
  bespoke panel. Adding one should mean writing a renderer and registering
  it, nothing else.
- Position, size and collapsed state belong in persisted settings, per
  island, keyed by kind — not in the scene.
- Islands must not overlap the graph's interaction: a drag beginning over an
  island belongs to the island, the same arbitration `CameraRig` already does
  for node-drag versus orbit.
- They have to survive the move to world-space in XR, so no island may depend
  on screen-space-only behaviour (no pixel-snapped anchoring, no reliance on
  overlay canvases).

Known islands so far: transcript, tool calls, tool input, agent detail
(model/temperature/context), MCP config, memory subgraph, sub-agent monitor.

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

## Open requests not yet scheduled

Gathered from use of v1; slotted into phases on the next pass.

- Nodes more spread out and **more free-floating / bouncing**
- **Grab the centre node** — it is currently the pinned shell anchor, so it
  needs unpinning plus a home spring that returns it
- **Subtree size sets distance**: a node with a bigger subtree sits further out
- **Do not hop back from a tool until its call completes**
- White theme is too bright: more white with a **greyish centre**, and the
  **edges are invisible on white**
- *Thinking* rendered in italics
- **Icon mode**: a toggle swapping node names for icons per kind — tool,
  model, memory, and each variation
- In-graph node settings: agent sphere size, tool sphere size, edge length
- Click the agent for model / temperature / context; click a tool for its MCP
  config, zooming into the sphere
- VR: grab and hold the centre node, hold trigger to pull every node in close
  and small around it
- Black background and a more **spacey** feel for the graph
- **Separate the scene background colour from the UI colour.** Today one
  bg/fg pair anchors everything, which is why they cannot move apart: every
  surface, hairline and muted tone is mixed between them. Splitting it means
  two anchors -- a scene background for the 3D void, and a UI background the
  panel palette keeps mixing against -- so the void can go pure black while
  the panels stay readable, still on one formula rather than per-theme
  special cases.
