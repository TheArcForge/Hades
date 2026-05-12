import { describe, it, expect } from "vitest";
import { buildHeaders } from "../src/transport.js";

describe("buildHeaders", () => {
  it("includes Content-Type and Content-Length", () => {
    const headers = buildHeaders("test-body", undefined);
    expect(headers["Content-Type"]).toBe("application/json");
    expect(headers["Content-Length"]).toBe(Buffer.byteLength("test-body"));
  });

  it("includes X-Hades-Trace-Id when provided", () => {
    const traceId = "aaaabbbbccccddddeeeeffffaaaabbbb";
    const headers = buildHeaders("body", traceId);
    expect(headers["X-Hades-Trace-Id"]).toBe(traceId);
  });

  it("omits X-Hades-Trace-Id when not provided", () => {
    const headers = buildHeaders("body", undefined);
    expect(headers["X-Hades-Trace-Id"]).toBeUndefined();
  });
});
