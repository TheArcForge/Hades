// Dashboard~/src/server.ts
import express from "express";
import { fileURLToPath } from "url";
import { dirname, join, resolve } from "path";
import { existsSync, writeFileSync } from "fs";
import { TracesDB } from "./db.js";
import { MemoryDB } from "./memory-db.js";
import { createTracesRouter } from "./api/traces.js";
import { createMemoryRouter } from "./api/memory.js";
import type { AddressInfo } from "net";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

function parseArgs(): { dbPath: string; memoryDir: string } {
  const args = process.argv.slice(2);
  let dbPath = "";
  let memoryDir = "";

  for (let i = 0; i < args.length; i++) {
    if (args[i] === "--db" && i + 1 < args.length) {
      dbPath = args[i + 1];
      i++;
    }
    if (args[i] === "--memory" && i + 1 < args.length) {
      memoryDir = args[i + 1];
      i++;
    }
  }

  if (!dbPath) {
    console.error("Usage: hades-dashboard --db <path-to-traces.db> [--memory <path-to-memory-dir>]");
    process.exit(1);
  }

  if (!memoryDir) {
    memoryDir = resolve(dbPath, "..", "memory");
  }

  return { dbPath: resolve(dbPath), memoryDir: resolve(memoryDir) };
}

async function main() {
  const { dbPath, memoryDir } = parseArgs();

  if (!existsSync(dbPath)) {
    console.error(`[hades-dashboard] Database not found: ${dbPath}`);
    process.exit(1);
  }

  const tracesDb = new TracesDB(dbPath);
  const memoryDb = new MemoryDB(memoryDir);
  const app = express();

  app.use(express.json());
  app.use("/api", createTracesRouter(tracesDb));
  app.use("/api", createMemoryRouter(memoryDb));

  const publicDir = join(__dirname, "..", "public");
  app.use(express.static(publicDir));

  app.get("*", (_req, res) => {
    res.sendFile(join(publicDir, "index.html"));
  });

  const server = app.listen(0, "127.0.0.1", () => {
    const port = (server.address() as AddressInfo).port;

    console.log(`[hades-dashboard] Running at http://127.0.0.1:${port}`);
    console.log(`[hades-dashboard] Reading traces from ${dbPath}`);
    console.log(`[hades-dashboard] Reading memory from ${memoryDir}`);

    const portFile = process.env.HADES_PORT_FILE;
    if (portFile) {
      writeFileSync(portFile, String(port), "utf-8");
    }
  });

  process.on("SIGINT", () => {
    tracesDb.close();
    process.exit(0);
  });

  process.on("SIGTERM", () => {
    tracesDb.close();
    process.exit(0);
  });
}

main();
