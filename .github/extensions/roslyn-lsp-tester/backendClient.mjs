// Thin JS client for the unified C# lspeek backend.
//
// This replaces the old in-extension LSP implementation (instance.mjs / lspClient.mjs) and
// per-instance web server (renderServer.mjs / renderer.mjs). All of that now lives in the
// C# backend (src/Client.Backend): it owns the LSP server lifecycle, records every frame,
// serves the web UI, and exposes an HTTP + SSE API. This module just spawns that backend as
// a private child process and proxies HTTP requests to it.
//
// Discovery: steps 1-3 mirror src/Client.Protocol/BackendLauncher.cs; step 4 is canvas-specific.
//   1. LSPEEK_BACKEND env var (a file, or a directory containing the host)
//   2. a bundled backend next to this extension (the extension dir, then a "backend" subfolder)
//   3. in-repo dev build (src/Client.Backend/bin/<config>/<tfm>/lspeek-backend[.exe|.dll])
//   4. `dotnet dnx lspeek-backend` — acquire the published tool from NuGet (no local build needed)
//
// Step 4 is what lets the canvas run off-repo. The .NET tools (lspeek, lspeek-mcp) bundle the
// backend beside them, so BackendLauncher.cs has no dnx step; this JS extension can't bundle a
// .NET app, so it falls back to dnx, which needs the .NET SDK on PATH but no prior build/install
// (dnx downloads + caches the lspeek-backend tool on first use, then launches it).
// Pin a version with LSPEEK_BACKEND_VERSION; opt into prereleases with LSPEEK_BACKEND_PRERELEASE.

import { spawn } from "node:child_process";
import { request as httpRequest } from "node:http";
import { existsSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const EXECUTABLE_NAME = "lspeek-backend";
const ENV_VAR = "LSPEEK_BACKEND";
const HANDSHAKE_PREFIX = "LSPEEK_BACKEND_URL=";
const HANDSHAKE_TIMEOUT_MS = 30000;

// `dotnet dnx` fallback (step 4): NuGet package id + env overrides + a longer handshake budget,
// since a cold first-run download from NuGet can take much longer than a local launch.
const DNX_PACKAGE_ID = "lspeek-backend";
const DNX_VERSION_ENV = "LSPEEK_BACKEND_VERSION";
const DNX_PRERELEASE_ENV = "LSPEEK_BACKEND_PRERELEASE";
const DNX_HANDSHAKE_TIMEOUT_MS = 120000;

const isWindows = process.platform === "win32";
const hostFileName = isWindows ? `${EXECUTABLE_NAME}.exe` : EXECUTABLE_NAME;
const extensionDir = path.dirname(fileURLToPath(import.meta.url));

/**
 * Resolve how to launch the backend: { command, args, timeoutMs? } where command is a native
 * apphost (args === []), "dotnet" with ["exec", <dll>], or "dotnet" with ["dnx", ...] to fetch
 * and run the published tool. timeoutMs overrides the handshake budget for the slow dnx path.
 */
function resolveLaunch() {
    const configured = process.env[ENV_VAR];
    if (configured && configured.trim()) {
        const value = configured.trim();
        if (isDirectory(value)) {
            const fromDir = hostInDirectory(value);
            if (fromDir) return fromDir;
        }
        if (existsSync(value)) return asLaunch(value);
        throw new Error(`Configured ${ENV_VAR} '${value}' was not found.`);
    }

    // A packaged extension ships the backend beside it (the extension dir, or a "backend" subfolder).
    const bundled = hostInDirectory(extensionDir)
        ?? hostInDirectory(path.join(extensionDir, "backend"));
    if (bundled) return bundled;

    const repoRoot = findRepoRoot(extensionDir);
    if (repoRoot) {
        const fromDev = scanDevBuild(path.join(repoRoot, "src", "Client.Backend", "bin"));
        if (fromDev) return fromDev;
    }

    // Last resort: fetch + run the published tool from NuGet via `dotnet dnx`. This needs the
    // .NET SDK on PATH but no local build or prior install; dnx caches the tool after first run.
    return dnxLaunch();
}

/**
 * Launch the backend via `dotnet dnx`, which downloads the published tool from NuGet (cached
 * after the first run) and runs it. LSPEEK_BACKEND_VERSION pins an exact version;
 * LSPEEK_BACKEND_PRERELEASE (1/true/yes/on) allows floating to the latest prerelease.
 */
function dnxLaunch() {
    const version = (process.env[DNX_VERSION_ENV] ?? "").trim();
    const args = ["dnx", "--yes"];
    // --prerelease and an explicit @version are mutually exclusive in dnx.
    if (!version && isTruthy(process.env[DNX_PRERELEASE_ENV])) args.push("--prerelease");
    args.push(version ? `${DNX_PACKAGE_ID}@${version}` : DNX_PACKAGE_ID);
    return { command: "dotnet", args, timeoutMs: DNX_HANDSHAKE_TIMEOUT_MS };
}

function isTruthy(value) {
    if (!value) return false;
    const s = String(value).trim().toLowerCase();
    return s === "1" || s === "true" || s === "yes" || s === "on";
}

function isDirectory(p) {
    try {
        return statSync(p).isDirectory();
    } catch {
        return false;
    }
}

function hostInDirectory(dir) {
    const host = path.join(dir, hostFileName);
    if (existsSync(host)) return { command: host, args: [] };
    const dll = path.join(dir, `${EXECUTABLE_NAME}.dll`);
    if (existsSync(dll)) return { command: "dotnet", args: ["exec", dll] };
    return null;
}

function asLaunch(file) {
    return file.toLowerCase().endsWith(".dll")
        ? { command: "dotnet", args: ["exec", file] }
        : { command: file, args: [] };
}

function findRepoRoot(start) {
    let dir = start;
    for (let i = 0; i < 100; i++) {
        if (existsSync(path.join(dir, "lspeek.slnx"))) return dir;
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    return null;
}

/**
 * Find the most-recently-built host under bin/<config>/<tfm>. The JS side has no build context,
 * so it scans every config/TFM and prefers the newest host file.
 */
function scanDevBuild(binRoot) {
    if (!isDirectory(binRoot)) return null;
    let best = null;
    for (const config of safeReaddir(binRoot)) {
        const configDir = path.join(binRoot, config);
        if (!isDirectory(configDir)) continue;
        for (const tfm of safeReaddir(configDir)) {
            if (!/^net\d/i.test(tfm)) continue;
            const tfmDir = path.join(configDir, tfm);
            if (!isDirectory(tfmDir)) continue;
            const launch = hostInDirectory(tfmDir);
            if (!launch) continue;
            const probe = launch.command === "dotnet" ? launch.args[1] : launch.command;
            const mtime = safeMtime(probe);
            if (!best || mtime > best.mtime) best = { launch, mtime };
        }
    }
    return best ? best.launch : null;
}

function safeReaddir(dir) {
    try {
        return readdirSync(dir);
    } catch {
        return [];
    }
}

function safeMtime(file) {
    try {
        return statSync(file).mtimeMs;
    } catch {
        return 0;
    }
}

/**
 * A private backend process plus an HTTP client for its loopback API. One per canvas instance.
 */
export class BackendProcess {
    constructor(instanceId) {
        this.instanceId = instanceId;
        this.child = undefined;
        this.baseUrl = undefined;
        this.stderrTail = [];
        this._ready = undefined;
    }

    /** Spawn the backend (once) and resolve when it has reported its URL. Idempotent. */
    ready() {
        if (!this._ready) this._ready = this._spawn();
        return this._ready;
    }

    _spawn() {
        return new Promise((resolve, reject) => {
            let launch;
            try {
                launch = resolveLaunch();
            } catch (err) {
                reject(err);
                return;
            }

            const args = [
                ...launch.args,
                "--port", "0",
                "--watch-stdin",
                "--parent-pid", String(process.pid),
            ];
            const child = spawn(launch.command, args, {
                stdio: ["pipe", "pipe", "pipe"],
                windowsHide: true,
            });
            this.child = child;

            const timeoutMs = launch.timeoutMs ?? HANDSHAKE_TIMEOUT_MS;
            let settled = false;
            const timer = setTimeout(() => {
                if (settled) return;
                settled = true;
                this.kill();
                reject(new Error(`Backend did not report its URL within ${timeoutMs}ms.`));
            }, timeoutMs);

            let stdoutBuffer = "";
            child.stdout.setEncoding("utf8");
            child.stdout.on("data", (chunk) => {
                if (settled) return;
                stdoutBuffer += chunk;
                let newline;
                while ((newline = stdoutBuffer.indexOf("\n")) >= 0) {
                    const line = stdoutBuffer.slice(0, newline).trim();
                    stdoutBuffer = stdoutBuffer.slice(newline + 1);
                    if (line.startsWith(HANDSHAKE_PREFIX)) {
                        const url = line.slice(HANDSHAKE_PREFIX.length).trim();
                        this.baseUrl = url.replace(/\/+$/, "");
                        settled = true;
                        clearTimeout(timer);
                        resolve(this.baseUrl);
                        return;
                    }
                }
            });

            child.stderr.setEncoding("utf8");
            child.stderr.on("data", (chunk) => {
                this.stderrTail.push(chunk);
                if (this.stderrTail.length > 50) this.stderrTail.shift();
            });

            child.on("error", (err) => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                reject(new Error(`Failed to spawn backend (${launch.command}): ${err.message}`));
            });

            child.on("exit", (code, signal) => {
                this.child = undefined;
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                const tail = this.stderrTail.join("").trim();
                reject(new Error(
                    `Backend exited before reporting its URL (code ${code}, signal ${signal}).` +
                    (tail ? `\n${tail}` : "")));
            });
        });
    }

    /** GET <path> and parse the JSON body. Throws on non-2xx using the backend's { error }. */
    get(pathname) {
        return this._request("GET", pathname, undefined);
    }

    /** POST <path> with a JSON body and parse the JSON response. Throws on non-2xx. */
    post(pathname, body) {
        return this._request("POST", pathname, body ?? {});
    }

    async _request(method, pathname, body) {
        const baseUrl = await this.ready();
        const target = new URL(baseUrl + pathname);
        const payload = body === undefined ? undefined : Buffer.from(JSON.stringify(body), "utf8");

        const result = await new Promise((resolve, reject) => {
            const req = httpRequest(
                {
                    hostname: target.hostname,
                    port: target.port,
                    path: target.pathname + target.search,
                    method,
                    headers: payload
                        ? { "Content-Type": "application/json", "Content-Length": payload.length }
                        : {},
                },
                (res) => {
                    const chunks = [];
                    res.on("data", (c) => chunks.push(c));
                    res.on("end", () => resolve({ status: res.statusCode ?? 0, body: Buffer.concat(chunks).toString("utf8") }));
                });
            req.on("error", reject);
            if (payload) req.write(payload);
            req.end();
        });

        let parsed;
        if (result.body) {
            try {
                parsed = JSON.parse(result.body);
            } catch {
                parsed = undefined;
            }
        }

        if (result.status < 200 || result.status >= 300) {
            const message = parsed && parsed.error ? parsed.error : `HTTP ${result.status}`;
            throw new Error(message);
        }
        return parsed ?? {};
    }

    /** Terminate the backend child. The backend also self-exits via --watch-stdin / --parent-pid. */
    kill() {
        const child = this.child;
        if (!child) return;
        this.child = undefined;
        try {
            child.stdin?.end();
        } catch {
            // ignore
        }
        try {
            child.kill();
        } catch {
            // ignore
        }
    }
}
