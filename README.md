# lspeek

A .NET tool terminal UI for interactively driving [Language Server Protocol](https://microsoft.github.io/language-server-protocol/) servers. Send requests, inspect responses, and explore server capabilities — all from your terminal.

## Usage

```
dnx lspeek <server> [--no-init] [--json <PATH>] [--exit]
```

| Argument / Option | Description |
|---|---|
| `<server>` | Built-in server name (e.g. `roslyn`) or path to a server config JSON file |
| `--no-init` | Skip the automatic `initialize` / `initialized` handshake |
| `--json <PATH>` | Run a JSON script file in non-interactive mode before entering the TUI |
| `--exit` | Exit after the script completes (use with `--json`) |

### Examples

```bash
dnx lspeek roslyn                          # launch and connect to Roslyn
dnx lspeek roslyn --no-init                # skip handshake
dnx lspeek roslyn --json replay.json       # run a script then enter TUI
dnx lspeek roslyn --json replay.json --exit # run a script and exit
dnx lspeek ./my-server.json                # use a custom server config
```

## Built-in Servers

| Name | Description |
|---|---|
| `roslyn` | C# / VB language server via `Microsoft.CodeAnalysis.LanguageServer` |

## Custom Server Configuration

You can point `<server>` at a JSON file to connect to any LSP server:

```json
{
  "name": "my-server",
  "command": "path/to/server",
  "arguments": ["--stdio"]
}
```

## Scripting

Sessions can be automated with JSON script files. A script is a JSON array of messages to send:

```json
[
  {
    "type": "request",
    "method": "textDocument/hover",
    "params": {
      "textDocument": { "uri": "file:///path/to/file.cs" },
      "position": { "line": 10, "character": 5 }
    }
  },
  {
    "type": "notification",
    "method": "textDocument/didOpen",
    "params": { "textDocument": { "uri": "file:///path/to/file.cs" } }
  }
]
```

In script mode, status/progress is written to stderr and per-message results are written to stdout. When you use `--json`, lspeek does not send `initialize` or `initialized` automatically; if the session needs that handshake, the script must include it explicitly.

Each entry supports:

| Field | Default | Description |
|---|---|---|
| `method` | *(required)* | LSP method name |
| `params` | `{}` | JSON payload |
| `type` | `"request"` | `"request"` or `"notification"` |

Use `X` in the TUI to export a session as a replayable script.
