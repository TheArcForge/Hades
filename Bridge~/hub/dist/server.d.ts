import http from "node:http";
import { Registry } from "./registry.js";
export interface HubServer {
    server: http.Server;
    registry: Registry;
    port: number;
    close: () => Promise<void>;
}
export declare function createHubServer(): Promise<HubServer>;
