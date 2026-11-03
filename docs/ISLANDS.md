# Islands, chat, and the send pipeline

The spec for the next pass. Ordered by the priority the user set.

**None of this needs a change to EVA.** Every endpoint already exists, and
the queue/stop behaviour is a client pattern, not a server one. Confirmed
against `eva_fast_api.py`:

    GET    /chats                    list
    POST   /chats                    create
    GET    /chats/{id}               one chat with its messages
    DELETE /chats/{id}               delete one
    DELETE /chats                    delete all
    POST   /chats/{id}/messages      persist a message
    POST   /submit_prompt            run (returns X-Job-Id)
    GET    /jobs/{id}/stream?start=N resume
    POST   /jobs/{id}/stop           cancel
    GET    /jobs/active              rejoin

## 0. The island frame (everything else sits in it)

A generic frame, not a per-panel implementation. One `Island` component
wrapping any content:

- **Move**: a six-dot grip at the **top right** of the frame. Drag moves the
  island. Only the grip moves it — dragging the body scrolls or selects.
- **Resize**: all four **corners**, with a minimum size per island kind.
- **Persisted** per island kind: position, size, collapsed. Stored with the
  rest of the settings, not in the scene.
- **Arbitration**: a pointer-down over an island belongs to the island, not
  to the graph. `CameraRig` already distinguishes node-drag from orbit; this
  is a third case and must be handled in the same place rather than by each
  island guessing.
- **No screen-space-only behaviour.** These move to world-space canvases in
  XR, so no pixel snapping and nothing that assumes an overlay canvas.

Islands to frame now: Chats, Chat, Tool Calls, Tool Input, Settings.

## 1. Chat like the web client

Today there is one implicit conversation. It becomes two islands:

- **Chats** — the list from `GET /chats`: title, last-run time, message
  count. Select one and it loads via `GET /chats/{id}`; the chat island
  grows to show it. New chat, delete chat.
- **Chat** — the transcript of the selected chat, the same content the
  transcript island shows now, but scoped to a chat id.

State lives in Domain (`ChatList`, `ChatSummary`, selected id) behind a
Domain-owned interface the way `IModelSelector` already is; Transport
implements it. The View must not gain a reference to Transport.

Persist the selected chat id, so relaunching lands back where it was — the
web client already does this with `eva.lastChatId`.

## 2. The input grows vertically

The prompt field currently scrolls text off to the left. It should wrap and
**grow upward** as lines are added, to a cap (around 6–8 lines) and then
scroll internally. Enter sends; Shift+Enter inserts a newline. The island
above it reflows rather than being overlapped.

## 3. Settings matching the web client

Same fields, same meanings, so the two clients do not drift:

    endpoint        showThinking     showRaw
    showTimings     showModel        theme / customTheme

Plus what is specific here: transport selection and EVA base URL (today only
in `config.json`), and the node-settings group from ROADMAP Phase 2 once it
exists. Settings open as an island like everything else.

## 4. Send / queue / stop

Copy the web client's semantics exactly (`ChatView.tsx`):

- idle: the button **sends**.
- streaming: typing shows "will queue"; the button **queues**, and queued
  messages send automatically when the run finishes.
- streaming with a queue: **Stop** cancels the run *and clears the queue* —
  one control, labelled for what it will do.
- the queue count is visible ("2 messages queued — will send automatically").

## 5. Tool input popup: longer, and stacked

Today one card appears and retires shortly after its `tool_end`. Instead:

- **Stack** concurrent calls rather than replacing — newest on top, a few
  visible, older ones collapsing.
- **Hold** each card for the whole life of its call plus a readable dwell
  after it resolves, so a fast call is still legible.
- Show the resolved state on the card (ok / error, duration) before it goes.

## 6. Hold the tool highlight until the call returns

The node highlight currently decays on a timer, so it can fade while the
call is still running. It must be **held** while that call's status is
Running and released only on `tool_end` / `tool_error` for the same
`run_id`.

The reducer already tracks status per `run_id`; this is a View change — the
glow needs a "pinned while running" flag rather than pure decay. No EVA
change, and no reducer change.
