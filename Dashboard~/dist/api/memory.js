// Dashboard~/src/api/memory.ts
import { Router } from "express";
export function createMemoryRouter(db) {
    const router = Router();
    router.get("/memory", (_req, res) => {
        const files = db.listFiles();
        res.json({ files });
    });
    router.get("/memory/:filename", (req, res) => {
        const file = db.getFile(req.params.filename);
        if (!file) {
            res.status(404).json({ error: "Memory file not found" });
            return;
        }
        res.json(file);
    });
    router.get("/proposals", (_req, res) => {
        const proposals = db.listProposals();
        res.json({ proposals });
    });
    router.post("/proposals/:id/accept", (req, res) => {
        const ok = db.acceptProposal(req.params.id);
        if (!ok) {
            res.status(404).json({ error: "Proposal not found" });
            return;
        }
        res.json({ status: "accepted" });
    });
    router.post("/proposals/:id/reject", (req, res) => {
        const ok = db.rejectProposal(req.params.id);
        if (!ok) {
            res.status(404).json({ error: "Proposal not found" });
            return;
        }
        res.json({ status: "rejected" });
    });
    router.get("/inferred", (_req, res) => {
        const files = db.listInferredFiles();
        res.json({ files });
    });
    router.get("/inferred/:filename", (req, res) => {
        const file = db.getInferredFile(req.params.filename);
        if (!file) {
            res.status(404).json({ error: "Inferred file not found" });
            return;
        }
        res.json(file);
    });
    return router;
}
//# sourceMappingURL=memory.js.map