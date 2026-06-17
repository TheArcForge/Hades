import { InstanceEntry, RegisterRequest, DeregisterRequest, HeartbeatRequest } from "./types.js";
export declare class Registry {
    private instances;
    private _launcherCount;
    private _lastLauncherActivity;
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
    /** Record any launcher request (connect or /rpc forward), so an actively-used hub stays
     * alive without relying on a disconnect notification that abrupt exits never send. */
    noteLauncherActivity(): void;
    isEmpty(): boolean;
    /** Auto-exit gate: no Unity instances AND no launcher activity within `autoExitMs`. Unlike
     * isEmpty() this is robust to leaked launcher counts, so the hub stops being immortal. */
    isIdle(autoExitMs: number, now?: number): boolean;
    instanceCount(): number;
    findByProjectPath(cwd: string): InstanceEntry | null;
}
