---
name: roslyn-lsp-control
description: Drive a live Roslyn C#/VB language server over LSP from a chat session. The same action API is exposed three ways by one shared backend — the in-repo "roslyn-lsp-tester" canvas, the "lspeek-mcp" MCP tools, and the "lspeek" TUI. Use when asked to start/test/debug the Roslyn language server, send raw or composed LSP messages (initialize, hover, definition, completion, diagnostics, solution/project open), reproduce an LSP bug against a local build/worktree, or watch the server's logs and work-done progress.
---

# Controlling the Roslyn Language Server over LSP

This skill explains how to interactively drive a **live Roslyn language server** (the
`Microsoft.CodeAnalysis.LanguageServer` host that powers the C# Dev Kit / VS Code C# extension)
built from any local repo or worktree.

In this repo (`lspeek`) a single **backend** (`lspeek-backend`) owns the server lifecycle: it
spawns `dotnet <Microsoft.CodeAnalysis.LanguageServer.dll> --stdio`, speaks the LSP wire protocol,
buffers **every** frame in both directions, auto-answers infrastructure requests, and records the
server's logs / `$/progress` / diagnostics for you to read back. You never talk to the server
directly — you call a small, fixed set of **actions** and the backend does the wire work.

Those actions are surfaced **identically by three frontends**, so the driving know-how below is the
same no matter which you use:

| Frontend | How you call an action | Notes |
| --- | --- | --- |
| **`roslyn-lsp-tester` canvas** (`.github/extensions/roslyn-lsp-tester`) | `invoke_canvas_action({ instanceId, actionName, input })` | Also renders a clickable timeline + manual JSON send box. |
| **`lspeek-mcp` MCP server** | call the MCP tool whose name **is** the action (e.g. `start_server`, `lsp_request`) with the `input` fields as arguments | Best for headless agent flows. |
| **`lspeek` TUI** | interactive (`lspeek roslyn`) or scripted (`lspeek roslyn --json script.json`) | Same backend; a terminal UI over it. |

Each frontend spawns its **own private backend** (one live server per canvas `instanceId` / per MCP
process / per TUI process). Pick whichever frontend the user is using; the actions, inputs, and
result envelopes are the same.

## Prerequisites

- **A backend the frontend can spawn.** The frontends locate and spawn `lspeek-backend`. Installed
  tools (`dotnet tool install --global lspeek` / `lspeek-mcp`) **bundle** the backend, so nothing
  extra is needed. When working from the repo, build it once with `dotnet build lspeek.slnx`
  (produces `src/Client.Backend/bin/<Config>/<tfm>/lspeek-backend(.exe)`, which the canvas/MCP
  auto-discover). You can also point them at a specific host with the `LSPEEK_BACKEND` environment
  variable (a host file or a directory containing it). The bundled backend is framework-dependent and
  needs the ASP.NET Core shared runtime (ships with the .NET SDK).
- **The frontend you intend to use is available:**
  - Canvas: confirm with `list_canvas_capabilities(canvasId:"roslyn-lsp-tester")`. It lives in this
    repo under `.github/extensions/roslyn-lsp-tester`, so it is auto-discovered when working in the
    repo. (If a stale user-scoped copy at `~/.copilot/extensions/roslyn-lsp-tester` also exists,
    pass `extensionId:"project:roslyn-lsp-tester"` to disambiguate, or uninstall the old copy.)
  - MCP: the `lspeek-mcp` server must be registered in the agent's MCP configuration.
  - TUI: run `lspeek` (the `lspeek` dotnet tool) or `dotnet run --project src/Client.Tui`.
- **A built Roslyn server.** The backend runs an existing build; it does **not** build the server
  for you.
  - Build output lives at:
    `<repoRoot>/artifacts/bin/Microsoft.CodeAnalysis.LanguageServer/<Config>/<tfm>/Microsoft.CodeAnalysis.LanguageServer.dll`
    (e.g. `Debug/net10.0`). `<tfm>` is `$(NetVSCode)` (currently `net10.0`).
  - To build from a worktree:
    `dotnet build src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Microsoft.CodeAnalysis.LanguageServer.csproj`
    (or `dotnet build Roslyn.slnx` / the `LanguageServer` project). Use `dotnet build`, **not** publish.
  - Alternatively, target the **released package** server with `server:"roslyn"` (see `start_server`).
- `dotnet` must be on PATH (the SDK matching the repo's `global.json`).

## The actions (your API)

For the **canvas**, open it once then call actions with
`invoke_canvas_action({ instanceId, actionName, input })`; **one server process is owned per
`instanceId`** (use a fresh `instanceId` for an independent server). For the **MCP**, call the
same-named tool with the input fields as arguments. For the **TUI**, the interactive UI and JSON
scripts map onto the same actions.

| Action | Purpose |
| --- | --- |
| `start_server` | Resolve + spawn the server from a build (or a named/config server). Replaces any running one for this instance/session. |
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
1. (canvas only) open_canvas(canvasId:"roslyn-lsp-tester", instanceId:"lsp1")
2. start_server  { serverPath:"<repo root | worktree | output dir | full dll path>", logLevel:"Information" }
3. lsp_request   { method:"initialize", params:{ ...see below... } }   // returns server capabilities
4. lsp_notify    { method:"initialized", params:{} }
5. load projects (solution/open | project/open | --autoLoadProjects)  // see "Loading projects"
6. wait_for_message { method:"workspace/projectInitializationComplete", timeoutMs:600000 }
7. open documents + send feature requests (hover, definition, completion, diagnostics, ...)
8. stop_server
```

### `start_server`

Either resolve a **Roslyn build** or use a **named/config server**:

- `serverPath` (or `repoRoot`) may be:
  - a **repo root / worktree** — the backend searches `artifacts/bin/...` and picks the newest build
    (pass `configuration:"Debug"`/`"Release"` to prefer one),
  - an **output directory** that already contains the dll, or
  - a **full path** to `Microsoft.CodeAnalysis.LanguageServer.dll`.
- `server` — a built-in name (e.g. `"roslyn"` for the released package server) or a path to a
  server-config JSON file. Use this when you don't have (or don't want) a local build.

Useful options: `logLevel` (`Trace|Debug|Information|Warning|Error|None`), `autoLoadProjects`
(bool or max count), `extensionLogDirectory` (on-disk logs), `extraArgs` (verbatim CLI args, e.g.
`["--telemetryLevel","off"]`), `extensions` (repeatable `--extension`), `dotnetPath`, `cwd`,
`env` (extra environment variables), `autoRespond` (default `true` — see below).

The server logs its own `Console.Out` to **stderr** (to keep stdout clean for JSON-RPC); the backend
captures those lines as `kind:"stderr"` records. Structured logs also arrive as `window/logMessage`
notifications.

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
- Include `workspaceFolders` (and the matching capabilities) whenever you will use
  `--autoLoadProjects` — autoload reads the workspace folders to discover `.sln`/`.csproj`.
- The response `result.capabilities` tells you what the server supports (hover, definition,
  completion, diagnostics, semantic tokens, etc.). `result._roslyn_processId` is the server pid.

## Loading projects (the important part)

Feature requests only return meaningful results once the relevant project(s) are loaded into the
workspace. There are three ways to load, all sent **after** `initialized`:

1. **Open a solution** — `lsp_notify { method:"solution/open", params:{ solution:"file:///.../My.sln" } }`
2. **Open projects** — `lsp_notify { method:"project/open", params:{ projects:["file:///.../A.csproj","file:///.../B.csproj"] } }`
3. **Autoload** — pass `autoLoadProjects:true` (or a number cap, default 500) to `start_server`.
   Requires `workspaceFolders` in `initialize`. **Not supported with DevKit.**

`solution/open` and `project/open` are **notifications** (no direct response). Loading is
asynchronous. **Do not assume it is done when the notify returns.**

### Knowing when loading finished — the reliable signal

Wait for the server→client notification **`workspace/projectInitializationComplete`**:

```
wait_for_message { method:"workspace/projectInitializationComplete", timeoutMs:600000 }
```

This is fired after the server finishes restoring/loading the workspace (it works **without**
DevKit). Large solutions can take minutes — use a generous `timeoutMs`.

While loading, the server streams progress you can watch:
- `window/logMessage` lines such as `"Loading <project>"` / `"Completed (re)load of all projects ..."`.
- `$/progress` work-done notifications (begin/report/end) for the load operation.

To observe progress, poll `get_messages { sinceSeq:<lastSeq> }` in a loop, or
`wait_for_message { containsText:"Completed (re)load" }`. After `projectInitializationComplete`,
you may also send `workspace/_roslyn_restore` or expect diagnostics to refresh.

### Auto-respond (why loading can hang if you disable it)

The server sends **requests back to the client** during startup/loading and **blocks** on the
answers:
- `workspace/configuration` — must be answered (the backend replies with an array of `null`s, one per
  requested item, i.e. "use defaults").
- `window/workDoneProgress/create` — **awaited** by the autoload initializer; if unanswered, autoload
  never begins.
- `client/registerCapability`, `window/showMessageRequest`, etc.

The backend **auto-answers all of these by default** (`autoRespond:true`). Keep it on unless you are
specifically testing the client side. If you set `autoRespond:false`, you must watch
`get_messages` for `direction:"recv", kind:"request"` and reply with
`respond_to_request { id:<id>, result:<...> }` yourself — otherwise loading and progress stall.

## Composing common feature requests

Open the document first (so the server tracks live buffer content), then query it. For C#, the
language id is `"csharp"`. Positions are **zero-based** `{ line, character }`.

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

**Diagnostics** — Roslyn uses the **pull** model. Request them explicitly:
```
lsp_request  { method:"textDocument/diagnostic", params:{ textDocument:{ uri:"file:///C:/proj/Program.cs" } } }
lsp_request  { method:"workspace/diagnostic",   params:{ previousResultIds:[] }, timeoutMs:120000 }
```
Some flows also emit `textDocument/publishDiagnostics` (push) — watch for it via `get_messages`.

Roslyn-specific methods you may need: `solution/open`, `project/open`,
`workspace/_roslyn_restore`, `roslyn/updateLogLevel` (`{ logLevel:"Trace" }`),
`textDocument/_vs_onAutoInsert`. Anything not wrapped by a convenience action can be sent with
`send_raw`.

## Reading async output

Server output that is **not** a direct response (logs, progress, diagnostics, project-load
completion, server→client requests) is captured in the buffer. Read it with:
- `get_messages { sinceSeq:<lastSeq>, methodContains:"...", kinds:["notification"] }` — poll; pass the
  returned `lastSeq` next time so you only get new frames. Use `includePayload:false` for a compact
  index, then re-fetch a single `seq` with payload.
- `wait_for_message { method | containsText, timeoutMs }` — block for a specific event.

Each record has `{ seq, time, direction(send|recv|meta), kind(request|response|notification|error|stderr|info|batch), method, id, summary, payload }`.

## Manual / exploratory sending

In the canvas, the user can paste raw JSON-RPC into the UI's send box. From any frontend you can use
`send_raw { message:{...}, waitForResponse:true }` for full control (custom ids, batches, malformed
messages for negative testing). `jsonrpc:"2.0"` is added if you omit it.

## Shutting down

`stop_server` performs the LSP `shutdown` request + `exit` notification, then force-kills if needed.
Always stop the server when you're done so the `dotnet` process doesn't linger. A non-zero exit code
on shutdown is normal (the server logs an exception when the client disconnects). Closing a canvas
panel (or exiting the MCP/TUI process) also tears down that frontend's private backend and server.

## Gotchas checklist

- Paths must be `file://` URIs (`file:///C:/...` on Windows), never bare Windows paths.
- `initialized` must be sent after the `initialize` **response**, before loading projects.
- `--autoLoadProjects` needs `workspaceFolders` in `initialize` and does **not** work with DevKit.
- Don't treat `solution/open` / `project/open` returning as "loaded" — wait for
  `workspace/projectInitializationComplete`.
- Keep `autoRespond:true` unless deliberately testing client behavior; otherwise loading hangs.
- Feature results are empty/partial until the owning project is loaded; open the document with
  `textDocument/didOpen` before per-file requests.
- The server's plain logs are on **stderr** (captured as `kind:"stderr"`); structured logs are
  `window/logMessage`. Raise detail with `logLevel:"Trace"` or `roslyn/updateLogLevel`.
- Big solutions load slowly — use large `timeoutMs` on the wait and on `workspace/diagnostic`.
- One live server per canvas `instanceId` / MCP process / TUI process; `start_server` replaces a
  running one. The message buffer persists across start/stop within an instance until `clear_messages`.
