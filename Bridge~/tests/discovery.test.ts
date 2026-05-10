import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { readDiscoveryFile } from "../src/discovery.js";
import { writeFileSync, mkdirSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";

describe("readDiscoveryFile", () => {
  let testDir: string;

  beforeEach(() => {
    testDir = join(tmpdir(), `hades-test-${Date.now()}`);
    mkdirSync(testDir, { recursive: true });
  });

  afterEach(() => {
    rmSync(testDir, { recursive: true, force: true });
  });

  it("reads port and endpoint from valid file", () => {
    const filePath = join(testDir, "server.json");
    writeFileSync(
      filePath,
      JSON.stringify({ port: 7780, endpoint: "http://127.0.0.1:7780/rpc", pid: 123 })
    );

    const result = readDiscoveryFile(filePath);

    expect(result).not.toBeNull();
    expect(result!.port).toBe(7780);
    expect(result!.endpoint).toBe("http://127.0.0.1:7780/rpc");
    expect(result!.pid).toBe(123);
  });

  it("returns null for missing file", () => {
    const result = readDiscoveryFile(join(testDir, "nope.json"));
    expect(result).toBeNull();
  });

  it("returns null for invalid JSON", () => {
    const filePath = join(testDir, "server.json");
    writeFileSync(filePath, "not json{{{");

    const result = readDiscoveryFile(filePath);
    expect(result).toBeNull();
  });
});
