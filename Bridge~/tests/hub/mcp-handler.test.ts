import { describe, it, expect } from "vitest";
import { Registry } from "../../hub/src/registry.js";
import { forwardToolCall } from "../../hub/src/mcp-handler.js";

describe("forwardToolCall error handling", () => {
  it("returns a JSON-RPC error (not a throw) when a healthy instance is unreachable", async () => {
    const registry = new Registry();
    registry.register({
      projectName: "Foo",
      projectPath: "/proj/foo",
      port: 47119, // nothing listening -> ECONNREFUSED (Unity died/reloading mid-call)
      pid: 1,
    });
    const body = JSON.stringify({
      jsonrpc: "2.0",
      id: 7,
      method: "tools/call",
      params: { name: "hades_ping", arguments: {} },
    });

    // Must resolve to a JSON-RPC error, NOT reject (which would surface as a raw HTTP 500).
    const result = await forwardToolCall(registry, "/proj/foo", body);
    const parsed = JSON.parse(result);
    expect(parsed.error).toBeDefined();
    expect(parsed.error.code).toBe(-32000);
    expect(parsed.id).toBe(7);
  });
});
