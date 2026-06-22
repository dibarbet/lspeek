// Roslyn LSP Tester — a canvas extension that lets a chat session launch and interactively
// drive a live language server, send arbitrary LSP messages, await responses, stream every
// frame to a clickable canvas, and read async server notifications (logs, $/progress,
// workspace/projectInitializationComplete).
//
// Thin client: this extension owns no LSP or web-server logic. It spawns the unified C#
// backend (src/Client.Backend) as a private child process — one per canvas instance — and
// proxies every action to the backend's loopback HTTP API. The backend also serves the web
// UI, so `open` simply returns the backend's URL. See backendClient.mjs for spawn/discovery.

import { joinSession, createCanvas } from "@github/copilot-sdk/extension";
import { BackendProcess } from "./backendClient.mjs";

/** @type {Map<string, { backend: BackendProcess }>} */
const entries = new Map();

function getEntry(instanceId) {
    let entry = entries.get(instanceId);
    if (!entry) {
        entry = { backend: new BackendProcess(instanceId) };
        entries.set(instanceId, entry);
    }
    return entry;
}

/** Get (spawning on first use) the backend for a canvas instance. */
async function ensureBackend(instanceId) {
    const entry = getEntry(instanceId);
    await entry.backend.ready();
    return entry.backend;
}

async function teardown(instanceId) {
    const entry = entries.get(instanceId);
    if (!entry) return;
    entries.delete(instanceId);
    if (entry.backend.baseUrl) {
        try {
            await entry.backend.post("/api/stop");
        } catch {
            // ignore — we kill the process next anyway
        }
    }
    entry.backend.kill();
}

// Result helpers ------------------------------------------------------------

function ok(data) {
    return { ok: true, ...data };
}
function fail(err) {
    return { ok: false, error: String(err && err.message ? err.message : err) };
}

const HELP = `Roslyn LSP Tester — how to drive the server:
1. open_canvas(canvasId:"roslyn-lsp-tester", instanceId:"lsp1")
2. start_server { serverPath:"<repo root / worktree, or full dll path>", logLevel:"Information" }   (or { server:"roslyn" } for the released package server)
3. lsp_request { method:"initialize", params:{ processId:null, rootUri:"file:///<ws>", capabilities:{ workspace:{ configuration:true, workspaceFolders:true } }, workspaceFolders:[{uri:"file:///<ws>",name:"ws"}] } }
4. lsp_notify  { method:"initialized", params:{} }
5. lsp_notify  { method:"solution/open", params:{ solution:"file:///<path>.sln" } }   (or project/open, or pass --autoLoadProjects at start)
6. wait_for_message { method:"workspace/projectInitializationComplete", timeoutMs:600000 }   // projects finished loading
7. open a document then send feature requests (textDocument/hover, definition, completion, ...).
Use get_messages to read logs / $/progress / diagnostics that arrive asynchronously.
File paths MUST be file:// URIs. Notifications get no response; requests do (lsp_request waits).`;

function buildMessagesQuery(input) {
    const qs = new URLSearchParams();
    if (input.sinceSeq != null) qs.set("sinceSeq", String(input.sinceSeq));
    if (input.limit != null) qs.set("limit", String(input.limit));
    if (Array.isArray(input.kinds) && input.kinds.length) qs.set("kinds", input.kinds.join(","));
    if (input.direction) qs.set("direction", input.direction);
    if (input.methodContains) qs.set("methodContains", input.methodContains);
    if (input.includePayload != null) qs.set("includePayload", String(input.includePayload));
    const search = qs.toString();
    return search ? `/api/messages?${search}` : "/api/messages";
}

const session = await joinSession({
    canvases: [
        createCanvas({
            id: "roslyn-lsp-tester",
            displayName: "Roslyn LSP Tester",
            description:
                "Launch and interactively drive a live Roslyn language server (any local build) over LSP. " +
                "Send arbitrary messages, await responses, stream every frame to a clickable canvas, and read async notifications.",
            inputSchema: {
                type: "object",
                properties: {
                    serverPath: {
                        type: "string",
                        description: "Optional: pre-fill a repo root/worktree or full dll path for the UI.",
                    },
                    autoStart: {
                        type: "boolean",
                        description: "If true and serverPath is set, start the server when the canvas opens.",
                    },
                },
            },
            actions: [
                {
                    name: "start_server",
                    description:
                        "Resolve and spawn the Roslyn language server (dotnet <Microsoft.CodeAnalysis.LanguageServer.dll> --stdio ...). " +
                        "Replaces any running server for this instance. serverPath may be a repo root/worktree (we search artifacts/bin), " +
                        "an output dir, or a full path to the dll. Alternatively pass server:'roslyn' (released package) or a server-config JSON path.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            serverPath: { type: "string", description: "Repo root / worktree, output dir, or full path to Microsoft.CodeAnalysis.LanguageServer.dll." },
                            server: { type: "string", description: "Built-in server name (e.g. 'roslyn') or a path to a server-config JSON file. Alternative to serverPath." },
                            repoRoot: { type: "string", description: "Alternative to serverPath: a repo root/worktree to search under artifacts/bin." },
                            configuration: { type: "string", description: "Preferred build configuration when searching (Debug/Release). Default: newest." },
                            logLevel: { type: "string", enum: ["Trace", "Debug", "Information", "Warning", "Error", "None"], description: "Server --logLevel. Default Information." },
                            autoLoadProjects: { type: ["boolean", "number"], description: "Pass --autoLoadProjects (optionally with a max count). Requires workspaceFolders in initialize; not for DevKit." },
                            extensionLogDirectory: { type: "string", description: "Pass --extensionLogDirectory for on-disk server logs." },
                            extraArgs: { type: "array", items: { type: "string" }, description: "Extra raw CLI args appended verbatim (e.g. [\"--telemetryLevel\",\"off\"])." },
                            extensions: { type: "array", items: { type: "string" }, description: "Paths passed as --extension (repeatable)." },
                            dotnetPath: { type: "string", description: "Override the 'dotnet' executable." },
                            cwd: { type: "string", description: "Working directory for the server process. Default: the dll directory." },
                            autoRespond: { type: "boolean", description: "Auto-answer server->client infra requests (workspace/configuration, workDoneProgress/create, ...). Default true; required for project load/progress to proceed." },
                            env: { type: "object", additionalProperties: { type: "string" }, description: "Extra environment variables for the server process, merged over the inherited environment. Useful for diagnostics, e.g. { \"DOTNET_DiagnosticPorts\": \"roslynperf\" } to let `dotnet-trace collect --diagnostic-port roslynperf` capture from startup, or DOTNET_gcServer / DOTNET_TieredCompilation toggles." },
                        },
                    },
                    handler: async (ctx) => {
                        try {
                            const backend = await ensureBackend(ctx.instanceId);
                            const out = await backend.post("/api/start", ctx.input || {});
                            return ok({
                                pid: out.pid,
                                dllPath: out.dllPath,
                                commandLine: out.commandLine,
                                candidates: (out.candidates || []).map((c) => ({ configuration: c.configuration, tfm: c.tfm, path: c.path })),
                                next: out.next ?? "Send 'initialize' via lsp_request, then 'initialized' via lsp_notify.",
                            });
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "stop_server",
                    description: "Gracefully stop the language server for this instance (shutdown + exit, then kill).",
                    inputSchema: { type: "object", properties: {} },
                    handler: async (ctx) => {
                        try {
                            const backend = await ensureBackend(ctx.instanceId);
                            await backend.post("/api/stop");
                            return ok({});
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "server_status",
                    description: "Return current server status: running state, pid, resolved dll, command line, message count, pending requests.",
                    inputSchema: { type: "object", properties: {} },
                    handler: async (ctx) => {
                        try {
                            const backend = await ensureBackend(ctx.instanceId);
                            const status = await backend.get("/api/status");
                            return ok({ status });
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "lsp_request",
                    description:
                        "Send an LSP request and WAIT for its response. Returns { result } or { error }. Use for initialize, " +
                        "textDocument/hover, definition, completion, references, workspace/_roslyn_restore, etc.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            method: { type: "string", description: "LSP method name." },
                            params: { description: "Params object for the request (any JSON)." },
                            timeoutMs: { type: "number", description: "Response timeout in ms (default 30000). Use a large value for slow operations." },
                        },
                        required: ["method"],
                    },
                    handler: async (ctx) => {
                        try {
                            const { method, params, timeoutMs } = ctx.input || {};
                            const backend = await ensureBackend(ctx.instanceId);
                            const resp = await backend.post("/api/request", { method, params, timeoutMs: timeoutMs ?? 30000 });
                            return ok({ id: resp.id, result: resp.result, error: resp.error });
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "lsp_notify",
                    description: "Send an LSP notification (no response expected): initialized, solution/open, project/open, textDocument/didOpen, exit, etc.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            method: { type: "string", description: "LSP method name." },
                            params: { description: "Params object for the notification (any JSON)." },
                        },
                        required: ["method"],
                    },
                    handler: async (ctx) => {
                        try {
                            const { method, params } = ctx.input || {};
                            const backend = await ensureBackend(ctx.instanceId);
                            await backend.post("/api/notify", { method, params });
                            return ok({ sent: method });
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "send_raw",
                    description:
                        "Send an arbitrary JSON-RPC message exactly as supplied (full control). 'message' is an object (or array for a batch). " +
                        "Set waitForResponse:true to block on the matching response for a request.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            message: { description: "Full JSON-RPC message object (or array). jsonrpc:'2.0' is added if missing." },
                            waitForResponse: { type: "boolean", description: "If the message is a request, wait for and return its response." },
                            timeoutMs: { type: "number", description: "Timeout when waiting (default 30000)." },
                        },
                        required: ["message"],
                    },
                    handler: async (ctx) => {
                        try {
                            const { message, waitForResponse, timeoutMs } = ctx.input || {};
                            const backend = await ensureBackend(ctx.instanceId);
                            const out = await backend.post("/api/send-raw", {
                                message,
                                waitForResponse: !!waitForResponse,
                                timeoutMs: timeoutMs ?? 30000,
                            });
                            return ok({ response: out.response });
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "respond_to_request",
                    description: "Manually respond to a server→client request (only needed when start_server was called with autoRespond:false).",
                    inputSchema: {
                        type: "object",
                        properties: {
                            id: { description: "The request id to respond to." },
                            result: { description: "Result value (omit if sending an error)." },
                            error: { description: "JSON-RPC error object (omit if sending a result)." },
                        },
                        required: ["id"],
                    },
                    handler: async (ctx) => {
                        try {
                            const { id, result, error } = ctx.input || {};
                            const backend = await ensureBackend(ctx.instanceId);
                            await backend.post("/api/respond", { id, result, error });
                            return ok({ respondedTo: id });
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "get_messages",
                    description:
                        "Read buffered LSP traffic (both directions). Essential for reading async server output: window/logMessage, " +
                        "$/progress, textDocument/publishDiagnostics, workspace/projectInitializationComplete. Poll with sinceSeq=lastSeq.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            sinceSeq: { type: "number", description: "Only return messages with seq greater than this. Use the lastSeq from a prior call to poll." },
                            limit: { type: "number", description: "Max messages to return (default 200). 0 = no limit." },
                            kinds: { type: "array", items: { type: "string" }, description: "Filter by kind: request, response, notification, stderr, info, error." },
                            direction: { type: "string", enum: ["send", "recv", "meta"], description: "Filter by direction." },
                            methodContains: { type: "string", description: "Only messages whose method contains this substring." },
                            includePayload: { type: "boolean", description: "Include full JSON payloads (default true). Set false for a compact index." },
                        },
                    },
                    handler: async (ctx) => {
                        try {
                            const backend = await ensureBackend(ctx.instanceId);
                            const out = await backend.get(buildMessagesQuery(ctx.input || {}));
                            return ok(out);
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "wait_for_message",
                    description:
                        "Block until a matching server message is recorded — e.g. wait for project load to finish via " +
                        "method:'workspace/projectInitializationComplete', or for a specific window/logMessage via containsText. " +
                        "Returns { matched } or { timedOut:true } with recent messages.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            method: { type: "string", description: "Exact LSP method to wait for (e.g. workspace/projectInitializationComplete)." },
                            containsText: { type: "string", description: "Case-insensitive substring to find within a message payload (e.g. a log line)." },
                            direction: { type: "string", enum: ["send", "recv"], description: "Direction to match (default recv)." },
                            sinceSeq: { type: "number", description: "Only consider messages after this seq (also scans existing buffer)." },
                            timeoutMs: { type: "number", description: "Max wait in ms (default 60000). Use a large value for big solutions." },
                        },
                    },
                    handler: async (ctx) => {
                        try {
                            const backend = await ensureBackend(ctx.instanceId);
                            const out = await backend.post("/api/wait", ctx.input || {});
                            if (out.matched != null) {
                                return ok({ matched: out.matched });
                            }
                            return { ok: false, timedOut: true, error: out.error, recent: out.recent };
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "clear_messages",
                    description: "Clear the buffered message log for this instance (server stays running).",
                    inputSchema: { type: "object", properties: {} },
                    handler: async (ctx) => {
                        try {
                            const backend = await ensureBackend(ctx.instanceId);
                            await backend.post("/api/clear");
                            return ok({});
                        } catch (err) {
                            return fail(err);
                        }
                    },
                },
                {
                    name: "help",
                    description: "Return a concise cheat-sheet for driving the Roslyn language server through this canvas.",
                    inputSchema: { type: "object", properties: {} },
                    handler: async () => ok({ help: HELP }),
                },
            ],
            open: async (ctx) => {
                let backend;
                try {
                    backend = await ensureBackend(ctx.instanceId);
                } catch (err) {
                    return {
                        title: "Roslyn LSP Tester",
                        status: `backend unavailable: ${String(err && err.message ? err.message : err)}`,
                    };
                }
                const input = ctx.input || {};
                if (input.autoStart && (input.serverPath || input.repoRoot || input.server)) {
                    backend.post("/api/start", input).catch((err) => {
                        session.log(`Roslyn LSP Tester: auto-start failed: ${err.message || err}`, { level: "warning" }).catch(() => {});
                    });
                }
                let status;
                try {
                    status = await backend.get("/api/status");
                } catch {
                    status = { status: "stopped" };
                }
                return {
                    title: "Roslyn LSP Tester",
                    url: backend.baseUrl,
                    status: status.status === "running" ? `running (pid ${status.pid})` : status.status,
                };
            },
            onClose: async (ctx) => {
                await teardown(ctx.instanceId);
            },
        }),
    ],
});

// Best-effort cleanup so we don't leak backend (and downstream language-server) processes when
// the extension is torn down. The backend also self-exits via --watch-stdin / --parent-pid.
function killAll() {
    for (const [, entry] of entries) {
        try {
            entry.backend.kill();
        } catch {
            // ignore
        }
    }
}
process.on("exit", killAll);
process.on("SIGTERM", () => {
    killAll();
    process.exit(0);
});
process.on("SIGINT", () => {
    killAll();
    process.exit(0);
});

await session.log("Roslyn LSP Tester canvas ready.").catch(() => {});
