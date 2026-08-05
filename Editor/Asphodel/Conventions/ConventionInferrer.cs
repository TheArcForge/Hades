using System.Collections.Generic;
using System.IO;
using ArcForge.Hades.Editor.Graph;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>
    /// Graph-grounded convention inference. Sibling to PatternInferenceEngine, but reads the graph
    /// (not Charon traces). Reconciles a self-validating Tier-2 view and human-in-the-loop proposals.
    /// </summary>
    public sealed class ConventionInferrer
    {
        // Re-propose a dismissed convention only if it later fires markedly stronger than at dismissal.
        const double ReproposeDelta = 0.2;

        readonly MemoryManager _mem;
        readonly GraphDatabase _db;
        readonly List<IConventionDetector> _detectors;

        public ConventionInferrer(MemoryManager mem, GraphDatabase db, List<IConventionDetector> detectors)
        {
            _mem = mem; _db = db; _detectors = detectors;
        }

        string InferredDir => Path.Combine(_mem.MemoryDir, "inferred");
        string ProposalsDir => Path.Combine(_mem.MemoryDir, "proposals");
        string Tier2Path(string key) => Path.Combine(InferredDir, $"convention-{key}.md");
        string ProposalPath(string key) => Path.Combine(ProposalsDir, $"convention-{key}.md");

        public void Run()
        {
            Directory.CreateDirectory(InferredDir);
            var ledger = ConventionLedger.Load(InferredDir);

            foreach (var det in _detectors)
            {
                ConventionResult r;
                try { r = det.Detect(_db); }
                catch (System.Exception ex) { Debug.LogWarning($"[Hades] convention '{det.Key}' failed: {ex.Message}"); continue; }

                ReconcileTier2(det.Key, r);
                ReconcileLifecycle(det.Key, r, ledger);
            }

            ledger.Save(InferredDir);
        }

        void ReconcileTier2(string key, ConventionResult r)
        {
            var path = Tier2Path(key);
            if (r != null && r.Fired)
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, ConventionMarkdown.Tier2(key, r));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);   // self-validation: retract the inferred view when the signal is gone
            }
        }

        void ReconcileLifecycle(string key, ConventionResult r, ConventionLedger ledger)
        {
            var fired = r != null && r.Fired;
            var inTier1 = MarkerInTier1(key, fired ? r.TargetFile : null);
            var proposalExists = File.Exists(ProposalPath(key));
            var status = ledger.Status(key);

            // Resolve a previously-pending proposal that the dashboard acted on (file now gone).
            if (status == "pending" && !proposalExists)
            {
                if (inTier1) { ledger.Set(key, "promoted", ledger.Confidence(key)); status = "promoted"; }
                else { ledger.Set(key, "dismissed", ledger.Confidence(key)); status = "dismissed"; }
            }
            if (inTier1 && status != "promoted") { ledger.Set(key, "promoted", ledger.Confidence(key)); status = "promoted"; }

            if (fired)
            {
                if (status == "promoted" || inTier1) return;          // already confirmed
                if (proposalExists) return;                           // already pending
                if (status == "dismissed" && r.Confidence < ledger.Confidence(key) + ReproposeDelta) return;

                _mem.CreateProposal(r.TargetFile, ConventionMarkdown.ProposalBody(key, r),
                    $"{r.Evidence} (confidence {r.Confidence:P0})", id: $"convention-{key}");
                ledger.Set(key, "pending", r.Confidence);
            }
            else
            {
                // Promoted but no longer supported by the graph → flag stale (once).
                if (status == "promoted")
                {
                    var stalePath = Path.Combine(ProposalsDir, $"convention-stale-{key}.md");
                    if (!File.Exists(stalePath))
                    {
                        _mem.CreateProposal("conventions",
                            $"The previously-confirmed convention '{key}' is no longer supported by the project graph. Consider removing it.\n{ConventionMarkdown.Marker(key)}",
                            "Convention no longer detected in the graph", id: $"convention-stale-{key}");
                    }
                }
            }
        }

        bool MarkerInTier1(string key, string targetFile)
        {
            var marker = ConventionMarkdown.Marker(key);
            // The convention may have been accepted into either Tier-1 file; check the likely target(s).
            foreach (var name in new[] { targetFile, "patterns", "conventions" })
            {
                if (string.IsNullOrEmpty(name)) continue;
                var f = _mem.ReadFile(name);
                if (f != null && f.Body != null && f.Body.Contains(marker)) return true;
            }
            return false;
        }
    }
}
