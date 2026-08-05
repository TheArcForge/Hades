import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { mkdtempSync, rmSync, mkdirSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { MemoryDB } from "../src/memory-db";

describe("MemoryDB path traversal", () => {
  let dir: string;
  let db: MemoryDB;

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), "hades-mem-"));
    mkdirSync(join(dir, "proposals"), { recursive: true });
    db = new MemoryDB(dir);
  });
  afterEach(() => rmSync(dir, { recursive: true, force: true }));

  it("getFile refuses traversal", () => {
    expect(db.getFile("../../etc/passwd")).toBeNull();
    expect(db.getFile("../secret")).toBeNull();
  });

  it("acceptProposal refuses a traversal target_file and writes nothing outside the dir", () => {
    writeFileSync(
      join(dir, "proposals", "poison.md"),
      "---\ntarget_file: ../escape\ncreated_at: 2026-01-01T00:00:00Z\nrationale: x\nstatus: pending\n---\nBODY"
    );
    expect(db.acceptProposal("poison")).toBe(false);
    expect(existsSync(join(dir, "..", "escape.md"))).toBe(false);
  });

  it("getInferredFile refuses traversal", () => {
    expect(db.getInferredFile("../../etc/passwd")).toBeNull();
  });
});
