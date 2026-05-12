import { Router } from "express";
import type { TracesDB } from "../db.js";

export function createTracesRouter(db: TracesDB): Router {
  const router = Router();

  router.get("/traces", (req, res) => {
    const parsedLimit = parseInt(req.query.limit as string);
    const limit = Math.min(Math.max(isNaN(parsedLimit) ? 50 : parsedLimit, 0), 200);
    const offset = Math.max(parseInt(req.query.offset as string) || 0, 0);
    const status = req.query.status as string | undefined;
    const namePattern = req.query.name as string | undefined;

    const traces = db.listTraces({ limit, offset, status, namePattern });

    const total = db.countTraces({ status, namePattern });

    res.json({ traces, total });
  });

  router.get("/traces/:id", (req, res) => {
    const trace = db.getTrace(req.params.id);
    if (!trace) {
      res.status(404).json({ error: "Trace not found" });
      return;
    }

    const spans = db.getSpans(req.params.id);
    res.json({ trace, spans });
  });

  return router;
}
