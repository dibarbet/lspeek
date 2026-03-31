# Terminal UI Mockup

## Goals
- Session log is the main focus of the interface.
- Each message is collapsed by default (showing method name + status only).
- Message details are only visible when expanded via hotkey.
- "Choose an action" is simplified.
- "View Full Log" is removed.
- Export session is available as a hotkey (not in the action list).
- Server stderr is captured and shown only in expanded message details.
- Keep "Shutdown" and "Exit" as the last action options.

## Main Screen (Default)

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ ManualLspClient - Session: roslyn                                     Server: Running PID 42 │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Session Log (collapsed by default)                                                           │
│                                                                                              │
│ 14:22:01  -> initialize                  OK                                                   │
│ 14:22:01  <- initialize                  OK                                                   │
│ 14:22:02  -> initialized                 Sent                                                 │
│ 14:22:04  -> textDocument/didOpen        Sent                                                 │
│ 14:22:05  <- textDocument/publishDiagnostics  WARN (2 diagnostics)                           │
│ 14:22:07  -> textDocument/completion     Pending                                              │
│ 14:22:07  <- textDocument/completion     OK                                                   │
│ 14:22:09  <- window/logMessage           INFO                                                 │
│                                                                                              │
│ > Select message: [7] textDocument/completion                                                │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Choose an action                                                                             │
│  1) Send request                                                                             │
│  2) Shutdown & Exit                                                                          │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Hotkeys: [Enter] Select Action  [J/K] Move  [E] Expand/Collapse  [X] Export Session  [Q] Exit │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Expanded Message Details (Hotkey: E)

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ Message Details (Expanded)                                                                   │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Time:      14:22:07.531                                                                      │
│ Direction: Received                                                                          │
│ Type:      Response                                                                          │
│ Method:    textDocument/completion                                                           │
│ Id:        17                                                                                │
│ Status:    OK                                                                                │
│                                                                                              │
│ Params / Result                                                                              │
│ {                                                                                            │
│   "isIncomplete": false,                                                                    │
│   "items": [ ... ]                                                                          │
│ }                                                                                            │
│                                                                                              │
│ Server stderr (captured during this message window)                                          │
│ [none]                                                                                       │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Hotkeys: [E] Collapse  [N/P] Next/Previous Message  [X] Export Session                       │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Expanded Message with stderr Example

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ Message Details (Expanded)                                                                   │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Time:      14:23:11.102                                                                      │
│ Direction: Received                                                                          │
│ Type:      Response                                                                          │
│ Method:    workspace/symbol                                                                  │
│ Id:        22                                                                                │
│ Status:    Error                                                                             │
│                                                                                              │
│ Error                                                                                         │
│ { "code": -32603, "message": "Internal error" }                                         │
│                                                                                              │
│ Server stderr (captured during this message window)                                          │
│ [14:23:11.090] analyzer: null ref in SymbolIndex                                             │
│ [14:23:11.091] stack: at Roslyn.LanguageServer.SymbolService...                              │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Hotkeys: [E] Collapse  [N/P] Next/Previous Message  [X] Export Session                       │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Send Request Screen

```text
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ Send Request                                                                                 │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Method                                                                                       │
│ > textDocument/definition                                                                     │
│                                                                                              │
│ Params JSON                                                                                  │
│ {                                                                                            │
│   "textDocument": { "uri": "file:///c:/repo/foo.cs" },                                  │
│   "position": { "line": 12, "character": 8 }                                            │
│ }                                                                                            │
│                                                                                              │
│ Validation: OK                                                                               │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ Actions: [S] Send  [C] Cancel                                                                │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Action and Hotkey Model
- Choose an action menu contains only:
  - Send request
  - Shutdown
  - Exit
- Export session is hotkey only: X
- Expand/collapse selected log item: E
- Move selected log item: J/K or Up/Down
- Enter opens action menu selection

## Log Row Structure (Collapsed)
- Timestamp
- Direction arrow (-> or <-)
- Request/notification method name
- Status summary

Example:
- `14:22:07 <- textDocument/completion OK`
- `14:23:11 <- workspace/symbol Error`

## Status Summary Rules
- Pending: request sent, awaiting response
- OK: response received without error
- Error: response has error payload
- Sent: notification sent successfully
- WARN/INFO: notification-derived display state

## stderr Recording Rules
- Capture all stderr lines from server process continuously.
- Group all contiguous stderr messages together into a single item in the session log

## Suggested Implementation Notes
- Add per-message metadata for status and associated stderr excerpts.
- Keep default rendering in collapsed mode for all messages.
- Remove standalone "View Full Log" and "View Server Stderr" actions.
- Keep shutdown and exit as final options in the action prompt.
- Add footer hotkey hints and refresh every render cycle.
