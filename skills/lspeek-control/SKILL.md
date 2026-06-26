---
name: lspeek-control
description: Drive a live language server (any LSP) from a chat session. The same action API is exposed three ways by one shared backend — the in-repo "lspeek-canvas" canvas, the "lspeek-mcp" MCP tools, and the "lspeek" TUI. Use when asked to start/test/debug a language server, send raw or composed LSP messages (initialize, hover, definition, completion, diagnostics), reproduce an LSP bug against a local build, or watch the server's logs and work-done progress. For driving a locally built Roslyn (C#/VB) server, also read the roslyn-lspeek skill.
---

# Controlling a Language Server over LSP

This skill explains how to interactively drive **any live LSP server** through `lspeek`: the
generic action API, the startup handshake, and how to read async output. The know-how here is
**server-agnostic** — it works for any language server you can launch over stdio (point it at a
server-config JSON, a built-in name, or a local build).

> Driving a locally built **Roslyn** C#/VB server (`Microsoft.CodeAnalysis.LanguageServer`) has
> extra specifics — build resolution, solution/project loading, pull diagnostics, Roslyn-only
> methods. Those live in the companion **`roslyn-lspeek`** skill, which builds on this one.

In this repo (`lspeek`) a single **backend** (`lspeek-http`) owns the server lifecycle: it
spawns `<server> --stdio` (or whatever the server config specifies), speaks the LSP wire protocol,
buffers **every** frame in both directions, auto-answers infrastructure requests, and records the
server's logs / `$/progress` / diagnostics for you to read back. You never talk to the server
directly — you call a small, fixed set of **actions** and the backend does the wire work.

Those actions are surfaced **identically by three frontends**, so the driving know-how below is the
same no matter which you use:

| Frontend | How you call an action | Notes |
| --- | --- | --- |
| **`lspeek-canvas` canvas** (`.github/extensions/lspeek-canvas`) | `invoke_canvas_action({ instanceId, actionName, input })` | Also renders a clickable timeline + manual JSON send box. |
| **`lspeek-mcp` MCP server** | call the MCP tool whose name **is** the action (e.g. `start_server`, `lsp_request`) with the `input` fields as arguments | Best for headless agent flows. |
| **`lspeek` TUI** | interactive (`lspeek <server>`) or scripted (`lspeek <server> --json script.json`) | Same backend; a terminal UI over it. |

Each frontend spawns its **own private backend** (one live server per canvas `instanceId` / per MCP
process / per TUI process). Pick whichever frontend the user is using; the actions, inputs, and
result envelopes are the same.

## Prerequisites

- **A backend the frontend can spawn.** The frontends locate and spawn `lspeek-http`. Installed
  tools (`dotnet tool install --global lspeek` / `lspeek-mcp`) are **self-contained and
  platform-specific** (published for the full RID set: win/linux/osx, x64/arm64, glibc/musl) and
  **bundle a self-contained copy of the backend**, so nothing extra is needed — no .NET runtime, no
  separate download. When working from the repo, build once with `dotnet build lspeek.slnx`; each
  frontend then finds the backend in a `backend/` folder beside its own build output, and the
  **canvas** auto-discovers the in-repo backend build directly. The canvas can also run fully
  off-repo: with no local build it falls back to `dotnet dnx lspeek-http`, which fetches the latest
  published backend tool from NuGet (cached after the first run), so it just needs the .NET SDK on
  PATH. You can point the `lspeek`/`lspeek-mcp` frontends at a specific host with the `LSPEEK_HTTP`
  environment variable (a host file or a directory containing it). The backend bundled in
  `lspeek`/`lspeek-mcp` and the one acquired via dnx are the same
  **self-contained, trimmed, ReadyToRun (R2R)** build for the matching RID (no separate .NET runtime
  needed); R2R cross-compiles, so all RIDs (win/linux/osx, x64/arm64, glibc/musl) are built on one runner.
- **The frontend you intend to use is available:**
  - Canvas: confirm with `list_canvas_capabilities(canvasId:"lspeek-canvas")`. It lives in this
    repo under `.github/extensions/lspeek-canvas`, so it is auto-discovered when working in the
    repo. (If a stale user-scoped copy at `~/.copilot/extensions/lspeek-canvas` also exists,
    pass `extensionId:"project:lspeek-canvas"` to disambiguate, or uninstall the old copy.)
  - MCP: the `lspeek-mcp` server must be registered in the agent's MCP configuration.
  - TUI: run `lspeek` (the `lspeek` dotnet tool) or `dotnet run --project src/Client.Tui`.
- **A server for the backend to run.** The backend runs an existing server; it does **not** build it
  for you. You can point it at:
  - a **server-config JSON** describing any LSP server (`{ "name", "command", "arguments" }`),
  - a **built-in name** (e.g. `server:"roslyn"` for the released Roslyn package server), or
  - a **local build** of a server (e.g. a Roslyn worktree build — see `roslyn-lspeek`).
- `dotnet` must be on PATH (the SDK matching the repo's `global.json`) when the server is a .NET app.

## The actions (your API)

For the **canvas**, open it once then call actions with
`invoke_canvas_action({ instanceId, actionName, input })`; **one server process is owned per
`instanceId`** (use a fresh `instanceId` for an independent server). For the **MCP**, call the
same-named tool with the input fields as arguments. For the **TUI**, the interactive UI and JSON
scripts map onto the same actions.

| Action | Purpose |
| --- | --- |
| `start_server` | Resolve + spawn the server from a server config, a built-in name, or a local build. Replaces any running one for this instance/session. |
| `stop_server` | Graceful `shutdown`+`exit`, then kill. |
| `server_status` | Running state, pid, resolved dll, command line, message/pending counts. |
| `lsp_request` | Send a request and **wait** for its response. Returns `{ result }` or `{ error }`. |
| `lsp_notify` | Send a notification (no response). |
| `send_raw` | Send an arbitrary JSON-RPC object/array verbatim; optional `waitForResponse`. |
| `respond_to_request` | Manually answer a server→client request (only if `autoRespond:false`). |
| `get_messages` | Read buffered traffic. Poll with `sinceSeq` to read new async output. |
| `wait_for_message` | Block until a matching server message arrives (by `method` or `containsText`). |
| `clear_messages` | Clear the buffer (server keeps running). |
| `help` | Built-in cheat-sheet. |

Notes:
- Every action returns a JSON envelope: `{ ok:true, ... }` on success, `{ ok:false, error }` on a
  transport failure. `lsp_request` resolves with the **full** response: success is
  `{ ok:true, result }`; an LSP-level error is `{ ok:true, error }` (the call succeeded but the
  server returned a JSON-RPC error). A transport failure/timeout is `{ ok:false, error }`.
- Request ids are managed for you (the backend uses `rqc-N`). Don't set your own id on `lsp_request`.
- In the canvas, every frame is also visible in the UI; you don't need to echo raw JSON to the user.

## Standard startup sequence

```
1. (canvas only) open_canvas(canvasId:"lspeek-canvas", instanceId:"lsp1")
2. start_server  { serverPath | server:"<name|config.json>", logLevel:"Information" }
3. lsp_request   { method:"initialize", params:{ ...see below... } }   // returns server capabilities
4. lsp_notify    { method:"initialized", params:{} }
5. load the workspace/projects as the server expects                  // server-specific; see below
6. wait for the server's "ready" signal (e.g. workspace/projectInitializationComplete)
7. open documents + send feature requests (hover, definition, completion, diagnostics, ...)
8. stop_server
```

### `start_server`

Point the backend at a server in one of these ways:

- `server` — a **built-in name** (e.g. `"roslyn"`) or a **path to a server-config JSON file**. Use
  this to drive any LSP server with `{ "name", "command", "arguments" }`.
- `serverPath` (or `repoRoot`) — a **local build** of a server: a repo root/worktree the backend
  searches, an output directory containing the server, or a full path to the server dll/executable.
  Build resolution is server-specific (for Roslyn the backend searches `artifacts/bin`; see
  `roslyn-lspeek`).

Useful options: `logLevel` (`Trace|Debug|Information|Warning|Error|None`), `autoLoadProjects`
(bool or max count, for servers that support it), `extensionLogDirectory` (on-disk logs), `extraArgs`
(verbatim CLI args, e.g. `["--telemetryLevel","off"]`), `extensions` (repeatable `--extension`),
`dotnetPath`, `cwd`, `env` (extra environment variables), `autoRespond` (default `true` — see below).

A server's plain `Console.Out` is typically captured by the backend as `kind:"stderr"` records (to
keep stdout clean for JSON-RPC); structured logs arrive as `window/logMessage` notifications.

### `initialize` params

Minimum that works:

```json
{
  "processId": null,
  "clientInfo": { "name": "lspeek" },
  "rootUri": "file:///C:/path/to/workspace",
  "capabilities": { "textDocument": {}, "workspace": { "configuration": true, "workspaceFolders": true } },
  "workspaceFolders": [ { "uri": "file:///C:/path/to/workspace", "name": "ws" } ]
}
```

- **All file system paths in LSP are `file://` URIs**, not Windows paths. On Windows use
  `file:///C:/dir/File.cs` (forward slashes, drive letter, three slashes).
- Include `workspaceFolders` (and the matching capabilities) whenever the server discovers its
  workspace from the folders (e.g. autoload).
- The response `result.capabilities` tells you what the server supports (hover, definition,
  completion, diagnostics, semantic tokens, etc.).

## Loading the workspace

Feature requests only return meaningful results once the relevant project(s)/workspace are loaded.
**How** you load is server-specific (e.g. Roslyn uses `solution/open` / `project/open` / autoload —
see `roslyn-lspeek`), but the **principles are the same** for every server:

- Loading is **asynchronous**. A load notification returning does **not** mean loading finished.
- Wait for the server's **completion signal** before issuing feature requests. Many servers emit a
  notification you can block on (e.g. Roslyn's `workspace/projectInitializationComplete`); use
  `wait_for_message { method:"<signal>", timeoutMs:600000 }`. Large workspaces can take minutes — use
  a generous `timeoutMs`.
- While loading, watch progress with `window/logMessage` lines and `$/progress` work-done
  notifications via `get_messages { sinceSeq:<lastSeq> }` or `wait_for_message { containsText:"..." }`.

### Auto-respond (why loading can hang if you disable it)

Servers often send **requests back to the client** during startup/loading and **block** on the
answers:
- `workspace/configuration` — must be answered (the backend replies with an array of `null`s, one per
  requested item, i.e. "use defaults").
- `window/workDoneProgress/create` — often **awaited** by load initializers; if unanswered, loading
  may never begin.
- `client/registerCapability`, `window/showMessageRequest`, etc.

The backend **auto-answers all of these by default** (`autoRespond:true`). Keep it on unless you are
specifically testing the client side. If you set `autoRespond:false`, you must watch
`get_messages` for `direction:"recv", kind:"request"` and reply with
`respond_to_request { id:<id>, result:<...> }` yourself — otherwise loading and progress stall.

## Composing common feature requests

Open the document first (so the server tracks live buffer content), then query it. Use the server's
`languageId` (e.g. `"csharp"` for C#, `"typescript"`, `"rust"`, ...). Positions are **zero-based**
`{ line, character }`.

Open a document:
```
lsp_notify { method:"textDocument/didOpen", params:{ textDocument:{
  uri:"file:///C:/proj/Program.cs", languageId:"csharp", version:1, text:"<full file text>" } } }
```

Hover:
```
lsp_request { method:"textDocument/hover", params:{
  textDocument:{ uri:"file:///C:/proj/Program.cs" }, position:{ line:10, character:15 } } }
```

Go to definition:
```
lsp_request { method:"textDocument/definition", params:{
  textDocument:{ uri:"file:///C:/proj/Program.cs" }, position:{ line:10, character:15 } } }
```

Completion (then optionally `completionItem/resolve`):
```
lsp_request { method:"textDocument/completion", params:{
  textDocument:{ uri:"file:///C:/proj/Program.cs" }, position:{ line:10, character:15 },
  context:{ triggerKind:1 } } }
```

References:
```
lsp_request { method:"textDocument/references", params:{
  textDocument:{ uri:"file:///C:/proj/Program.cs" }, position:{ line:10, character:15 },
  context:{ includeDeclaration:true } } }
```

Document symbols / workspace symbols:
```
lsp_request { method:"textDocument/documentSymbol", params:{ textDocument:{ uri:"file:///C:/proj/Program.cs" } } }
lsp_request { method:"workspace/symbol", params:{ query:"MyType" } }
```

**Diagnostics** — servers use either **push** (`textDocument/publishDiagnostics`, watch via
`get_messages`) or **pull** (request `textDocument/diagnostic` / `workspace/diagnostic` explicitly).
Check the server's `initialize` capabilities to see which it advertises (Roslyn uses pull — see
`roslyn-lspeek`).

Anything not wrapped by a convenience action can be sent with `send_raw`.

## Reading async output

Server output that is **not** a direct response (logs, progress, diagnostics, load completion,
server→client requests) is captured in the buffer. Read it with:
- `get_messages { sinceSeq:<lastSeq>, methodContains:"...", kinds:["notification"] }` — poll; pass the
  returned `lastSeq` next time so you only get new frames. Use `includePayload:false` for a compact
  index, then re-fetch a single `seq` with payload.
- `wait_for_message { method | containsText, timeoutMs }` — block for a specific event.

Each record has `{ seq, time, direction(send|recv|meta), kind(request|response|notification|error|stderr|info|batch), method, id, summary, payload }`.
A `response` record's `method` is the originating request's method (recovered by id), so responses can be tied back to their request and filtered by `methodContains`.

## Manual / exploratory sending

In the canvas, the user can paste raw JSON-RPC into the UI's send box. From any frontend you can use
`send_raw { message:{...}, waitForResponse:true }` for full control (custom ids, batches, malformed
messages for negative testing). `jsonrpc:"2.0"` is added if you omit it.

## Shutting down

`stop_server` performs the LSP `shutdown` request + `exit` notification, then force-kills if needed.
Always stop the server when you're done so the server process doesn't linger. A non-zero exit code
on shutdown is normal for some servers (they log an exception when the client disconnects). Closing a
canvas panel (or exiting the MCP/TUI process) also tears down that frontend's private backend and server.

## Gotchas checklist

- Paths must be `file://` URIs (`file:///C:/...` on Windows), never bare Windows paths.
- `initialized` must be sent after the `initialize` **response**, before loading the workspace.
- Don't treat a load notification returning as "loaded" — wait for the server's completion signal.
- Keep `autoRespond:true` unless deliberately testing client behavior; otherwise loading hangs.
- Feature results are empty/partial until the owning project/workspace is loaded; open the document
  with `textDocument/didOpen` before per-file requests.
- The server's plain logs are usually on **stderr** (captured as `kind:"stderr"`); structured logs are
  `window/logMessage`. Raise detail with `logLevel:"Trace"`.
- Big workspaces load slowly — use large `timeoutMs` on waits and on pull `workspace/diagnostic`.
- One live server per canvas `instanceId` / MCP process / TUI process; `start_server` replaces a
  running one. The message buffer persists across start/stop within an instance until `clear_messages`.
