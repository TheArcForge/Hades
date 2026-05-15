import { InstanceEntry, RegisterRequest, DeregisterRequest, HeartbeatRequest } from "./types.js";
export declare class Registry {
    private instances;
    private _launcherCount;
    get launcherCount(): number;
    register(req: RegisterRequest): void;
    deregister(req: DeregisterRequest): void;
    heartbeat(req: HeartbeatRequest): boolean;
    get(projectPath: string): InstanceEntry | null;
    getAll(): InstanceEntry[];
    markStale(projectPath: string): void;
    markHealthy(projectPath: string): void;
    remove(projectPath: string): void;
    launcherConnect(): void;
    launcherDisconnect(): void;
    isEmpty(): boolean;
    instanceCount(): number;
    findByProjectPath(cwd: string): InstanceEntry | null;
}
