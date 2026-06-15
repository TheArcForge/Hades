import { describe, it, expect, beforeAll, afterAll, beforeEach } from "vitest";
import http from "node:http";
import { createHubServer, type HubServer } from "../../hub/src/server.js";

function httpGet(port: number, path: string): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    http
      .get(`http://127.0.0.1:${port}${path}`, { timeout: 2000 }, (res) => {
        let data = "";
        res.on("data", (chunk: string) => (data += chunk));
        res.on("end", () => resolve({ status: res.statusCode!, body: data }));
      })
      .on("error", reject);
  });
}

function httpPost(
  port: number,
  path: string,
  body: string,
  headers?: Record<string, string>
): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      {
        hostname: "127.0.0.1",
        port,
        path,
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
          ...headers,
        },
        timeout: 5000,
      },
      (res) => {
        let data = "";
        res.on("data", (chunk: string) => (data += chunk));
        res.on("end", () => resolve({ status: res.statusCode!, body: data }));
      }
    );
    req.on("error", reject);
    req.write(body);
    req.end();
  });
}

describe("Hub HTTP API", () => {
  let hub: HubServer;

  beforeAll(async () => {
    hub = await createHubServer();
  });

  afterAll(async () => {
    await hub.close();
  });

  beforeEach(() => {
    // Clear registry between tests by deregistering everything
    for (const instance of hub.registry.getAll()) {
      hub.registry.remove(instance.projectPath);
    }
    // Reset launcher count
    while (hub.registry.launcherCount > 0) {
      hub.registry.launcherDisconnect();
    }
  });

  describe("/health", () => {
    it("returns status ok with counts", async () => {
      const res = await httpGet(hub.port, "/health");
      const data = JSON.parse(res.body);

      expect(res.status).toBe(200);
      expect(data.status).toBe("ok");
      expect(data.instances).toBe(0);
      expect(data.launchers).toBe(0);
    });
  });

  describe("/api/register", () => {
    it("registers an instance and shows it in /health", async () => {
      const res = await httpPost(
        hub.port,
        "/api/register",
        JSON.stringify({
          projectName: "TestGame",
          projectPath: "/path/to/TestGame",
          port: 11111,
          pid: 1234,
        })
      );
      expect(JSON.parse(res.body).ok).toBe(true);

      const health = JSON.parse((await httpGet(hub.port, "/health")).body);
      expect(health.instances).toBe(1);
    });
  });

  describe("/api/deregister", () => {
    it("marks instance transient on transient=true", async () => {
      await httpPost(
        hub.port,
        "/api/register",
        JSON.stringify({
          projectName: "TestGame",
          projectPath: "/path/to/TestGame",
          port: 11111,
          pid: 1234,
        })
      );

      await httpPost(
        hub.port,
        "/api/deregister",
        JSON.stringify({ projectPath: "/path/to/TestGame", transient: true })
      );

      const instance = hub.registry.get("/path/to/TestGame");
      expect(instance).not.toBeNull();
      expect(instance!.status).toBe("transient");
    });

    it("removes instance on transient=false", async () => {
      await httpPost(
        hub.port,
        "/api/register",
        JSON.stringify({
          projectName: "TestGame",
          projectPath: "/path/to/TestGame",
          port: 11111,
          pid: 1234,
        })
      );

      await httpPost(
        hub.port,
        "/api/deregister",
        JSON.stringify({ projectPath: "/path/to/TestGame", transient: false })
      );

      expect(hub.registry.get("/path/to/TestGame")).toBeNull();
    });
  });

  describe("/api/heartbeat", () => {
    it("updates existing instance", async () => {
      await httpPost(
        hub.port,
        "/api/register",
        JSON.stringify({
          projectName: "TestGame",
          projectPath: "/path/to/TestGame",
          port: 11111,
          pid: 1234,
        })
      );

      await httpPost(
        hub.port,
        "/api/heartbeat",
        JSON.stringify({
          projectPath: "/path/to/TestGame",
          port: 22222,
          pid: 1234,
        })
      );

      expect(hub.registry.get("/path/to/TestGame")!.port).toBe(22222);
    });

    it("auto-registers unknown instance from heartbeat", async () => {
      await httpPost(
        hub.port,
        "/api/heartbeat",
        JSON.stringify({
          projectPath: "/path/to/Unknown",
          port: 33333,
          pid: 5678,
        })
      );

      const instance = hub.registry.get("/path/to/Unknown");
      expect(instance).not.toBeNull();
      expect(instance!.projectName).toBe("Unknown");
      expect(instance!.port).toBe(33333);
    });
  });

  describe("/api/launcher", () => {
    it("connect increments launcher count", async () => {
      await httpPost(hub.port, "/api/launcher/connect", "{}");
      const health = JSON.parse((await httpGet(hub.port, "/health")).body);
      expect(health.launchers).toBe(1);
    });

    it("disconnect decrements launcher count", async () => {
      await httpPost(hub.port, "/api/launcher/connect", "{}");
      await httpPost(hub.port, "/api/launcher/disconnect", "{}");
      const health = JSON.parse((await httpGet(hub.port, "/health")).body);
      expect(health.launchers).toBe(0);
    });
  });

  describe("/api/status", () => {
    it("returns full registry state", async () => {
      await httpPost(
        hub.port,
        "/api/register",
        JSON.stringify({
          projectName: "GameA",
          projectPath: "/path/a",
          port: 11111,
          pid: 1,
        })
      );
      await httpPost(
        hub.port,
        "/api/register",
        JSON.stringify({
          projectName: "GameB",
          projectPath: "/path/b",
          port: 22222,
          pid: 2,
        })
      );

      const res = await httpGet(hub.port, "/api/status");
      const data = JSON.parse(res.body);

      expect(data.instances).toHaveLength(2);
      expect(data.instances.map((i: { projectName: string }) => i.projectName).sort()).toEqual([
        "GameA",
        "GameB",
      ]);
    });
  });

  describe("404", () => {
    it("returns 404 for unknown routes", async () => {
      const res = await httpGet(hub.port, "/nonexistent");
      expect(res.status).toBe(404);
    });
  });
});

describe("MCP Forwarding", () => {
  let hub: HubServer;
  let mockUnity: http.Server;
  let mockUnityPort: number;

  beforeAll(async () => {
    hub = await createHubServer();

    // Create a mock Unity MCP server that echoes back a tools/list response
    mockUnity = http.createServer((req, res) => {
      let body = "";
      req.on("data", (chunk: string) => (body += chunk));
      req.on("end", () => {
        const parsed = JSON.parse(body);

        if (parsed.method === "tools/list") {
          const response = JSON.stringify({
            jsonrpc: "2.0",
            id: parsed.id,
            result: {
              tools: [{ name: "hades_ping", description: "Ping" }],
            },
          });
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end(response);
          return;
        }

        if (parsed.method === "tools/call") {
          const response = JSON.stringify({
            jsonrpc: "2.0",
            id: parsed.id,
            result: {
              content: [{ type: "text", text: `called: ${parsed.params.name}` }],
            },
          });
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end(response);
          return;
        }

        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ jsonrpc: "2.0", id: parsed.id, result: {} }));
      });
    });

    await new Promise<void>((resolve) => {
      mockUnity.listen(0, "127.0.0.1", () => {
        const addr = mockUnity.address();
        mockUnityPort = typeof addr === "object" && addr ? addr.port : 0;
        resolve();
      });
    });

    // Register mock Unity with the hub
    hub.registry.register({
      projectName: "TestGame",
      projectPath: "/Users/test/Projects/TestGame",
      port: mockUnityPort,
      pid: process.pid,
    });
  });

  afterAll(async () => {
    await hub.close();
    await new Promise<void>((r) => mockUnity.close(() => r()));
  });

  it("forwards tools/call to the correct Unity instance", async () => {
    const rpcRequest = JSON.stringify({
      jsonrpc: "2.0",
      id: "test-1",
      method: "tools/call",
      params: { name: "hades_ping", arguments: {} },
    });

    const res = await httpPost(hub.port, "/rpc", rpcRequest, {
      "X-Hades-Project": "/Users/test/Projects/TestGame",
    });

    const data = JSON.parse(res.body);
    expect(data.id).toBe("test-1");
    expect(data.result.content[0].text).toBe("called: hades_ping");
  });

  it("forwards tools/list to Unity instance", async () => {
    const rpcRequest = JSON.stringify({
      jsonrpc: "2.0",
      id: "test-2",
      method: "tools/list",
      params: {},
    });

    const res = await httpPost(hub.port, "/rpc", rpcRequest, {
      "X-Hades-Project": "/Users/test/Projects/TestGame",
    });

    const data = JSON.parse(res.body);
    expect(data.result.tools).toHaveLength(1);
    expect(data.result.tools[0].name).toBe("hades_ping");
  });

  it("resolves via parent path matching", async () => {
    const rpcRequest = JSON.stringify({
      jsonrpc: "2.0",
      id: "test-3",
      method: "tools/call",
      params: { name: "hades_ping", arguments: {} },
    });

    const res = await httpPost(hub.port, "/rpc", rpcRequest, {
      "X-Hades-Project": "/Users/test/Projects",
    });

    const data = JSON.parse(res.body);
    expect(data.result.content[0].text).toBe("called: hades_ping");
  });

  it("resolves via child path matching", async () => {
    const rpcRequest = JSON.stringify({
      jsonrpc: "2.0",
      id: "test-4",
      method: "tools/call",
      params: { name: "hades_ping", arguments: {} },
    });

    const res = await httpPost(hub.port, "/rpc", rpcRequest, {
      "X-Hades-Project": "/Users/test/Projects/TestGame/Packages/com.foo",
    });

    const data = JSON.parse(res.body);
    expect(data.result.content[0].text).toBe("called: hades_ping");
  });

  it("returns error when no instance matches (2+ instances, so no single-instance fallback)", async () => {
    // A second instance so the single-instance fallback doesn't fire — a genuine no-match.
    hub.registry.register({
      projectName: "OtherGame",
      projectPath: "/Users/test/Projects/OtherGame",
      port: 1,
      pid: process.pid,
    });
    try {
      const rpcRequest = JSON.stringify({
        jsonrpc: "2.0",
        id: "test-5",
        method: "tools/call",
        params: { name: "hades_ping", arguments: {} },
      });

      const res = await httpPost(hub.port, "/rpc", rpcRequest, {
        "X-Hades-Project": "/completely/different/path",
      });

      const data = JSON.parse(res.body);
      expect(data.error).toBeDefined();
      expect(data.error.code).toBe(-32000);
      expect(data.error.message).toContain("No Unity instance found");
      expect(data.error.message).toContain("TestGame");
    } finally {
      hub.registry.remove("/Users/test/Projects/OtherGame");
    }
  });

  it("single-instance fallback: routes a non-matching call to the only registered instance", async () => {
    // Only TestGame is registered (describe default), so an unidentifiable cwd ("/") routes to it.
    const rpcRequest = JSON.stringify({
      jsonrpc: "2.0",
      id: "test-fallback",
      method: "tools/call",
      params: { name: "hades_ping", arguments: {} },
    });

    const res = await httpPost(hub.port, "/rpc", rpcRequest, {
      "X-Hades-Project": "/",
    });

    const data = JSON.parse(res.body);
    expect(data.result.content[0].text).toBe("called: hades_ping");
  });
});
