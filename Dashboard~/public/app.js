/* global React, ReactDOM */

const { createElement: h, useState, useEffect, useCallback } = React;

function formatTime(unixMs) {
  if (!unixMs) return "—";
  const d = new Date(unixMs);
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function formatDate(unixMs) {
  if (!unixMs) return "";
  const d = new Date(unixMs);
  return d.toLocaleDateString([], { month: "short", day: "numeric" });
}

function formatDuration(ms) {
  if (ms == null) return "—";
  if (ms < 1) return "<1ms";
  if (ms < 1000) return ms + "ms";
  return (ms / 1000).toFixed(2) + "s";
}

function statusClass(status) {
  if (!status) return "unset";
  return status.toLowerCase();
}

async function fetchTraces(params) {
  const qs = new URLSearchParams();
  if (params.limit) qs.set("limit", params.limit);
  if (params.offset) qs.set("offset", params.offset);
  if (params.status) qs.set("status", params.status);
  if (params.name) qs.set("name", params.name);
  const res = await fetch("/api/traces?" + qs.toString());
  return res.json();
}

async function fetchTrace(id) {
  const res = await fetch("/api/traces/" + encodeURIComponent(id));
  if (!res.ok) return null;
  return res.json();
}

function StatusBadge({ status }) {
  return h("span", { className: "status-badge " + statusClass(status) }, status || "UNSET");
}

function Filters({ filters, onChange }) {
  return h("div", { className: "filters" },
    h("input", {
      type: "text",
      placeholder: "Filter by name...",
      value: filters.name || "",
      onChange: function(e) { onChange({ ...filters, name: e.target.value ? "%" + e.target.value + "%" : "" }); },
    }),
    h("select", {
      value: filters.status || "",
      onChange: function(e) { onChange({ ...filters, status: e.target.value }); },
    },
      h("option", { value: "" }, "All statuses"),
      h("option", { value: "Ok" }, "Ok"),
      h("option", { value: "Error" }, "Error"),
      h("option", { value: "Timeout" }, "Timeout")
    )
  );
}

function TraceRow({ trace, onClick }) {
  return h("div", { className: "trace-row", onClick: onClick },
    h("span", { className: "time" }, formatDate(trace.start_time) + " " + formatTime(trace.start_time)),
    h("span", { className: "name" }, trace.root_span_name),
    h("span", { className: "duration" }, formatDuration(trace.total_duration_ms)),
    h("span", null, h(StatusBadge, { status: trace.status })),
    h("span", { className: "spans" }, (trace.span_count || 0) + " spans")
  );
}

function Pagination({ offset, limit, total, onChange }) {
  var page = Math.floor(offset / limit) + 1;
  var totalPages = Math.max(1, Math.ceil(total / limit));

  return h("div", { className: "pagination" },
    h("button", { disabled: offset === 0, onClick: function() { onChange(Math.max(0, offset - limit)); } }, "Prev"),
    h("span", { style: { color: "var(--text-secondary)", fontSize: "13px", alignSelf: "center" } },
      "Page " + page + " of " + totalPages),
    h("button", { disabled: offset + limit >= total, onClick: function() { onChange(offset + limit); } }, "Next")
  );
}

function TraceListView({ onSelectTrace }) {
  var _a = useState([]), traces = _a[0], setTraces = _a[1];
  var _b = useState(0), total = _b[0], setTotal = _b[1];
  var _c = useState(true), loading = _c[0], setLoading = _c[1];
  var _d = useState({ name: "", status: "" }), filters = _d[0], setFilters = _d[1];
  var _e = useState(0), offset = _e[0], setOffset = _e[1];
  var limit = 50;

  var loadTraces = useCallback(function() {
    setLoading(true);
    fetchTraces({ limit: limit, offset: offset, status: filters.status, name: filters.name })
      .then(function(data) {
        setTraces(data.traces);
        setTotal(data.total);
      })
      .catch(function(err) { console.error("Failed to load traces:", err); })
      .finally(function() { setLoading(false); });
  }, [filters.status, filters.name, offset]);

  useEffect(function() { loadTraces(); }, [loadTraces]);
  useEffect(function() { setOffset(0); }, [filters.status, filters.name]);

  if (loading) return h("div", { className: "loading" }, "Loading traces...");

  return h("div", null,
    h(Filters, { filters: filters, onChange: setFilters }),
    traces.length === 0
      ? h("div", { className: "loading" }, "No traces found")
      : h("div", { className: "trace-list" },
          traces.map(function(t) {
            return h(TraceRow, { key: t.trace_id, trace: t, onClick: function() { onSelectTrace(t.trace_id); } });
          })
        ),
    total > limit
      ? h(Pagination, { offset: offset, limit: limit, total: total, onChange: setOffset })
      : null
  );
}

function buildSpanTree(spans) {
  var byId = {};
  spans.forEach(function(s) { byId[s.span_id] = Object.assign({}, s, { children: [] }); });

  var roots = [];
  spans.forEach(function(s) {
    if (s.parent_span_id && byId[s.parent_span_id]) {
      byId[s.parent_span_id].children.push(byId[s.span_id]);
    } else {
      roots.push(byId[s.span_id]);
    }
  });
  return roots;
}

function flattenTree(nodes, depth) {
  var result = [];
  for (var i = 0; i < nodes.length; i++) {
    var node = nodes[i];
    result.push(Object.assign({}, node, { depth: depth }));
    result = result.concat(flattenTree(node.children, depth + 1));
  }
  return result;
}

function SpanWaterfall({ spans, traceStart, traceDuration, onSelectSpan, selectedSpanId }) {
  var tree = buildSpanTree(spans);
  var flat = flattenTree(tree, 0);

  return h("div", { className: "waterfall" },
    flat.map(function(span) {
      var spanStart = span.start_time - traceStart;
      var spanDuration = span.end_time ? span.end_time - span.start_time : traceDuration - spanStart;
      var leftPct = traceDuration > 0 ? (spanStart / traceDuration) * 100 : 0;
      var widthPct = traceDuration > 0 ? (spanDuration / traceDuration) * 100 : 100;
      var isSelected = span.span_id === selectedSpanId;
      var indent = span.depth * 16;

      return h("div", {
        key: span.span_id,
        className: "span-row",
        style: isSelected ? { background: "rgba(233, 69, 96, 0.1)" } : undefined,
        onClick: function() { onSelectSpan(span.span_id === selectedSpanId ? null : span.span_id); },
      },
        h("span", { className: "span-label", style: { paddingLeft: (indent + 8) + "px" } }, span.name),
        h("span", { className: "span-bar-container" },
          h("span", {
            className: "span-bar " + statusClass(span.status),
            style: { left: leftPct + "%", width: Math.max(widthPct, 0.3) + "%" },
          })
        ),
        h("span", { className: "span-duration" }, formatDuration(span.end_time ? span.end_time - span.start_time : null))
      );
    })
  );
}

function SpanDetailPanel({ span }) {
  if (!span) return null;

  var attrs = span.attributes || {};
  var events = span.events || [];
  var entries = Object.entries(attrs);

  return h("div", { className: "span-detail" },
    h("h3", null, span.name),
    h("table", { className: "attr-table" },
      h("tbody", null,
        h("tr", null, h("td", null, "span_id"), h("td", null, span.span_id)),
        h("tr", null, h("td", null, "kind"), h("td", null, span.kind)),
        h("tr", null, h("td", null, "status"), h("td", null, h(StatusBadge, { status: span.status }))),
        h("tr", null, h("td", null, "start_time"), h("td", null, formatTime(span.start_time))),
        h("tr", null, h("td", null, "end_time"), h("td", null, span.end_time ? formatTime(span.end_time) : "—")),
        h("tr", null, h("td", null, "duration"), h("td", null, formatDuration(span.end_time ? span.end_time - span.start_time : null))),
        span.parent_span_id
          ? h("tr", null, h("td", null, "parent_span_id"), h("td", null, span.parent_span_id))
          : null,
        entries.map(function(kv) {
          return h("tr", { key: kv[0] }, h("td", null, kv[0]), h("td", null, String(kv[1])));
        })
      )
    ),
    events.length > 0
      ? h("div", { style: { marginTop: "12px" } },
          h("h4", { style: { fontSize: "13px", marginBottom: "8px" } }, "Events"),
          events.map(function(ev, i) {
            return h("div", { key: i, style: { fontSize: "12px", fontFamily: "var(--font-mono)", marginBottom: "4px" } },
              formatTime(ev.Timestamp || ev.timestamp) + " " + (ev.Name || ev.name),
              (ev.Attributes || ev.attributes)
                ? " " + JSON.stringify(ev.Attributes || ev.attributes)
                : ""
            );
          })
        )
      : null
  );
}

function TraceDetailView({ traceId, onBack }) {
  var _a = useState(null), data = _a[0], setData = _a[1];
  var _b = useState(true), loading = _b[0], setLoading = _b[1];
  var _c = useState(null), error = _c[0], setError = _c[1];
  var _d = useState(null), selectedSpanId = _d[0], setSelectedSpanId = _d[1];

  useEffect(function() {
    setLoading(true);
    setError(null);
    setSelectedSpanId(null);
    fetchTrace(traceId)
      .then(function(d) {
        if (!d) setError("Trace not found");
        else setData(d);
      })
      .catch(function(err) { setError(err.message); })
      .finally(function() { setLoading(false); });
  }, [traceId]);

  if (loading) return h("div", null,
    h("span", { className: "back-link", onClick: onBack }, "← Back to traces"),
    h("div", { className: "loading" }, "Loading...")
  );

  if (error) return h("div", null,
    h("span", { className: "back-link", onClick: onBack }, "← Back to traces"),
    h("div", { className: "error-msg" }, error)
  );

  var trace = data.trace;
  var spans = data.spans;
  var traceDuration = trace.total_duration_ms || (trace.end_time ? trace.end_time - trace.start_time : 0);
  var selectedSpan = selectedSpanId ? spans.find(function(s) { return s.span_id === selectedSpanId; }) : null;

  return h("div", null,
    h("span", { className: "back-link", onClick: onBack }, "← Back to traces"),
    h("div", { className: "trace-header" },
      h("h2", null, trace.root_span_name),
      h("div", { className: "trace-meta" },
        h("span", null, formatDate(trace.start_time) + " " + formatTime(trace.start_time)),
        h("span", null, "Duration: " + formatDuration(traceDuration)),
        h("span", null, h(StatusBadge, { status: trace.status })),
        h("span", null, (trace.span_count || spans.length) + " spans"),
        h("span", { style: { fontFamily: "var(--font-mono)", fontSize: "11px" } }, trace.trace_id)
      )
    ),
    h(SpanWaterfall, {
      spans: spans,
      traceStart: trace.start_time,
      traceDuration: traceDuration,
      onSelectSpan: setSelectedSpanId,
      selectedSpanId: selectedSpanId,
    }),
    h(SpanDetailPanel, { span: selectedSpan })
  );
}

// === Memory API helpers ===
async function fetchMemoryFiles() {
  var res = await fetch("/api/memory");
  return res.json();
}

async function fetchMemoryFile(filename) {
  var res = await fetch("/api/memory/" + encodeURIComponent(filename));
  if (!res.ok) return null;
  return res.json();
}

async function fetchProposals() {
  var res = await fetch("/api/proposals");
  return res.json();
}

async function acceptProposal(id) {
  var res = await fetch("/api/proposals/" + encodeURIComponent(id) + "/accept", { method: "POST" });
  return res.json();
}

async function rejectProposal(id) {
  var res = await fetch("/api/proposals/" + encodeURIComponent(id) + "/reject", { method: "POST" });
  return res.json();
}

// === Memory Views ===
function validationBadge(status) {
  var cls = status === "ok" ? "ok" : status === "warning" ? "warning" : "error";
  return h("span", { className: "status-badge " + cls }, status.toUpperCase());
}

function MemoryFileRow({ file, onClick }) {
  return h("div", { className: "trace-row", onClick: onClick },
    h("span", { className: "name" }, file.filename),
    h("span", null, validationBadge(file.validation_status)),
    h("span", { className: "time" }, file.last_reviewed ? "Reviewed: " + file.last_reviewed : ""),
    h("span", { className: "spans" }, Math.round(file.size / 1024 * 10) / 10 + " KB")
  );
}

function MemoryDetailView({ filename, onBack }) {
  var _a = useState(null), data = _a[0], setData = _a[1];
  var _b = useState(true), loading = _b[0], setLoading = _b[1];

  useEffect(function() {
    setLoading(true);
    fetchMemoryFile(filename)
      .then(function(d) { setData(d); })
      .finally(function() { setLoading(false); });
  }, [filename]);

  if (loading) return h("div", null,
    h("span", { className: "back-link", onClick: onBack }, "← Back to memory"),
    h("div", { className: "loading" }, "Loading...")
  );

  if (!data) return h("div", null,
    h("span", { className: "back-link", onClick: onBack }, "← Back to memory"),
    h("div", { className: "error-msg" }, "File not found")
  );

  return h("div", null,
    h("span", { className: "back-link", onClick: onBack }, "← Back to memory"),
    h("div", { className: "trace-header" },
      h("h2", null, data.filename),
      h("div", { className: "trace-meta" },
        h("span", null, validationBadge(data.validation_status)),
        data.last_reviewed ? h("span", null, "Reviewed: " + data.last_reviewed) : null,
        data.last_validated ? h("span", null, "Validated: " + data.last_validated.substring(0, 10)) : null
      )
    ),
    h("pre", { style: { whiteSpace: "pre-wrap", fontFamily: "var(--font-mono)", fontSize: "13px", background: "var(--bg-secondary)", padding: "16px", borderRadius: "8px", lineHeight: "1.5" } }, data.body)
  );
}

function MemoryListView() {
  var _a = useState([]), files = _a[0], setFiles = _a[1];
  var _b = useState(true), loading = _b[0], setLoading = _b[1];
  var _c = useState(null), selected = _c[0], setSelected = _c[1];

  useEffect(function() {
    setLoading(true);
    fetchMemoryFiles()
      .then(function(data) { setFiles(data.files || []); })
      .finally(function() { setLoading(false); });
  }, []);

  if (selected) {
    return h(MemoryDetailView, { filename: selected, onBack: function() { setSelected(null); } });
  }

  if (loading) return h("div", { className: "loading" }, "Loading memory files...");

  return h("div", null,
    files.length === 0
      ? h("div", { className: "loading" }, "No memory files found")
      : h("div", { className: "trace-list" },
          files.map(function(f) {
            return h(MemoryFileRow, { key: f.filename, file: f, onClick: function() { setSelected(f.filename); } });
          })
        )
  );
}

function ProposalRow({ proposal, onAccept, onReject }) {
  return h("div", { className: "trace-row", style: { flexDirection: "column", alignItems: "flex-start", gap: "8px" } },
    h("div", { style: { display: "flex", width: "100%", justifyContent: "space-between", alignItems: "center" } },
      h("span", { className: "name" }, "→ " + proposal.target_file + ".md"),
      h("span", { className: "time" }, proposal.created_at ? proposal.created_at.substring(0, 10) : "")
    ),
    h("div", { style: { fontSize: "12px", color: "var(--text-secondary)" } }, proposal.rationale),
    h("pre", { style: { fontSize: "11px", background: "var(--bg-secondary)", padding: "8px", borderRadius: "4px", margin: "0", whiteSpace: "pre-wrap", maxHeight: "120px", overflow: "auto", width: "100%" } }, proposal.content),
    h("div", { style: { display: "flex", gap: "8px" } },
      h("button", { onClick: function() { onAccept(proposal.id); }, style: { background: "#2ecc71", color: "#fff", border: "none", padding: "4px 12px", borderRadius: "4px", cursor: "pointer" } }, "Accept"),
      h("button", { onClick: function() { onReject(proposal.id); }, style: { background: "#e94560", color: "#fff", border: "none", padding: "4px 12px", borderRadius: "4px", cursor: "pointer" } }, "Reject")
    )
  );
}

function ProposalsView() {
  var _a = useState([]), proposals = _a[0], setProposals = _a[1];
  var _b = useState(true), loading = _b[0], setLoading = _b[1];

  var load = function() {
    setLoading(true);
    fetchProposals()
      .then(function(data) { setProposals(data.proposals || []); })
      .finally(function() { setLoading(false); });
  };

  useEffect(load, []);

  var handleAccept = function(id) {
    acceptProposal(id).then(load);
  };

  var handleReject = function(id) {
    rejectProposal(id).then(load);
  };

  if (loading) return h("div", { className: "loading" }, "Loading proposals...");

  return h("div", null,
    proposals.length === 0
      ? h("div", { className: "loading" }, "No pending proposals")
      : h("div", { className: "trace-list" },
          proposals.map(function(p) {
            return h(ProposalRow, { key: p.id, proposal: p, onAccept: handleAccept, onReject: handleReject });
          })
        )
  );
}

function App() {
  var _a = useState("traces"), tab = _a[0], setTab = _a[1];
  var _b = useState(null), selectedTraceId = _b[0], setSelectedTraceId = _b[1];

  useEffect(function() {
    var hash = window.location.hash.slice(1);
    if (hash.startsWith("trace/")) setSelectedTraceId(hash.slice(6));
    else if (hash === "memory") setTab("memory");
    else if (hash === "proposals") setTab("proposals");
  }, []);

  var selectTrace = function(id) {
    window.location.hash = "trace/" + id;
    setSelectedTraceId(id);
  };

  var goBackTraces = function() {
    window.location.hash = "";
    setSelectedTraceId(null);
  };

  var switchTab = function(t) {
    setTab(t);
    setSelectedTraceId(null);
    window.location.hash = t === "traces" ? "" : t;
  };

  var content;
  if (tab === "traces") {
    content = selectedTraceId
      ? h(TraceDetailView, { traceId: selectedTraceId, onBack: goBackTraces })
      : h(TraceListView, { onSelectTrace: selectTrace });
  } else if (tab === "memory") {
    content = h(MemoryListView);
  } else if (tab === "proposals") {
    content = h(ProposalsView);
  }

  return h("div", null,
    h("div", { className: "header" },
      h("h1", null, "Charon"),
      h("div", { className: "tab-bar" },
        h("button", { className: "tab-btn" + (tab === "traces" ? " active" : ""), onClick: function() { switchTab("traces"); } }, "Traces"),
        h("button", { className: "tab-btn" + (tab === "memory" ? " active" : ""), onClick: function() { switchTab("memory"); } }, "Memory"),
        h("button", { className: "tab-btn" + (tab === "proposals" ? " active" : ""), onClick: function() { switchTab("proposals"); } }, "Proposals")
      )
    ),
    h("div", { className: "container" }, content)
  );
}

var root = ReactDOM.createRoot(document.getElementById("root"));
root.render(h(App));
