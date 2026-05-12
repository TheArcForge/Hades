import { request } from "http";

export function formatJsonRpcRequest(id: number, method: string, params?: unknown): string {
  return JSON.stringify({
    jsonrpc: "2.0",
    id,
    method,
    params: params ?? {},
  });
}

export interface JsonRpcResponse {
  jsonrpc: string;
  id: number | string | null;
  result?: unknown;
  error?: { code: number; message: string };
}

export function parseJsonRpcResponse(json: string): JsonRpcResponse {
  return JSON.parse(json) as JsonRpcResponse;
}

export function buildHeaders(body: string, traceId?: string): Record<string, string | number> {
  const headers: Record<string, string | number> = {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body),
  };
  if (traceId) {
    headers["X-Hades-Trace-Id"] = traceId;
  }
  return headers;
}

export function sendToUnity(endpoint: string, body: string, traceId?: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint);
    const req = request(
      {
        hostname: url.hostname,
        port: url.port,
        path: url.pathname,
        method: "POST",
        headers: buildHeaders(body, traceId),
      },
      (res) => {
        let data = "";
        res.on("data", (chunk) => (data += chunk));
        res.on("end", () => resolve(data));
      }
    );
    req.on("error", reject);
    req.write(body);
    req.end();
  });
}
