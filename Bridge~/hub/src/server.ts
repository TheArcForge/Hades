import http from "node:http";
import path from "node:path";
import { Registry } from "./registry.js";
import { forwardToolCall } from "./mcp-handler.js";
import type {
  RegisterRequest,
  DeregisterRequest,
  HeartbeatRequest,
} from "./types.js";

export interface HubServer {
  server: http.Server;
  registry: Registry;
  port: number;
  close: () => Promise<void>;
}

function readBody(req: http.IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    let data = "";
    req.on("data", (chunk: string) => (data += chunk));
    req.on("end", () => resolve(data));
    req.on("error", reject);
  });
}

function jsonResponse(
  res: http.ServerResponse,
  status: number,
  body: unknown
): void {
  const json = JSON.stringify(body);
  res.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(json),
  });
  res.end(json);
}

function createRequestHandler(registry: Registry) {
  return async function handleRequest(
    req: http.IncomingMessage,
    res: http.ServerResponse
  ): Promise<void> {
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
      const body = JSON.parse(await readBody(req)) as RegisterRequest;
      registry.register(body);
      jsonResponse(res, 200, { ok: true });
      return;
    }

    if (url === "/api/deregister" && method === "POST") {
      const body = JSON.parse(await readBody(req)) as DeregisterRequest;
      registry.deregister(body);
      jsonResponse(res, 200, { ok: true });
      return;
    }

    if (url === "/api/heartbeat" && method === "POST") {
      const body = JSON.parse(await readBody(req)) as HeartbeatRequest;
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
      const projectPath = req.headers["x-hades-project"] as string;
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

export function createHubServer(): Promise<HubServer> {
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
        close: () =>
          new Promise<void>((r) => {
            server.close(() => r());
          }),
      });
    });
  });
}
