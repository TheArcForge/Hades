import { Registry } from "./registry.js";
import http from "node:http";

export async function forwardToolCall(
  registry: Registry,
  projectPath: string,
  body: string
): Promise<string> {
  const instance = registry.findByProjectPath(projectPath);

  if (!instance) {
    const available = registry
      .getAll()
      .map((i) => `  - ${i.projectName} (${i.projectPath})`)
      .join("\n");
    const listing = available || "  (none)";
    return JSON.stringify({
      jsonrpc: "2.0",
      id: extractId(body),
      error: {
        code: -32000,
        message: `No Unity instance found for ${projectPath}.\nRunning instances:\n${listing}`,
      },
    });
  }

  if (instance.status === "transient") {
    return await waitForTransientAndForward(instance.port, body);
  }

  try {
    return await httpPost(instance.port, body);
  } catch {
    // The instance was healthy in the registry but is now unreachable — typically it
    // began a domain reload between routing and forwarding. Return a clean JSON-RPC
    // error the client can retry, instead of letting the rejection surface as HTTP 500.
    return JSON.stringify({
      jsonrpc: "2.0",
      id: extractId(body),
      error: {
        code: -32000,
        message:
          "Unity instance unreachable (it may be reloading); please retry in a moment.",
      },
    });
  }
}

async function waitForTransientAndForward(
  port: number,
  body: string
): Promise<string> {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    try {
      const result = await httpPost(port, body);
      return result;
    } catch {
      await new Promise((r) => setTimeout(r, 500));
    }
  }
  return JSON.stringify({
    jsonrpc: "2.0",
    id: extractId(body),
    error: {
      code: -32000,
      message:
        "Unity is reloading, please retry in a moment.",
    },
  });
}

export async function fetchToolsList(port: number): Promise<string> {
  const request = JSON.stringify({
    jsonrpc: "2.0",
    id: "tools-list",
    method: "tools/list",
    params: {},
  });
  return await httpPost(port, request);
}

function httpPost(port: number, body: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      {
        hostname: "127.0.0.1",
        port,
        path: "/rpc",
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
        },
        timeout: 30_000,
      },
      (res) => {
        let data = "";
        res.on("data", (chunk: string) => (data += chunk));
        res.on("end", () => resolve(data));
      }
    );
    req.on("error", reject);
    req.on("timeout", () => {
      req.destroy();
      reject(new Error("Request timeout"));
    });
    req.write(body);
    req.end();
  });
}

function extractId(json: string): unknown {
  try {
    return JSON.parse(json).id ?? null;
  } catch {
    return null;
  }
}
