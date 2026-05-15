import http from "node:http";
export async function forwardToolCall(registry, projectPath, body) {
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
    return await httpPost(instance.port, body);
}
async function waitForTransientAndForward(port, body) {
    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
        try {
            const result = await httpPost(port, body);
            return result;
        }
        catch {
            await new Promise((r) => setTimeout(r, 500));
        }
    }
    return JSON.stringify({
        jsonrpc: "2.0",
        id: extractId(body),
        error: {
            code: -32000,
            message: "Unity is reloading, please retry in a moment.",
        },
    });
}
export async function fetchToolsList(port) {
    const request = JSON.stringify({
        jsonrpc: "2.0",
        id: "tools-list",
        method: "tools/list",
        params: {},
    });
    return await httpPost(port, request);
}
function httpPost(port, body) {
    return new Promise((resolve, reject) => {
        const req = http.request({
            hostname: "127.0.0.1",
            port,
            path: "/rpc",
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Content-Length": Buffer.byteLength(body),
            },
            timeout: 30_000,
        }, (res) => {
            let data = "";
            res.on("data", (chunk) => (data += chunk));
            res.on("end", () => resolve(data));
        });
        req.on("error", reject);
        req.on("timeout", () => {
            req.destroy();
            reject(new Error("Request timeout"));
        });
        req.write(body);
        req.end();
    });
}
function extractId(json) {
    try {
        return JSON.parse(json).id ?? null;
    }
    catch {
        return null;
    }
}
//# sourceMappingURL=mcp-handler.js.map