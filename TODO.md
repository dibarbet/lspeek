# lspeek TODO

## Bugs

- [ ] Failed requests still show as "Pending" when the server shuts down — the error response is not traced until a subsequent request triggers disconnect detection. May need a server-side fix or a client-side keepalive/flush mechanism.

## Features

- [ ] **Full session export** — Export all messages (sent, received, stderr, diagnostics) to a file for easier searching and offline analysis. Current export only saves sent messages as a replayable script.
- [ ] **External editor for JSON** — Open request JSON in VS Code or another real editor instead of the inline editor, for complex multi-line editing.
- [ ] **Resolve request helper** — From a completion (or other) request/response in the log, provide an action to automatically build the corresponding resolve request with details pre-filled from a selected item.
- [ ] **Wait for project load in JSON flow** — Support waiting for the server to finish loading the project (e.g. waiting for specific notifications or a ready signal) in the non-interactive JSON scripting flow, so that requests aren't sent before the server is ready.
- [ ] **Session log message filtering** — Add configurable filters for the session log to hide or mute verbose message types (e.g. `window/logMessage`) that can drown out more interesting traffic.
- [ ] **Workspace root CLI argument** — Allow passing a default workspace root as a CLI argument (e.g. `--workspace <path>`) instead of always using the current directory.
- [ ] **Shutdown without exiting TUI** — Add a way to send the LSP `shutdown`/`exit` sequence without immediately closing the TUI, so you can inspect the final log state.
- [ ] **Simulate typing** — Support for simulating document editing: `textDocument/didOpen` followed by a series of `textDocument/didChange` notifications, to test incremental sync scenarios.

## Testing

- [ ] Add more tests — unit tests for session log, request builder, template generation, and integration tests for the TUI flow.
