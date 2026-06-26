---
name: roslyn-lspeek
description: Roslyn-specific guidance for driving a local or worktree build of the Roslyn C#/VB language server (Microsoft.CodeAnalysis.LanguageServer) through lspeek (the lspeek-canvas canvas, lspeek-mcp MCP tools, or lspeek TUI). Use when starting/testing/debugging a locally built Roslyn server, resolving the build from a repo/worktree, loading solutions/projects, waiting for projectInitializationComplete, requesting pull diagnostics, or sending Roslyn-only LSP methods. Builds on the generic lspeek-control skill.
---

# Driving a local Roslyn language server with lspeek

This skill covers the **Roslyn-specific** parts of driving the
`Microsoft.CodeAnalysis.LanguageServer` host (the server that powers the C# Dev Kit / VS Code C#
extension) built from any local repo or worktree.

> **Read [`lspeek-control`](../lspeek-control/SKILL.md) first.** It describes the shared backend, the
> three frontends (`lspeek-canvas` canvas, `lspeek-mcp` MCP, `lspeek` TUI), the full action API
> (`start_server`, `lsp_request`, `lsp_notify`, `get_messages`, `wait_for_message`, ...), the
> `initialize` handshake, auto-respond, reading async output, and shutdown. Everything below assumes
> that generic know-how and only adds what's specific to Roslyn.

## Building the Roslyn server

The backend runs an **existing build**; it does **not** build the server for you.

- Build output lives at:
  `<repoRoot>/artifacts/bin/Microsoft.CodeAnalysis.LanguageServer/<Config>/<tfm>/Microsoft.CodeAnalysis.LanguageServer.dll`
  (e.g. `Debug/net10.0`). `<tfm>` is `$(NetVSCode)` (currently `net10.0`).
- To build from a worktree:
  `dotnet build src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Microsoft.CodeAnalysis.LanguageServer.csproj`
  (or `dotnet build Roslyn.slnx` / the `LanguageServer` project). Use `dotnet build`, **not** publish.
- Alternatively, target the **released package** server with `server:"roslyn"` (no local build).
- `dotnet` must be on PATH (the SDK matching the repo's `global.json`).

## `start_server` for Roslyn

Resolve a **Roslyn build** or use the **released package** server:

- `serverPath` (or `repoRoot`) may be:
  - a **repo root / worktree** — the backend searches `artifacts/bin/...` and picks the newest build
    (pass `configuration:"Debug"`/`"Release"` to prefer one),
  - an **output directory** that already contains the dll, or
  - a **full path** to `Microsoft.CodeAnalysis.LanguageServer.dll`.
- `server:"roslyn"` — the released package server. Use this when you don't have (or don't want) a
  local build.

The server starts as `dotnet <Microsoft.CodeAnalysis.LanguageServer.dll> --stdio ...`. The
`initialize` response includes `result._roslyn_processId` (the server pid). All other `start_server`
options (`logLevel`, `extraArgs`, `extensions`, `env`, `autoRespond`, ...) are documented in
`lspeek-control`.

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

### Auto-respond and loading

Roslyn **blocks** on client answers during startup/loading — notably
`window/workDoneProgress/create` is **awaited** by the autoload initializer (if unanswered, autoload
never begins) and `workspace/configuration` must be answered. Keep `autoRespond:true` (the default).
See the auto-respond section of `lspeek-control` for the full mechanism.

## Roslyn feature specifics

- **Language id** for C# is `"csharp"`. Open the document with `textDocument/didOpen` before
  per-file requests so the server tracks live buffer content. (See `lspeek-control` for the generic
  hover/definition/completion/references/symbol request shapes.)
- **Diagnostics use the pull model.** Request them explicitly:
  ```
  lsp_request  { method:"textDocument/diagnostic", params:{ textDocument:{ uri:"file:///C:/proj/Program.cs" } } }
  lsp_request  { method:"workspace/diagnostic",   params:{ previousResultIds:[] }, timeoutMs:120000 }
  ```
  Some flows also emit `textDocument/publishDiagnostics` (push) — watch for it via `get_messages`.
- **Roslyn-specific methods** you may need: `solution/open`, `project/open`,
  `workspace/_roslyn_restore`, `roslyn/updateLogLevel` (`{ logLevel:"Trace" }`),
  `textDocument/_vs_onAutoInsert`. Anything not wrapped by a convenience action can be sent with
  `send_raw`.

## Roslyn gotchas checklist

- Build output path is
  `artifacts/bin/Microsoft.CodeAnalysis.LanguageServer/<Config>/<tfm>/...dll`; `<tfm>` is `$(NetVSCode)`.
  Use `dotnet build`, **not** publish.
- `--autoLoadProjects` needs `workspaceFolders` in `initialize` and does **not** work with DevKit.
- Don't treat `solution/open` / `project/open` returning as "loaded" — wait for
  `workspace/projectInitializationComplete`.
- Keep `autoRespond:true`; Roslyn's autoload awaits `window/workDoneProgress/create`, so loading
  hangs without it.
- Diagnostics are **pull** — request `textDocument/diagnostic` / `workspace/diagnostic`; they won't
  arrive on their own.
- Big solutions load slowly — use large `timeoutMs` on the wait and on `workspace/diagnostic`.
- Raise log detail with `logLevel:"Trace"` at start or `roslyn/updateLogLevel` at runtime. The
  server's plain logs are on **stderr** (captured as `kind:"stderr"`); structured logs are
  `window/logMessage`.
