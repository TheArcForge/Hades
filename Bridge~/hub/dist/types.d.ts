export type InstanceStatus = "healthy" | "transient" | "stale";
export interface InstanceEntry {
    projectName: string;
    projectPath: string;
    port: number;
    pid: number;
    registeredAt: number;
    lastHeartbeat: number;
    status: InstanceStatus;
    transientSince: number | null;
    manifestPackages?: string[];
}
export interface RegisterRequest {
    projectName: string;
    projectPath: string;
    port: number;
    pid: number;
    manifestPackages?: string[];
}
export interface DeregisterRequest {
    projectPath: string;
    transient: boolean;
}
export interface HeartbeatRequest {
    projectPath: string;
    port: number;
    pid: number;
}
export interface HubInfo {
    port: number;
    pid: number;
    startedAt: number;
}
