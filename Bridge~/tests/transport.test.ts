import { describe, it, expect } from "vitest";
import { formatJsonRpcRequest, parseJsonRpcResponse } from "../src/transport.js";

describe("formatJsonRpcRequest", () => {
  it("formats a valid JSON-RPC request", () => {
    const result = formatJsonRpcRequest(1, "tools/call", { name: "hades_ping", arguments: {} });
    const parsed = JSON.parse(result);

    expect(parsed.jsonrpc).toBe("2.0");
    expect(parsed.id).toBe(1);
    expect(parsed.method).toBe("tools/call");
    expect(parsed.params.name).toBe("hades_ping");
  });

  it("uses empty object for params when not provided", () => {
    const result = formatJsonRpcRequest(2, "initialize");
    const parsed = JSON.parse(result);

    expect(parsed.params).toEqual({});
  });
});

describe("parseJsonRpcResponse", () => {
  it("parses a success response", () => {
    const json = JSON.stringify({
      jsonrpc: "2.0",
      id: 1,
      result: { content: [{ type: "text", text: "hello" }] },
    });
    const result = parseJsonRpcResponse(json);

    expect(result.id).toBe(1);
    expect(result.result).toBeDefined();
    expect(result.error).toBeUndefined();
  });

  it("parses an error response", () => {
    const json = JSON.stringify({
      jsonrpc: "2.0",
      id: 2,
      error: { code: -32601, message: "Method not found" },
    });
    const result = parseJsonRpcResponse(json);

    expect(result.id).toBe(2);
    expect(result.error).toBeDefined();
    expect(result.error!.code).toBe(-32601);
  });
});
