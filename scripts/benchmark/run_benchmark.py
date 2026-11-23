#!/usr/bin/env python3
"""run_benchmark.py - run the Hades benchmark arms headlessly and record raw results.

Read-only against the Unity project; standard library only, Python 3.9+.
Design note: docs/design/BENCHMARK-01.md.

What it does
  For each (task, arm, repeat) it starts a FRESH `claude -p` session, captures the whole
  result envelope, decides whether the run is valid, and writes one JSON file per run.

What it deliberately does NOT do
  It never scores. Scoring is a separate, blind step over the final answers only (see the
  design note), because an arm's transcript reveals which arm it is.

Three traps this guards against, each found by running the thing rather than reading docs:
  1. A FAILED RUN LOOKS LIKE A SUCCESSFUL ONE. An auth failure returns
     {"subtype":"success","is_error":true,"api_error_status":401,"total_cost_usd":0} with the
     error text sitting in `result`. Scoring that would record an error message as an answer
     and average a zero into the cost.
  2. `claude auth status` reports loggedIn:true while every call 401s, so it is NOT a valid
     preflight. The only honest preflight is a real call.
  3. The CLI waits on stdin and may print a non-JSON warning before the JSON object.
"""

import argparse
import datetime
import json
import pathlib
import re
import subprocess
import sys
import time

DEFAULT_SERVER = "http://127.0.0.1:7823/mcp"


def arm_specs(server_url):
    """Both arms pass --strict-mcp-config: this account has unrelated MCP servers
    (Gmail, Drive, Calendar, ...) that would otherwise load into every session and
    pollute both the tool surface and the token counts."""
    return {
        # control: no MCP at all, full default tool set (Bash/Read/Grep/Glob)
        "A": {"mcp": {"mcpServers": {}}, "slash_commands": False},
        # headline: the graph alone - no skills, no commands, so this measures the
        # graph rather than the graph plus 23 skills' worth of Unity prose
        "B": {"mcp": {"mcpServers": {"hades": {"type": "http", "url": server_url}}},
              "slash_commands": False},
        # optional: what a user actually installs
        "C": {"mcp": {"mcpServers": {"hades": {"type": "http", "url": server_url}}},
              "slash_commands": True},
        # P - the plugin, delivered the way a real user installs it rather than through
        # --mcp-config. Exists to test whether the delivery mechanism, not the product, is
        # what left the tools deferred and unused in the 2026-11-21 dry runs.
        "P": {"mcp": None, "slash_commands": True,
              "plugin_dir": "/Users/mike/Projects/Hades/ClaudeCodePlugin"},
        # D - C plus one line telling the agent the graph exists. This is the analogue of the
        # MCP `instructions` field Hades does not yet send, so C vs D prices discoverability.
        "D": {"mcp": {"mcpServers": {"hades": {"type": "http", "url": server_url}}},
              "slash_commands": True,
              "append_system": (
                  "This Unity project has a pre-built Hades knowledge graph available over MCP. "
                  "The hades tools can answer questions about asset references, dependencies and "
                  "scene structure directly, without searching files.")},
    }


def extract_json(raw):
    """The CLI may precede the JSON object with a plain-text warning."""
    raw = raw.strip()
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        pass
    m = re.search(r"\{.*\}", raw, re.S)
    if not m:
        raise ValueError(f"no JSON object in output: {raw[:200]!r}")
    return json.loads(m.group(0))


def tree_snapshot(project):
    """`git status --porcelain` plus the HEAD sha. AutoDRIVE already carries pre-existing
    deletions (the extracted .zip archives), so this compares the snapshot before a run with
    the snapshot after it, not against an empty tree."""
    try:
        head = subprocess.run(["git", "-C", project, "rev-parse", "HEAD"],
                              capture_output=True, text=True, timeout=60)
        status = subprocess.run(["git", "-C", project, "status", "--porcelain"],
                                capture_output=True, text=True, timeout=300)
    except (subprocess.TimeoutExpired, OSError) as exc:
        return f"UNAVAILABLE: {exc}"
    if head.returncode != 0 or status.returncode != 0:
        return "UNAVAILABLE: not a git checkout"
    return head.stdout.strip() + "\n" + status.stdout


def tool_calls_from_stream(stdout):
    """Which tools each run actually used, counted from --output-format stream-json.

    This exists because the plain `json` envelope does not say. In the 2026-11-17 dry run the
    only evidence that the graph arm never touched the graph was the agent's own prose, which
    is not evidence. `hades_used` is the fact the A-vs-B-vs-C comparison turns on.
    """
    counts, final = {}, None
    for line in stdout.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            ev = json.loads(line)
        except json.JSONDecodeError:
            continue
        if ev.get("type") == "result":
            final = ev
        msg = ev.get("message") or {}
        for block in msg.get("content") or []:
            if isinstance(block, dict) and block.get("type") == "tool_use":
                counts[block.get("name", "?")] = counts.get(block.get("name", "?"), 0) + 1
    return counts, final


def verdict(env):
    """Valid, or the reason it is void. Never score a void run; re-run it."""
    if env.get("is_error"):
        return False, f"is_error (api_error_status={env.get('api_error_status')})"
    if env.get("api_error_status") is not None:
        return False, f"api_error_status={env.get('api_error_status')}"
    denials = env.get("permission_denials") or []
    if denials:
        return False, f"permission_denials={len(denials)}"
    if not str(env.get("result") or "").strip():
        return False, "empty result"
    return True, None


def run_once(prompt, arm, spec, model, cwd, mcp_path, timeout, permission_mode):
    cmd = [
        "claude", "-p", prompt,
        "--output-format", "stream-json", "--verbose",
        "--model", model,
        # Identical for every arm. The dry run of 2026-11-17 had `Bash` denied in BOTH arms,
        # which would have crippled the shell-only control while the graph arm kept 33 MCP
        # tools - a result that reads as "the graph wins" when the opponent was blocked.
        "--permission-mode", permission_mode,
    ]
    if spec.get("mcp") is not None:
        mcp_path.write_text(json.dumps(spec["mcp"]))
        cmd += ["--strict-mcp-config", "--mcp-config", str(mcp_path)]
    if spec.get("plugin_dir"):
        cmd += ["--plugin-dir", spec["plugin_dir"]]
    if spec.get("append_system"):
        cmd += ["--append-system-prompt", spec["append_system"]]
    if not spec["slash_commands"]:
        cmd.append("--disable-slash-commands")

    before = tree_snapshot(cwd)
    started = time.time()
    with open("/dev/null") as devnull:          # the CLI blocks waiting on stdin
        proc = subprocess.run(cmd, cwd=cwd, stdin=devnull,
                              capture_output=True, text=True, timeout=timeout)
    wall = time.time() - started

    after = tree_snapshot(cwd)
    tree_intact = (before == after) and not before.startswith("UNAVAILABLE")

    tool_counts, env = tool_calls_from_stream(proc.stdout)
    try:
        if env is None:
            env = extract_json(proc.stdout)
    except ValueError as exc:
        return {"arm": arm, "valid": False, "void_reason": f"unparseable: {exc}",
                "wall_seconds": wall, "returncode": proc.returncode,
                "tree_intact": tree_intact, "tool_counts": tool_counts,
                "stdout_head": proc.stdout[:500], "stderr_head": proc.stderr[:500]}

    ok, reason = verdict(env)
    # Every task is a read-only question against a pinned checkout, so a modified tree means
    # something went wrong. Void and flag it rather than scoring it.
    if ok and not tree_intact:
        ok, reason = False, ("project tree modified during the run"
                             if not before.startswith("UNAVAILABLE")
                             else f"tree check unavailable ({before})")
    usage = env.get("usage", {}) or {}
    return {
        "arm": arm,
        "valid": ok,
        "void_reason": reason,
        "wall_seconds": round(wall, 2),
        "returncode": proc.returncode,
        "answer": env.get("result"),
        "session_id": env.get("session_id"),
        "stop_reason": env.get("stop_reason"),
        "num_turns": env.get("num_turns"),
        "duration_ms": env.get("duration_ms"),
        "duration_api_ms": env.get("duration_api_ms"),
        # a locally computed list-price estimate, NOT a bill - these runs are made on a
        # Max subscription at no marginal cost. Never report this as money spent.
        "list_price_equivalent_usd": env.get("total_cost_usd"),
        "input_tokens": usage.get("input_tokens"),
        "output_tokens": usage.get("output_tokens"),
        "cache_read_input_tokens": usage.get("cache_read_input_tokens"),
        "cache_creation_input_tokens": usage.get("cache_creation_input_tokens"),
        "model_usage": list((env.get("modelUsage") or {}).keys()),
        "permission_denials": env.get("permission_denials"),
        "tree_intact": tree_intact,
        "tool_counts": tool_counts,
        "tool_calls_total": sum(tool_counts.values()),
        # the fact the whole comparison turns on: did this arm actually touch the graph?
        "hades_used": any(("hades" in name.lower()) for name in tool_counts),
        "hades_calls": sum(n for name, n in tool_counts.items() if "hades" in name.lower()),
    }


def preflight(model, mcp_path, cwd, timeout, permission_mode):
    """A real call. `claude auth status` says loggedIn:true while calls 401."""
    mcp_path.write_text(json.dumps({"mcpServers": {}}))
    with open("/dev/null") as devnull:
        proc = subprocess.run(
            ["claude", "-p", "Reply with exactly: ok", "--output-format", "json",
             "--model", model, "--strict-mcp-config", "--mcp-config", str(mcp_path),
             "--permission-mode", permission_mode],
            cwd=cwd, stdin=devnull, capture_output=True, text=True, timeout=timeout)
    try:
        env = extract_json(proc.stdout)
    except ValueError as exc:
        return False, str(exc)
    ok, reason = verdict(env)
    return ok, reason


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tasks", required=True, help="JSON file: [{id, prompt}, ...]")
    ap.add_argument("--project", required=True, help="Unity project root (the agent's cwd)")
    ap.add_argument("--out", required=True, help="results directory")
    ap.add_argument("--arms", default="A,B")
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--model", default="claude-opus-5-5")
    ap.add_argument("--server", default=DEFAULT_SERVER)
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--permission-mode", default="bypassPermissions",
                    help="identical for every arm; asymmetric tool access voids the comparison")
    ap.add_argument("--label", default="", help="tag written into every record")
    args = ap.parse_args()

    tasks = json.loads(pathlib.Path(args.tasks).read_text())
    arms = [a.strip().upper() for a in args.arms.split(",") if a.strip()]
    specs = arm_specs(args.server)
    for a in arms:
        if a not in specs:
            sys.exit(f"unknown arm {a!r}; known: {sorted(specs)}")

    outdir = pathlib.Path(args.out)
    outdir.mkdir(parents=True, exist_ok=True)
    mcp_path = outdir / ".mcp-config.json"

    print(f"preflight ({args.model}) ...", flush=True)
    ok, reason = preflight(args.model, mcp_path, args.project, 180, args.permission_mode)
    if not ok:
        sys.exit(f"preflight FAILED: {reason}\n"
                 f"Run `claude auth login` - note that `claude auth status` can report\n"
                 f"loggedIn:true while every call still returns 401.")
    print("preflight ok\n", flush=True)

    records, void = [], 0
    for task in tasks:
        for arm in arms:
            for rep in range(1, args.runs + 1):
                tag = f"{task['id']}/{arm}/run{rep}"
                print(f"  {tag} ...", end=" ", flush=True)
                rec = run_once(task["prompt"], arm, specs[arm], args.model,
                               args.project, mcp_path, args.timeout,
                               args.permission_mode)
                rec.update({"task_id": task["id"], "repeat": rep, "model": args.model,
                            "label": args.label,
                            "started_utc": datetime.datetime.now(
                                datetime.timezone.utc).isoformat()})
                (outdir / f"{task['id']}_{arm}_run{rep}.json").write_text(
                    json.dumps(rec, indent=2))
                records.append(rec)
                if rec["valid"]:
                    print(f"ok  {rec['num_turns']} turns  "
                          f"${rec['list_price_equivalent_usd']:.4f} lpe  "
                          f"{rec['wall_seconds']}s  "
                          f"{rec['tool_calls_total']} tool calls"
                          f"{' (hades x%d)' % rec['hades_calls'] if rec['hades_used'] else ' (no hades)'}",
                          flush=True)
                else:
                    void += 1
                    print(f"VOID ({rec['void_reason']}) - re-run this one", flush=True)

    (outdir / "_all_runs.json").write_text(json.dumps(records, indent=2))
    valid = [r for r in records if r["valid"]]
    print(f"\n{len(valid)} valid, {void} void -> {outdir}")
    if valid:
        total = sum(r["list_price_equivalent_usd"] or 0 for r in valid)
        print(f"list-price equivalent (NOT a bill): ${total:.4f}")


if __name__ == "__main__":
    main()
