import { createInterface } from "readline";
import { readDiscoveryFile } from "./discovery.js";
import { sendToUnity } from "./transport.js";
import { existsSync } from "fs";
import { join } from "path";

function findDiscoveryFile(): string | null {
  let dir = process.cwd();

  while (true) {
    const candidate = join(dir, ".arcforge", "server.json");
    if (existsSync(candidate)) return candidate;
    const parent = join(dir, "..");
    if (parent === dir) break;
    dir = parent;
  }

  return null;
}

async function main() {
  const discoveryPath = findDiscoveryFile();
  if (!discoveryPath) {
    process.stderr.write("[hades-bridge] No .arcforge/server.json found. Is Unity running with Hades?\n");
    process.exit(1);
  }

  const discovery = readDiscoveryFile(discoveryPath);
  if (!discovery) {
    process.stderr.write("[hades-bridge] Failed to read discovery file.\n");
    process.exit(1);
  }

  process.stderr.write(`[hades-bridge] Connected to Hades at ${discovery.endpoint}\n`);

  const rl = createInterface({ input: process.stdin });

  rl.on("line", async (line) => {
    if (!line.trim()) return;

    try {
      const response = await sendToUnity(discovery.endpoint, line);
      if (response) {
        process.stdout.write(response + "\n");
      }
    } catch (err) {
      const errorResponse = JSON.stringify({
        jsonrpc: "2.0",
        id: null,
        error: { code: -32000, message: `Bridge error: ${err}` },
      });
      process.stdout.write(errorResponse + "\n");
    }
  });

  rl.on("close", () => {
    process.exit(0);
  });
}

main();
