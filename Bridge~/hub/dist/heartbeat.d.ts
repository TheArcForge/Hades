import { Registry } from "./registry.js";
export declare const HEARTBEAT_STALE_MS = 90000;
export declare const TRANSIENT_TIMEOUT_MS = 30000;
export declare const STALE_PURGE_MS = 300000;
export type ProbeFunction = (port: number) => Promise<boolean>;
export declare function checkStaleInstances(registry: Registry, probe: ProbeFunction): Promise<void>;
export declare function probeUnityInstance(port: number): Promise<boolean>;
