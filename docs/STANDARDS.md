# Coding standards

This project is going to grow — more tools, more agents, more islands, then
XR. Everything here exists to keep that growth *additive*. The test of each
rule is the same: when something new arrives, does it slot in, or does it
force a redesign?

These are not aspirations. The code currently holds to them, and a change
that breaks one should be rejected or should change this file first.

## 1. The layering is the law

    View  ->  Domain  ->  Transport
    App (composition root) sees all three. Nothing else does.

Enforced by asmdef, not by discipline:

- **Domain** references nothing. No `UnityEngine` at all. It is plain C# so
  the reducer can be tested headlessly — no editor, no headset, no agent.
- **Transport** references Domain only, and is also `noEngineReferences`, so
  nothing on a worker thread can touch a Unity type even by accident.
- **View** references Domain only. It must never see Transport. If a view
  needs something from the wire, the reducer exposes it.
- **App** is the only assembly allowed to know about all three.

If a change needs View to reference Transport, the change is wrong. That is
what the `IAgentTransport`-in-Domain move was about.

## 2. The 60 fps layer allocates nothing and never touches the reducer

Glow, link heat, pulses, label billboards and layout ticks run in `Update()`
over mutable fields and preallocated arrays. No LINQ, no closures, no string
concatenation, no `new` per frame. State changes arrive as reducer events;
frames read fields.

Anything time-based uses the frame-rate-independent form
(`1 - Mathf.Exp(-k * Time.deltaTime)`), never a raw `Lerp(a, b, k)` per
frame, which silently changes behaviour with frame rate.

## 3. One reducer, one state

`ConsoleReducer` is the single place wire events become state. The graph, the
transcript, the tool rail and every future island are *readers*. No view
parses a `data:` line, splits a tool name, or keeps its own copy of a message
list.

Adding an island means writing a renderer over `ConsoleState` and registering
it. If it means touching the reducer's internals, the state shape is wrong.

## 4. The server owns the truth

The client is a projection. Specifically:

- IDs (`chat_id`, `job_id`, `run_id`) come from the server and are never
  invented locally.
- Streams append; nothing else mutates a message mid-flight. One writer per
  piece of state.
- Reconnect is normal, not exceptional. A headset sleeps; the client rejoins
  a running job rather than losing it.

This is not theory. The web client was bitten by a restore effect that
overwrote a live turn, and by duplicate replies from a save racing a chat
switch. Both were single-writer violations.

## 5. Tests

Domain is headless-testable and stays that way. Current suite: 65 EditMode
tests, all green; that number only goes up.

Every bug that reaches the running app gets a test before it gets a fix —
the `run_id` pairing, the `llm_end`-per-turn behaviour and the reconnect
cursor are all in the suite because they were real.

Transport is tested against a fake HTTP handler. `EvaSseTransport` against a
*live* server is still unproven; that gap is known and tracked.

## 6. Comments explain why, not what

The codebase does this already — keep it. A comment that restates the line
below it is noise; a comment that records *the reason a non-obvious choice
was made* is the most valuable thing in the file. Examples worth imitating:

- why the borders are a rim term and not an inverted hull
- why the layout settles and then freezes
- why labels slerp with damping instead of snapping
- why `run_id` pairing exists at all

When a decision had a rejected alternative, name the alternative.

## 7. File size and responsibility

Split at roughly 400–500 lines, or sooner when a file has grown a second
responsibility.

Current offender, tracked: `View/GraphView.cs` at 678 lines does layout
ticking, sphere and label management, the edge mesh, pulses and glow decay.
It should become a coordinator over three or four focused components before
islands and icon mode add more to it.

## 8. Configuration and secrets

- No credential ever enters this repo, the scene, or `config.json`.
- Runtime connection settings live in `config.json` beside the executable, so
  a build is repointed without a rebuild.
- Defaults live in **code**, not in the scene. The scene is regenerated from
  them by the editor menu **Agent Console/Build Scene** — so a tuned value
  that only exists as a serialized scene field will be lost, and belongs in
  the source default instead.
- Nothing reads `~/.tokens`, and `.env` is gitignored.

## 9. Repository hygiene

- **No `.bak` files, ever.** Git is the backup. The same goes for `.orig`,
  `.tmp`, scratch scripts and captured screenshots — none of it belongs in
  `Assets/`.
- Unity YAML (`.unity`, `.prefab`, `.asset`, `.mat`) stays **text** and
  diffable. Only real binaries go through LFS. Routing scenes through LFS
  turns every change into an opaque blob.
- `Library/`, `Builds/` and IDE files are ignored and stay that way.
- Commit messages say what changed and *why*, including the alternative not
  taken where one existed.

## 10. Adding things should be boring

The architecture is working if each of these is a small, local change:

- **a new agent** — one bridge adapter, zero client changes
- **a new tool or MCP server** — nothing at all; the graph grafts unknown
  nodes on first use and recovers the server from the `server__tool` name
- **a new island** — a renderer over `ConsoleState` plus a registration
- **a new theme** — five base colours; everything else is mixed from them

If one of those starts requiring edits in three assemblies, stop and fix the
seam rather than pushing through.
