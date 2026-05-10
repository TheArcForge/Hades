import { readFileSync, existsSync } from "fs";

export interface DiscoveryData {
  port: number;
  endpoint: string;
  pid: number;
}

export function readDiscoveryFile(filePath: string): DiscoveryData | null {
  if (!existsSync(filePath)) {
    return null;
  }

  try {
    const content = readFileSync(filePath, "utf-8");
    const data = JSON.parse(content);
    return {
      port: data.port,
      endpoint: data.endpoint,
      pid: data.pid,
    };
  } catch {
    return null;
  }
}
