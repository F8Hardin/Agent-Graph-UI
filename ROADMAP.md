# Roadmap

Where this is, what ships next, and what is deferred. Phase 1 is the bar for
"usable": a built game you can chat with EVA through.

## Current state - 2026-11-09

Read this first; the phase sections below describe intent, this describes fact.

`main` is at `288169d`, clean, pushed. 170 EditMode tests pass. The Windows build
in `Builds/Windows` was made from that commit on 2026-11-09.

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
- **Chat bubbles with Markdown (UNI-1).** User bubbles right, agent bubbles
  left, 75% max width, tinted by the `bubbleUser` / `bubbleEva` theme tokens.
  `MarkdownTmp` (Domain, engine-free) turns the agent's reply into TMP rich
  text, with fenced code as its own panel; only the open message repaints
  while a reply streams. Links render but are not yet clickable.
- **Model switching** in-app, and a JetBrains Mono font asset.
- **Swappable node styles (UNI-25..28).** A node's body is a `NodeVisual`
  (`View/NodeStyles/`), picked by `NodeStyleKind` and swapped live from
  Settings. Three styles: Plasma Dome (default), Orbital Core, Hologram Wire.
  All motion is procedural in the shader, driven by a phase float
  accumulated in C#; no ParticleSystem per node.
- **Glass-tube edges (UNI-30).** Six times thicker, the same fresnel rim as
  the domes, a pixel-width floor that scales with resolution, colours from
  the node tier tokens.

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

## Graphics overhaul - started 2026-11-09

The goal: the graph and the UI look professional, spacey and alive, as one
designed product. Graphics work lives in View and the shader/material folders
and never touches Domain, Transport, the reducer or the wire contract. Cards
are on the EVA Kanban (Area = Unity App).

Art direction, approved by the user from a three-way mock: keep all three node
looks as selectable styles, in the order Plasma Dome, Orbital Core, Hologram
Wire. Plasma is the default; Hologram is the cheap style for the headset (one
draw, no bloom).

### Next up, in this order (set 2026-11-10)

This list is the queue. The top item is built first; nothing below it starts
until it ships. The phase table further down is the map, not the order.

| # | Card | What | Why it is here |
|---|---|---|---|
| done | UNI-31 | The main EVA node is clickable and grabbable like every other bubble | Shipped 5cdc02c, in review. The layout now anchors on the agent's position and a dropped agent stays put. Verified through the drag API, not yet by hand. |
| 1 | UNI-22, UNI-23 | Memory graph: recreate the Obsidian vault as a graph off the memory node (notes as nodes, wiki-links as edges), and show a memory search sweeping its files, lighting the notes it touched | Moved to the front on 2026-11-10. First cut lights notes from the paths already in `vault_search` / `vault_read` tool events; EVA-6 makes the hits exact. Open: where the note and link list comes from, since a web or headset client cannot read the vault from disk. |
| 2 | UNI-37 | Lightsaber edges: when a node is called, light extends along the edge toward it (agent, then server, then tool), holds for the whole call, and retracts when the call ends. The tool node stays highlighted until its call completes | Edges must glow as well as nodes. Today the edge gets one bead and a decaying heat value; only the node glow is held until `tool_end` for the same `run_id`, and that is unverified against live EVA. |
| 3 | UNI-38 | Edges are real 3D rods (cylinders), not flat ribbons. Looser, string-like connections may follow as a second edge style | UNI-30 shaded a camera-facing ribbon to look round; it still reads flat. Real cylinder geometry in the one edge mesh, one draw call, with room for an edge-style switch. |
| 4 | UNI-39 | Physics: a dropped node stays exactly where it is dropped; grabbing one node does not set the others floating; others move only through their connecting edges; children follow a moved parent | Observed: a dropped node is pulled toward other nodes. A drag re-heats one global alpha, so every node moves under repulsion, tier shell springs and fixed-rest link springs, and release unpins the node into those forces. Spreading by subtree size belongs to the initial placement only, not to a force that keeps acting. |
| 5 | UNI-40 | Node size from subtree size: the agent biggest, tool-heavy MCP servers large; subtrees with several children placed more spread out when the graph is created | Makes the graph show what takes up context. Radius is fixed per tier today. |
| 6 | UNI-20 | Graph built from the live `/tools` catalogue, with real server status | Fixes "fates_desktop is always red": the graph is seeded from the hard-coded `SampleInventory`, where the desktop and github are written as errored with no tools and the offline MacBook as connected. Nothing reads live status. |

After those: backdrop controls (UNI-32), spacey backdrop and white-theme
centre (UNI-29), high-quality fog (UNI-33), better plasma orbs (UNI-34), the
UI pass, then the web and XR builds (UNI-35, UNI-36). Sounds with a mute
toggle (UNI-41) are noted for later.

### Phase map

| Phase | What | Cards | State |
|---|---|---|---|
| 1 | Audit and art direction | - | done |
| 2 | Node style system, three styles, Settings picker | UNI-25, 26, 27, 28 | shipped, in review |
| 3 | Backdrop and depth: black void, parallax layers, scene background split from UI colour, white page with a greyish centre | UNI-29, WEB-3 | next |
| 3b | Backdrop controls: fog toggle; particle field distance, amount, colour, min/max size, inner/outer spawn radius | UNI-32 | next |
| 3c | High-quality, fog-like cloud instead of flat scene fog plus motes | UNI-33 | open |
| 4 | Edges: thicker and rim-lit (shipped), real tube geometry, traversal animation with the highlight held until the call returns | UNI-30, 37, 38 | UNI-30 shipped; 37, 38 queued |
| 5 | Motion and layout: grab the centre node, local drag physics, node size and spread by subtree size, camera focus and lock | UNI-31, 39, 40, 15, 16, 17, 19 | UNI-31 first |
| 6 | UI pass: islands, chat, tool feed, settings and top bar in one visual language | UNI-2, 7, 8, 9, 10, 11 | open |
| 7 | Performance and headset check | UNI-36 | open |
| 8 | Sound: thinking, data moving, tool calls; mute in the menu | UNI-41 | later |

Known rough edges in what shipped, to close before these cards move to Done:

- Nothing new has been looked at on the white themes.
- Error/down, memory and selected states are implemented in each style but
  have not been seen on screen.
- Hologram Wire labels are hard to read over the wires at the default framing.
- The edges are shaded ribbons, not tubes (UNI-38).
- Orbital Core's core does not yet grow with call count (UNI-18).
- Plasma Dome's motes are shader dots; the user wants higher-quality orb
  assets (UNI-34), likely a Blender-rendered sprite or flipbook. His
  glowing-dome reference image is still outstanding.
- `GraphView.cs` is still over the 400-500 line standard (about 717).

Fog options for UNI-33, not yet chosen: layered soft noise billboards (cheap,
headset-safe); a raymarched noise volume in a fullscreen pass (best look,
desktop only); 3D-noise textures baked in Blender.

## Platform targets

Asked for 2026-11-09: a web build and an XR build, to test soon.

- **WebGL (UNI-35).** The build module is installed. Blocker: Transport is
  plain .NET (`HttpClient`, worker threads), which the browser does not have,
  so it needs a second `IAgentTransport` built on `UnityWebRequest` or browser
  fetch, A look-only build on `ReplayTransport` can come
  first. Graphics run in the visitor's browser; the server only serves the
  files. Access is over Tailscale with a manually entered address; CORS, a
  public address and auth are noted for later scaling and ignored for now.
  This reopens the "Phones" decision below, which ruled WebGL out for
  mobile Safari; desktop browsers do not share that memory ceiling.
- **XR on Quest 3 (UNI-36).** OpenXR 1.19 and XR Interaction Toolkit 3.7 are
  already in the project. The Android build module is not installed, so a
  standalone headset build needs it added in Unity Hub; a PC-tethered OpenXR
  build over Link can be tried without it. Phase 4 below still applies:
  islands move to world-space canvases, and bloom comes off.

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

Each line now names its card where one exists.

- Nodes more spread out and **more free-floating / bouncing** (overhaul phase 5)
- **Grab the centre node** (UNI-31, must-have) — it is currently the pinned shell anchor, so it
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
- Black background and a more **spacey** feel for the graph (UNI-29)
- **Fog toggle and particle field controls** in settings (UNI-32)
- **High-quality fog-like cloud** (UNI-33) and **higher-quality plasma orbs** (UNI-34)
- **Web build and XR build** to test soon (UNI-35, UNI-36)
- **Separate the scene background colour from the UI colour.** Today one
  bg/fg pair anchors everything, which is why they cannot move apart: every
  surface, hairline and muted tone is mixed between them. Splitting it means
  two anchors -- a scene background for the 3D void, and a UI background the
  panel palette keeps mixing against -- so the void can go pure black while
  the panels stay readable, still on one formula rather than per-theme
  special cases.
