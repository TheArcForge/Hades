import { Registry } from "./registry.js";
export declare function forwardToolCall(registry: Registry, projectPath: string, body: string): Promise<string>;
export declare function fetchToolsList(port: number): Promise<string>;
