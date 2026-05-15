import http from "node:http";
import path from "node:path";
import { Registry } from "./registry.js";
import { forwardToolCall } from "./mcp-handler.js";
function readBody(req) {
    return new Promise((resolve, reject) => {
        let data = "";
        req.on("data", (chunk) => (data += chunk));
        req.on("end", () => resolve(data));
        req.on("error", reject);
    });
}
function jsonResponse(res, status, body) {
    const json = JSON.stringify(body);
    res.writeHead(status, {
        "Content-Type": "application/json",
        "Content-Length": Buffer.byteLength(json),
    });
    res.end(json);
}
function createRequestHandler(registry) {
    return async function handleRequest(req, res) {
        const url = req.url ?? "";
        const method = req.method ?? "";
        if (url === "/health" && method === "GET") {
            jsonResponse(res, 200, {
                status: "ok",
                uptime: process.uptime(),
                instances: registry.instanceCount(),
                launchers: registry.launcherCount,
            });
            return;
        }
        if (url === "/api/status" && method === "GET") {
            jsonResponse(res, 200, {
                instances: registry.getAll(),
                launchers: registry.launcherCount,
            });
            return;
        }
        if (url === "/api/register" && method === "POST") {
            const body = JSON.parse(await readBody(req));
            registry.register(body);
            jsonResponse(res, 200, { ok: true });
            return;
        }
        if (url === "/api/deregister" && method === "POST") {
            const body = JSON.parse(await readBody(req));
            registry.deregister(body);
            jsonResponse(res, 200, { ok: true });
            return;
        }
        if (url === "/api/heartbeat" && method === "POST") {
            const body = JSON.parse(await readBody(req));
            const known = registry.heartbeat(body);
            if (!known) {
                registry.register({
                    projectName: path.basename(body.projectPath),
                    projectPath: body.projectPath,
                    port: body.port,
                    pid: body.pid,
                });
            }
            jsonResponse(res, 200, { ok: true });
            return;
        }
        if (url === "/api/launcher/connect" && method === "POST") {
            registry.launcherConnect();
            jsonResponse(res, 200, { ok: true });
            return;
        }
        if (url === "/api/launcher/disconnect" && method === "POST") {
            registry.launcherDisconnect();
            jsonResponse(res, 200, { ok: true });
            return;
        }
        if (url === "/rpc" && method === "POST") {
            const projectPath = req.headers["x-hades-project"];
            const body = await readBody(req);
            const response = await forwardToolCall(registry, projectPath, body);
            res.writeHead(200, {
                "Content-Type": "application/json",
                "Content-Length": Buffer.byteLength(response),
            });
            res.end(response);
            return;
        }
        jsonResponse(res, 404, { error: "Not found" });
    };
}
export function createHubServer() {
    return new Promise((resolve) => {
        const registry = new Registry();
        const handler = createRequestHandler(registry);
        const server = http.createServer((req, res) => {
            handler(req, res).catch((err) => {
                jsonResponse(res, 500, { error: String(err) });
            });
        });
        server.listen(0, "127.0.0.1", () => {
            const addr = server.address();
            const port = typeof addr === "object" && addr ? addr.port : 0;
            resolve({
                server,
                registry,
                port,
                close: () => new Promise((r) => {
                    server.close(() => r());
                }),
            });
        });
    });
}
//# sourceMappingURL=server.js.map