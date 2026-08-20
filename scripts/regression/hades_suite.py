#!/usr/bin/env python3
"""Hades regression suite — executable form of FINDINGS.md.

Adopted from an internal tester's findings bundle into this repo. Round 2 (2026-08-14 bundle: 22
Part B cases, up from 17) landed as P1-G7/A1/E1-E11; on top of it we added two of our own F21
cycle-refusal guards (now E13-E14). Round 3 (2026-08-15 bundle) added one new open finding, F22,
as E12 — the tester's own numbering, kept authoritative here — and refined E2's setup so it no
longer overlaps F22 (see E2's docstring). Anchors are re-pointed at Hades-Unity-Client, as in
round 1; layered on top of the tester's cases and harness are our own additions carried forward
from round 1 — the --url flag / HADES_URL env var, and HADES_HOME awareness in case A1. See
README.md in this directory for how to run it and how to read the output.

Two parts:

  PART A  replayed through the app's own `hades_regression` tool from a recorded fixture.

  PART B  run directly over MCP by this script. Originally because of F15 — at 0.1.0 the recorder
          captured only Editor-routed calls (2 of 6 in a mixed probe), so the graph/read surface
          could not be expressed as a fixture at all. F15 is fixed as of 2.0.0-beta.2 (5 of 6), but
          Part B stays direct: P1-P3 assert on tools/list and initialize, which are not tool calls
          and can never be fixtures, and several cases assert structure ("must error", "GUID must
          not change") rather than "equals the recorded value".

Every case declares what it should do TODAY:

  expect="fail"  an open finding. Failing = the bug is still there. PASSING IS NEWS: the bug is
                 fixed and the case should be flipped to expect="pass" to guard it.
  expect="pass"  behaviour that is currently correct, asserted so a future change cannot break it
                 silently.

Exit code is 0 when every case matched its expectation, 1 otherwise. Read the table, not just the
exit code.

STATE AS OF THIS MERGE (2026-08-15, tester's round 3 / 2.0.0-beta.3): Part B is 25 cases. 24
declare expect="pass" — fixed findings held as regression guards (including E7-E11 and E13-E14,
the F16/F19/F20/F21 stress-round cases), plus the pre-existing structural guards E5/E6. The 25th,
E12 (F22, found this round), declares expect="fail": open when the tester shipped it, it flips to
expect="pass" the moment the fix lands, turning it into a guard like the rest. F17 and F18 are not
covered by a case, deliberately: F18 dumps 1,000+ files, and F17's fix is confirmed but left
unautomated because it is upstream of the Editor and a length check is cheap to eyeball. F12 is
fixed (the scene save was our code, not Unity's, and hit every testMode) and is guarded in the
plugin EditMode suite instead. See README.md.

Usage:
    python3 hades_suite.py                 # everything (needs a live Unity Editor for D/E cases)
    python3 hades_suite.py --no-editor     # protocol + graph cases only
    python3 hades_suite.py --list          # describe cases without running them
    python3 hades_suite.py --url http://127.0.0.1:7900/mcp   # point at a different port
                                            # (or set HADES_URL instead of passing --url)

No dependencies beyond the standard library.
"""
import argparse
import json
import os
import shutil
import sys
import time
import urllib.request

URL = os.environ.get("HADES_URL", "http://127.0.0.1:7823/mcp")
FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "fixtures", "editor-routed.json")

# Project-specific anchors, re-anchored to Hades-Unity-Client. Point these at equivalents when
# running on another project; each must be an asset something demonstrably references, or the
# assertions are meaningless. Evidence for each is a grep of the referencing file, recorded
# below so it can be re-verified without re-deriving it.

UNITY_ROOT = "/Users/mike/Projects/Hades-Unity-Client"

# Assets/Demo/Prefabs/Enemy_Boss.prefab:71 (identically Enemy_Fast.prefab:71 and
# Enemy_Tank.prefab:71):
#   m_SourcePrefab: {fileID: 100100000, guid: b2461a69f8268425f9005f9b0c5a0829, type: 3}
# matches Enemy.prefab's own guid (Assets/Demo/Prefabs/Enemy.prefab.meta) — three prefab
# variants demonstrably reference this prefab as their source. Re-verified for round 2, 2026-08-15
# (byte-identical to the original round-1 evidence).
REFERENCED_PREFAB = "Assets/Demo/Prefabs/Enemy.prefab"

# Assets/Readme.asset:15:
#   icon: {fileID: 2800000, guid: 727a75301c3d24613a3ebcec4a24c2c8, type: 3}
# matches Assets/TutorialInfo/Icons/URP.png's own guid (URP.png.meta). This is the only
# texture anywhere in this project, and this Readme icon field is its only reference.
# Re-verified for round 2, 2026-08-15 (byte-identical to the original round-1 evidence).
REFERENCED_TEXTURE = "Assets/TutorialInfo/Icons/URP.png"

# No material in this project references a texture: Assets/Demo/Materials/M_Enemy.mat's
# _BaseMap and _MainTex are both {fileID: 0} (verified) — the project has exactly one texture
# total (REFERENCED_TEXTURE above), used only as a Readme icon, never by a material.
#
# Round 1 fell back to Assets/Settings/PC_Renderer.asset (its ScreenSpaceAmbientOcclusion
# feature references seven blue-noise textures + a shader by GUID). Re-verifying that choice
# live for round 2 (2026-08-15) showed it does not actually exercise F6: trace_dependencies
# returns totalReturned=0 for it, but NOT because the dependency is unresolved in the F6
# sense — the response's own `dangling` array lists all of them (danglingCount=21, truncated),
# each with the same danglingNote: their target GUIDs are package-bundled and live outside
# every root Hades scans (Library/PackageCache, built-in shaders), so they can never resolve
# to a graph node regardless of F6's fix status. That's a different, legitimate "outside the
# scanned roots" case, not the F6 gap (in-project textures/shaders absent from the graph) —
# using it here would make this case fail forever even after F6 is completely fixed.
#
# Re-anchored to Assets/Readme.asset instead: it has exactly two GUID references (m_Script ->
# Assets/TutorialInfo/Scripts/Readme.cs, icon -> Assets/TutorialInfo/Icons/URP.png, both
# Assets/Readme.asset:12,15) and both are project-local, in-scan-root assets. Verified live
# against the running core, 2026-08-15: trace_dependencies("Assets/Readme.asset") returns
# totalReturned=2, dangling=[] — a real, fully-resolved forward walk, the exact complement of
# REFERENCED_TEXTURE/G2's reverse lookup on the same Readme.asset -> URP.png edge (the only
# real texture reference this project has, walked in the other direction). Not literally a
# material — same naming compromise round 1 already made with the renderer-asset substitute,
# kept for minimal diff against the rest of this file.
MAT_WITH_TEXTURES = "Assets/Readme.asset"

TMP = "Assets/HadesRegressionTmp"


# --------------------------------------------------------------------------- transport

def rpc(method, params, timeout=180):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request(URL, data=body, headers={
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream"})
    try:
        raw = urllib.request.urlopen(req, timeout=timeout).read().decode()
    except Exception as e:
        return {"_err": f"transport: {e}"}
    out = None
    for line in raw.splitlines():
        if line.startswith("data: "):
            out = json.loads(line[6:])
    if out is None:
        return {"_err": "no SSE data frame"}
    if "error" in out:
        return {"_err": str(out["error"])[:300]}
    return out.get("result", {})


def call(tool, args=None, timeout=180):
    """Call an MCP tool. Returns parsed JSON, or {"_err": ...} for tool/transport errors."""
    r = rpc("tools/call", {"name": tool, "arguments": args or {}}, timeout)
    if "_err" in r:
        return r
    txt = " ".join(c.get("text", "") for c in r.get("content", []))
    if r.get("isError"):
        return {"_err": txt[:400]}
    try:
        return json.loads(txt)
    except Exception:
        return {"_text": txt}


def not_in_graph(res):
    return "_err" in res and "not in the graph" in res["_err"]


# --------------------------------------------------------------------------- assertions
# Each returns (ok: bool, detail: str). ok=True means "behaved correctly".

def c_f1_no_boolean_schemas():
    """F1 (fixed in 2.0.0-beta.2): no tool may declare a boolean-form subschema at the top level
    of `properties`. At 0.1.0 one such field (`inspect_asset.outputSchema.properties.value`) made
    Claude Code reject the entire 32-tool list — the single highest-impact assertion here."""
    r = rpc("tools/list", {})
    if "_err" in r:
        return False, r["_err"]
    offenders = []
    for t in r.get("tools", []):
        for field in ("inputSchema", "outputSchema"):
            props = (t.get(field) or {}).get("properties") or {}
            for key, val in props.items():
                if isinstance(val, bool):
                    offenders.append(f"{t['name']}.{field}.properties.{key}")
    if offenders:
        return False, f"{len(offenders)} top-level boolean subschema(s): {offenders}"
    return True, "no top-level boolean subschemas"


def c_f1_nested_boolean_schemas():
    """F1 (fixed): the same boolean form nested under `items`. 16 existed at 0.1.0; 15 of them
    were inert against Claude Code 2.1.220, which does not descend there, but a stricter validator
    would have rejected those too. All 16 are gone as of 2.0.0-beta.2."""
    r = rpc("tools/list", {})
    if "_err" in r:
        return False, r["_err"]
    found = []

    def walk(node, path):
        if isinstance(node, dict):
            for k, v in node.items():
                if isinstance(v, bool) and (k == "items" or k == "additionalProperties"
                                            or path.endswith(".properties")):
                    found.append(f"{path}.{k}")
                else:
                    walk(v, f"{path}.{k}")
        elif isinstance(node, list):
            for i, v in enumerate(node):
                walk(v, f"{path}[{i}]")

    for t in r.get("tools", []):
        for field in ("inputSchema", "outputSchema"):
            if t.get(field):
                walk(t[field], f"{t['name']}.{field}")
    if found:
        return False, f"{len(found)} boolean subschema(s) total, e.g. {found[:3]}"
    return True, "none"


def c_f5_server_version():
    """F5 (fixed): the initialize handshake should advertise the product version. At 0.1.0 it
    reported the assembly version `1.0.0.0` — which was the only version string readable while F1
    blocked the tool list, and it looked like a v1 server."""
    r = rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                           "clientInfo": {"name": "hades-suite", "version": "1"}})
    v = (r.get("serverInfo") or {}).get("version")
    if v and v != "1.0.0.0":
        return True, f"serverInfo.version={v}"
    return False, f"serverInfo.version={v!r} (expected the product version, e.g. 2.0.0)"


def c_f6_trace_material():
    """F6: an asset with real, resolved dependencies must not report zero dependencies.
    MAT_WITH_TEXTURES here is Assets/Readme.asset (no material in this project references a
    texture, and the project's only other real GUID-referencing asset resolves solely to
    package-bundled, out-of-scan-root GUIDs that would dangle regardless of F6 — see the anchor
    comment above); Readme.asset's icon field resolves to this project's one real texture."""
    r = call("trace_dependencies", {"assetPath": MAT_WITH_TEXTURES, "maxDepth": 2, "limit": 10})
    if "_err" in r:
        return False, r["_err"]
    n = r.get("totalReturned", 0)
    if n and n > 0:
        return True, f"{n} dependency(ies)"
    return False, "0 dependencies, despite Readme.asset's icon field resolving to a real project texture"


def c_f6_refs_to_texture():
    """F6 (fixed): reverse lookup on a texture that something in the project references. At 0.1.0
    textures were not graph nodes at all, so this errored with "not in the graph"."""
    r = call("find_references_to", {"assetPath": REFERENCED_TEXTURE, "limit": 5})
    if not_in_graph(r):
        return False, "'not in the graph' for a texture this project demonstrably references"
    if "_err" in r:
        return False, r["_err"]
    return True, f"{r.get('totalReferences')} reference(s)"


def c_f6_search_texture():
    """F6: search_by_name must be able to find a texture asset that exists on disk."""
    name = os.path.basename(REFERENCED_TEXTURE).rsplit(".", 1)[0]
    r = call("search_by_name", {"namePattern": name, "limit": 5})
    if "_err" in r:
        return False, r["_err"]
    hits = [h for h in r.get("results", []) if h.get("path") == REFERENCED_TEXTURE]
    return (True, "found") if hits else (False, f"0 hits for {name!r}")


def c_f6_control_prefab_refs():
    """Control for F6: the same reverse lookup on an indexed kind must work. If this fails the
    failures above are not kind-specific and the diagnosis in FINDINGS.md is wrong."""
    r = call("find_references_to", {"assetPath": REFERENCED_PREFAB, "limit": 5})
    if "_err" in r:
        return False, r["_err"]
    return True, f"{r.get('totalReferences')} reference(s)"


def c_f7_unknown_kind_errors():
    """F7: an unrecognised `kind` must be rejected, not answered with an empty result set that
    is indistinguishable from a genuinely empty project area."""
    r = call("graph_query", {"kind": "Bananas", "limit": 3})
    if "_err" in r:
        return True, "rejected"
    return False, f"returned {r.get('totalReturned')} results instead of an error"


def c_f7_scene_kind():
    """F7, concretely: 'Scene' returning 0 despite scenes existing in the project. Either the
    kind should be accepted or it should error; silently returning 0 is the defect."""
    r = call("graph_query", {"kind": "Scene", "limit": 3})
    if "_err" in r:
        return True, "rejected (acceptable)"
    if r.get("totalReturned"):
        return True, f"{r.get('totalReturned')} results"
    return False, "0 results and no error, on a project with real scenes"


def c_f13_unknown_param_rejected():
    """F13: unknown parameters must be rejected. They are silently dropped, so following the
    server's own advice to use `type_filter` (a retired v1.2 name) returns unfiltered results
    that look filtered."""
    r = call("search_by_name", {"namePattern": "Enemy", "zzBogusParam": "x", "limit": 1})
    if "_err" in r:
        return True, "rejected"
    return False, "unknown parameter silently ignored"


def c_f9_unity_version_persisted():
    """F9: with an Editor attached, the app's stored project record should know its Unity
    version. hades_charon_status reports it correctly, so the value exists and is not persisted."""
    st = call("hades_status")
    if "_err" in st:
        return False, st["_err"]
    ch = call("hades_charon_status")
    if not ch.get("attached"):
        return False, "SKIP-CONDITION: no Editor attached"
    guid = st.get("defaultProject")
    # HADES_HOME relocates the app's state dir (app and plugin both honor it); same here, or
    # this case reads the wrong home whenever the suite targets an isolated instance.
    home = os.environ.get("HADES_HOME") or os.path.expanduser("~/Library/Application Support/Hades")
    p = os.path.join(home, "projects", guid, "project.json")
    if not os.path.exists(p):
        return False, f"missing {p}"
    rec = json.load(open(p))
    if rec.get("UnityVersion"):
        return True, f"UnityVersion={rec['UnityVersion']}"
    return False, (f"UnityVersion=null while charon reports {ch.get('unityVersion')}; "
                   f"LastSeen={rec.get('LastSeen')} FirstSeen={rec.get('FirstSeen')}")


# ---- Editor-dependent -----------------------------------------------------------------

def _clean_tmp():
    """Remove the scratch folder. Done on the filesystem because asset_manage has no delete op
    — which is itself one of the gaps reported in FINDINGS.md."""
    d = os.path.join(UNITY_ROOT, TMP)
    for p in (d, d + ".meta"):
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
        elif os.path.exists(p):
            os.remove(p)
    call("asset_manage", {"operations": [{"op": "refresh"}]})


def c_f14_new_asset_indexed():
    """F14: an asset created through Hades' own mutation tools must be visible to Hades' own
    query tools without a manual rebuild. The script path already does this on recompile."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "RegTmpSrc"}]})
    mk = call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "RegTmpSrc", "prefabPath": f"{TMP}/New.prefab"}]})
    if mk.get("failed"):
        call("scene_apply", {"operations": [{"op": "delete", "target": "RegTmpSrc"}]})
        return False, f"SETUP FAILED: {json.dumps(mk)[:150]}"
    time.sleep(3)
    r = call("find_references_to", {"assetPath": f"{TMP}/New.prefab"})
    call("scene_apply", {"operations": [{"op": "delete", "target": "RegTmpSrc"}]})
    if not_in_graph(r):
        return False, "freshly created prefab is 'not in the graph'"
    if "_err" in r:
        return False, r["_err"]
    return True, "visible without a rebuild"


def c_f14_move_not_stale():
    """F14: after a move, the graph must not answer about the old path and deny the new one.
    Deliberately does NOT call hades_rebuild_graph between create and move — that intervening
    rebuild is exactly what E12/F22 isolates as its own, separate bug (a one-variable
    differential against this case). Adding it back here would make this case fail for F22's
    reason instead of testing what it says it tests."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "RegTmpSrc2"}]})
    call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "RegTmpSrc2", "prefabPath": f"{TMP}/Before.prefab"}]})
    time.sleep(3)                                     # incremental indexing, no rebuild (see E12)
    call("asset_manage", {"operations": [
        {"op": "move", "sourcePath": f"{TMP}/Before.prefab",
         "destPath": f"{TMP}/After.prefab"}]})
    time.sleep(3)
    old = call("find_references_to", {"assetPath": f"{TMP}/Before.prefab"})
    new = call("find_references_to", {"assetPath": f"{TMP}/After.prefab"})
    call("scene_apply", {"operations": [{"op": "delete", "target": "RegTmpSrc2"}]})
    old_gone, new_ok = not_in_graph(old), not not_in_graph(new)
    if old_gone and new_ok:
        return True, "old path rejected, new path resolves"
    return False, (f"old path {'rejected' if old_gone else 'STILL ANSWERED'}; "
                   f"new path {'resolves' if new_ok else 'NOT IN GRAPH'}")


def c_f10_tag_visible_after_create():
    """F10: a tag must be visible to the tool surface's own reader immediately after creation.
    The write lands in the live Editor; project_settings reads from disk and cannot see it until
    an unrelated save flushes ProjectSettings."""
    tag = "RegSuiteVisibilityTag"
    call("project_settings_apply", {"operations": [{"op": "deleteTag", "name": tag}]})
    mk = call("project_settings_apply", {"operations": [{"op": "createTag", "name": tag}]})
    if mk.get("failed"):
        return False, f"SETUP FAILED: {json.dumps(mk)[:150]}"
    r = call("project_settings", {"section": "tags"})
    visible = tag in (r.get("tags") or [])
    call("project_settings_apply", {"operations": [{"op": "deleteTag", "name": tag}]})
    call("scene_manage", {"operations": [{"op": "save"}]})   # flush ProjectSettings back
    if visible:
        return True, "visible immediately"
    return False, "created tag absent from project_settings(tags) until an unrelated save"


def c_f8_nested_prefab():
    """F8 GUARD. The archive documents prefab_apply create as producing a flattened,
    disconnected prefab; it was fixed (SaveAsPrefabAssetAndConnect) but the docs still say
    otherwise. Pinned so the old behaviour cannot come back unnoticed."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "RegLeafSrc"}]})
    call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "RegLeafSrc", "prefabPath": f"{TMP}/Leaf.prefab"}]})
    call("scene_apply", {"operations": [
        {"op": "create", "name": "RegOuterSrc"},
        {"op": "reparent", "target": "RegLeafSrc", "newParent": "RegOuterSrc"}]})
    call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "RegOuterSrc", "prefabPath": f"{TMP}/Outer.prefab"}]})
    outer = os.path.join(UNITY_ROOT, TMP, "Outer.prefab")
    detail = "Outer.prefab was not written"
    ok = False
    if os.path.exists(outer):
        body = open(outer).read()
        ok = "!u!1001" in body and "stripped" in body
        detail = (f"PrefabInstance={'!u!1001' in body} stripped={'stripped' in body}")
    call("scene_apply", {"operations": [{"op": "delete", "target": "RegOuterSrc"}]})
    return ok, detail


def c_move_guid_stable():
    """GUARD: asset_manage move must carry the .meta with the file and keep the GUID stable.
    A move that changed GUIDs would silently break every reference in the project, and the op
    documents itself as having no undo."""
    _clean_tmp()
    call("material_apply", {"operations": [{"op": "create", "path": f"{TMP}/M.mat"}]})
    meta = os.path.join(UNITY_ROOT, TMP, "M.mat.meta")
    if not os.path.exists(meta):
        return False, "SETUP FAILED: material .meta not written"
    before = [l for l in open(meta) if l.startswith("guid:")][0].strip()
    call("asset_manage", {"operations": [
        {"op": "move", "sourcePath": f"{TMP}/M.mat", "destPath": f"{TMP}/M2.mat"}]})
    meta2 = os.path.join(UNITY_ROOT, TMP, "M2.mat.meta")
    if not os.path.exists(meta2):
        return False, "destination .meta missing after move"
    after = [l for l in open(meta2) if l.startswith("guid:")][0].strip()
    stale = os.path.exists(meta)
    if before == after and not stale:
        return True, f"{before} preserved, stale .meta removed"
    return False, f"{before} -> {after}, stale .meta present={stale}"


def c_material_setproperty_on_disk():
    """GUARD: a mutation's success message is not evidence. Assert the value in the YAML."""
    _clean_tmp()
    call("material_apply", {"operations": [
        {"op": "create", "path": f"{TMP}/P.mat"},
        {"op": "setProperty", "path": f"{TMP}/P.mat",
         "propertyName": "_Metallic", "value": 0.75}]})
    p = os.path.join(UNITY_ROOT, TMP, "P.mat")
    if not os.path.exists(p):
        return False, "material not written"
    body = open(p).read()
    return ("_Metallic: 0.75" in body,
            "_Metallic: 0.75 present" if "_Metallic: 0.75" in body
            else "value absent from YAML despite reported success")


def c_f16_traversal_refused():
    """F16: a `..` path must not be able to write outside Assets/. Cleans up after itself, and
    reports the escape as a failure rather than leaving it on disk."""
    _clean_tmp()
    escape_rel = "Assets/../HadesSuiteEscape.mat"
    target = os.path.join(os.path.dirname(UNITY_ROOT.rstrip("/")),
                          os.path.basename(UNITY_ROOT.rstrip("/")), "HadesSuiteEscape.mat")
    r = call("material_apply", {"operations": [{"op": "create", "path": escape_rel}]})
    landed = [p for p in (target, target + ".meta") if os.path.exists(p)]
    for p in landed:
        os.remove(p)
    refused = ("_err" in r) or bool(r.get("failed"))
    if refused and not landed:
        return True, "traversal refused, nothing written outside Assets/"
    return False, (f"accepted={not refused}; "
                   f"{len(landed)} file(s) landed outside Assets/ (removed by this test)")


def c_f16_no_clobber():
    """F16: `create` must not silently overwrite an existing file of a different type. Uses a
    prefab this test created as the victim — never a real project asset."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "SuiteClobberSrc"}]})
    mk = call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "SuiteClobberSrc",
         "prefabPath": f"{TMP}/Victim.prefab"}]})
    victim = os.path.join(UNITY_ROOT, TMP, "Victim.prefab")
    if mk.get("failed") or not os.path.exists(victim):
        call("scene_apply", {"operations": [{"op": "delete", "target": "SuiteClobberSrc"}]})
        return False, "SETUP FAILED: victim prefab not created"
    r = call("material_apply", {"operations": [
        {"op": "create", "path": f"{TMP}/Victim.prefab"}]})
    body = open(victim, encoding="utf-8", errors="replace").read(400)
    clobbered = "Material:" in body and "GameObject" not in body
    call("scene_apply", {"operations": [{"op": "delete", "target": "SuiteClobberSrc"}]})
    refused = ("_err" in r) or bool(r.get("failed"))
    if refused and not clobbered:
        return True, "refused; the prefab was left intact"
    return False, f"accepted={not refused}; prefab overwritten with material YAML={clobbered}"


def c_f19_trace_nested_prefab():
    """F19: trace_dependencies must walk prefab->prefab nesting. find_references_to answers the
    same edge in reverse, so a 0 here is a contradiction, not an empty project."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "SuiteInnerSrc"}]})
    call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "SuiteInnerSrc",
         "prefabPath": f"{TMP}/Inner.prefab"}]})
    call("prefab_apply", {"operations": [{"op": "instantiate",
                                         "prefabPath": f"{TMP}/Inner.prefab"}]})
    call("scene_apply", {"operations": [
        {"op": "create", "name": "SuiteOuterSrc"},
        {"op": "reparent", "target": "SuiteInnerSrc", "newParent": "SuiteOuterSrc"}]})
    call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "SuiteOuterSrc",
         "prefabPath": f"{TMP}/Outer2.prefab"}]})
    time.sleep(2)
    rev = call("find_references_to", {"assetPath": f"{TMP}/Inner.prefab", "limit": 5})
    fwd = call("trace_dependencies", {"assetPath": f"{TMP}/Outer2.prefab",
                                      "maxDepth": 3, "limit": 20})
    call("scene_apply", {"operations": [{"op": "delete", "target": "SuiteOuterSrc"}]})
    rev_n = rev.get("totalReferences") if "_err" not in rev else 0
    fwd_n = fwd.get("totalReturned") if "_err" not in fwd else -1
    if not rev_n:
        return False, "SETUP FAILED: nesting did not register in reverse either"
    if fwd_n and fwd_n > 0:
        return True, f"forward={fwd_n} dep(s), reverse={rev_n} ref(s)"
    return False, (f"trace_dependencies={fwd_n} while find_references_to reports {rev_n} "
                   f"ref(s) on the same edge")


def c_f20_create_twice_signalled():
    """F20: repeating a create must refuse, or distinguish created from replaced. animation_apply
    already refuses with a pointer to the edit tool; the others report identical success."""
    _clean_tmp()
    a = call("material_apply", {"operations": [{"op": "create", "path": f"{TMP}/Twice.mat"}]})
    b = call("material_apply", {"operations": [{"op": "create", "path": f"{TMP}/Twice.mat"}]})
    if "_err" in a or a.get("failed"):
        return False, "SETUP FAILED: first create did not succeed"
    refused = ("_err" in b) or bool(b.get("failed"))
    if refused:
        return True, "second create refused"
    blob = json.dumps(b).lower()
    if "replaced" in blob or "overwrit" in blob or "existed" in blob:
        return True, "second create reported as a replacement"
    return False, "second create returned an identical success — created and replaced are indistinguishable"


def c_f21_self_reparent_refused():
    """F21: reparenting a GameObject under itself must be refused."""
    call("scene_apply", {"operations": [{"op": "create", "name": "SuiteSelfP"}]})
    r = call("scene_apply", {"operations": [
        {"op": "reparent", "target": "SuiteSelfP", "newParent": "SuiteSelfP"}]})
    call("scene_apply", {"operations": [{"op": "delete", "target": "SuiteSelfP"}]})
    refused = ("_err" in r) or bool(r.get("failed"))
    return (refused, "refused" if refused else "accepted a self-parent")


def c_f22_move_after_rebuild():
    """F22: after a full hades_rebuild_graph, a subsequent move must still retire the old path.
    Without an intervening rebuild the move is handled correctly (that is E2); with one, both the
    old and the new path keep answering, so the graph holds two paths for one asset. Verified with
    a one-variable differential and stable at t+40s. Open as of this merge (2026-08-15,
    2.0.0-beta.3) — expect="fail" until the fix lands, then flips to "pass" to guard it like E2."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "F22Src"}]})
    mk = call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "F22Src", "prefabPath": f"{TMP}/Before.prefab"}]})
    if mk.get("failed"):
        call("scene_apply", {"operations": [{"op": "delete", "target": "F22Src"}]})
        return False, f"SETUP FAILED: {json.dumps(mk)[:140]}"
    time.sleep(2)
    call("hades_rebuild_graph", timeout=600)
    call("asset_manage", {"operations": [
        {"op": "move", "sourcePath": f"{TMP}/Before.prefab",
         "destPath": f"{TMP}/After.prefab"}]})
    time.sleep(12)
    old = call("find_references_to", {"assetPath": f"{TMP}/Before.prefab"})
    new = call("find_references_to", {"assetPath": f"{TMP}/After.prefab"})
    call("scene_apply", {"operations": [{"op": "delete", "target": "F22Src"}]})
    old_gone, new_ok = not_in_graph(old), not not_in_graph(new)
    if old_gone and new_ok:
        return True, "old path retired after a rebuild+move"
    return False, (f"old path {'retired' if old_gone else 'STILL ANSWERS'}, "
                   f"new path {'resolves' if new_ok else 'not in graph'} "
                   f"— the graph holds both paths for one asset")


def c_f21_scene_duplicate_self_refused():
    """F21-sibling: scene_manage duplicate with sourcePath==destPath must refuse. Unlike
    createVariant's own explicit basePrefabPath==variantPath check (see the createVariant sibling
    case below), duplicate has no dedicated self-check of its own - it is refused as a side effect
    of destPath's own already-exists guard (AssetPathGuard.RequireNewAssetPath): a self-duplicate's
    destination trivially already exists, being the source itself. Guards the scene file is left
    untouched on disk, not just that the call errors."""
    _clean_tmp()
    path = f"{TMP}/SelfDup.unity"
    mk = call("scene_manage", {"operations": [{"op": "create", "path": path}]})
    on_disk = os.path.join(UNITY_ROOT, path)
    if mk.get("failed") or not os.path.exists(on_disk):
        return False, f"SETUP FAILED: scene not written: {json.dumps(mk)[:150]}"
    before = open(on_disk, "rb").read()
    r = call("scene_manage", {"operations": [
        {"op": "duplicate", "sourcePath": path, "destPath": path}]})
    after = open(on_disk, "rb").read() if os.path.exists(on_disk) else b""
    refused = ("_err" in r) or bool(r.get("failed"))
    unchanged = before == after
    blob = json.dumps(r).lower()
    mentions = "selfdup.unity" in blob or "already exist" in blob
    if refused and unchanged and mentions:
        return True, "refused (already-exists guard), scene left untouched"
    return False, f"accepted={not refused}; unchanged={unchanged}; error mentions path/reason={mentions}"


def c_f21_prefab_createvariant_self_refused():
    """F21: prefab_apply createVariant with basePrefabPath==variantPath must refuse -
    PrefabCommands.DoCreateVariant's own dedicated check (its doc comment: accepting base ==
    variant "destroyed the target during the tester's own repro", the variant save silently
    replacing the very base prefab it was meant to be based on). Guards the base prefab file is
    left byte-identical, not just that the call errors."""
    _clean_tmp()
    base_path = f"{TMP}/SelfVariantBase.prefab"
    call("scene_apply", {"operations": [{"op": "create", "name": "SuiteSelfVariantSrc"}]})
    mk = call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "SuiteSelfVariantSrc", "prefabPath": base_path}]})
    call("scene_apply", {"operations": [{"op": "delete", "target": "SuiteSelfVariantSrc"}]})
    on_disk = os.path.join(UNITY_ROOT, base_path)
    if mk.get("failed") or not os.path.exists(on_disk):
        return False, f"SETUP FAILED: base prefab not created: {json.dumps(mk)[:150]}"
    before = open(on_disk, "rb").read()
    r = call("prefab_apply", {"operations": [
        {"op": "createVariant", "basePrefabPath": base_path, "variantPath": base_path}]})
    after = open(on_disk, "rb").read() if os.path.exists(on_disk) else b""
    refused = ("_err" in r) or bool(r.get("failed"))
    unchanged = before == after
    blob = json.dumps(r).lower()
    mentions = "differ" in blob or "selfvariantbase.prefab" in blob
    if refused and unchanged and mentions:
        return True, "refused (must-differ guard), base prefab left byte-identical"
    return False, f"accepted={not refused}; unchanged={unchanged}; error mentions reason={mentions}"


# --------------------------------------------------------------------------- registry

CASES = [
    # id, finding, needs_editor, expect, fn, one-line description
    ("P1", "F1",  False, "pass", c_f1_no_boolean_schemas,
     "no top-level boolean subschema in any tool schema (blocks the whole tool list)"),
    ("P2", "F1",  False, "pass", c_f1_nested_boolean_schemas,
     "no boolean subschemas anywhere (latent against a stricter validator)"),
    ("P3", "F5",  False, "pass", c_f5_server_version,
     "initialize advertises the product version, not the assembly version"),
    ("G1", "F6",  False, "pass", c_f6_trace_material,
     "trace_dependencies resolves a real asset's dependencies (script + project texture)"),
    ("G2", "F6",  False, "pass", c_f6_refs_to_texture,
     "find_references_to answers for a referenced texture"),
    ("G3", "F6",  False, "pass", c_f6_search_texture,
     "search_by_name finds a texture asset"),
    ("G4", "F6",  False, "pass", c_f6_control_prefab_refs,
     "CONTROL: find_references_to works on an indexed kind (prefab)"),
    ("G5", "F7",  False, "pass", c_f7_unknown_kind_errors,
     "graph_query rejects an unrecognised kind instead of returning empty"),
    ("G6", "F7",  False, "pass", c_f7_scene_kind,
     "graph_query(kind=Scene) does not silently return 0 despite scenes existing"),
    ("G7", "F13", False, "pass", c_f13_unknown_param_rejected,
     "unknown parameters are rejected, not silently dropped"),
    ("A1", "F9",  True,  "pass", c_f9_unity_version_persisted,
     "project.json records UnityVersion while an Editor is attached"),
    ("E1", "F14", True,  "pass", c_f14_new_asset_indexed,
     "a newly created asset is queryable without a manual rebuild"),
    ("E2", "F14", True,  "pass", c_f14_move_not_stale,
     "after a move the graph rejects the old path and resolves the new one"),
    ("E3", "F10", True,  "pass", c_f10_tag_visible_after_create,
     "a created tag is visible to project_settings immediately"),
    ("E4", "F8",  True,  "pass", c_f8_nested_prefab,
     "GUARD: prefab_apply create produces a nested PrefabInstance, not a flattened copy"),
    ("E5", "-",   True,  "pass", c_move_guid_stable,
     "GUARD: asset_manage move preserves GUID and removes the stale .meta"),
    ("E6", "-",   True,  "pass", c_material_setproperty_on_disk,
     "GUARD: material_apply setProperty is present in the saved YAML"),
    ("E7", "F16", True,  "pass", c_f16_traversal_refused,
     "a '..' path cannot write outside Assets/ (test removes any escape it finds)"),
    ("E8", "F16", True,  "pass", c_f16_no_clobber,
     "create does not silently overwrite an existing file of another type"),
    ("E9", "F19", True,  "pass", c_f19_trace_nested_prefab,
     "trace_dependencies walks prefab->prefab nesting that find_references_to sees in reverse"),
    ("E10", "F20", True, "pass", c_f20_create_twice_signalled,
     "a repeated create refuses, or distinguishes created from replaced"),
    ("E11", "F21", True, "pass", c_f21_self_reparent_refused,
     "reparenting a GameObject under itself is refused"),
    # E12 is an open finding as of this merge (2026-08-15): expect="fail" until the fix lands,
    # then flip it to "pass" — it becomes a regression guard like every other case here.
    ("E12", "F22", True, "fail", c_f22_move_after_rebuild,
     "after a rebuild, a move still retires the old path (without a rebuild it does — see E2)"),
    ("E13", "F21", True, "pass", c_f21_scene_duplicate_self_refused,
     "scene_manage duplicate with sourcePath==destPath is refused, not silently self-overwritten"),
    ("E14", "F21", True, "pass", c_f21_prefab_createvariant_self_refused,
     "prefab_apply createVariant with basePrefabPath==variantPath is refused, base left byte-identical"),
]


def part_a():
    print("=" * 100)
    print("PART A — replay of Editor-routed calls through the app's own hades_regression tool")
    print("=" * 100)
    if not os.path.exists(FIXTURE):
        print(f"  fixture missing: {FIXTURE}")
        return 0, 0
    calls = json.load(open(FIXTURE))["calls"]
    print(f"  replaying {len(calls)} recorded call(s) from {os.path.basename(FIXTURE)}")
    r = call("hades_regression", {"action": "replay", "calls": calls}, timeout=600)
    if "_err" in r:
        print(f"  REPLAY ERROR: {r['_err']}")
        return 0, len(calls)
    for res in r.get("results") or []:
        flag = "ok  " if res.get("passed") else "FAIL"
        err = f"  {str(res.get('error'))[:110]}" if res.get("error") else ""
        print(f"    [{flag}] {res.get('method')}{err}")
    p, f = r.get("passed") or 0, r.get("failed") or 0
    print(f"  replay: {p} passed, {f} failed of {r.get('total')}")
    return p, f


def main():
    global URL
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default=URL,
                     help=f"Hades MCP endpoint (env: HADES_URL; default: {URL})")
    ap.add_argument("--no-editor", action="store_true", help="skip Editor-dependent cases")
    ap.add_argument("--list", action="store_true", help="describe cases and exit")
    ap.add_argument("--skip-part-a", action="store_true")
    args = ap.parse_args()

    URL = args.url

    if args.list:
        print(f"{'id':4} {'finding':8} {'editor':7} {'expect':7} description")
        for cid, fid, ed, exp, _, desc in CASES:
            print(f"{cid:4} {fid:8} {str(ed):7} {exp:7} {desc}")
        return 0

    attached = False
    ch = call("hades_charon_status")
    if "_err" not in ch:
        attached = bool(ch.get("attached"))
    print(f"Editor attached: {attached}"
          f"{' (' + str(ch.get('unityVersion')) + ')' if attached else ''}")

    if not args.skip_part_a and attached and not args.no_editor:
        part_a()
    else:
        print("\nPART A skipped (needs a live Editor)")

    print()
    print("=" * 100)
    print("PART B — direct assertions (the graph/read surface hades_regression cannot record)")
    print("=" * 100)
    deviations = []
    for cid, fid, needs_ed, expect, fn, desc in CASES:
        if needs_ed and (args.no_editor or not attached):
            print(f"  [SKIP] {cid} {fid:5} {desc}")
            continue
        try:
            ok, detail = fn()
        except Exception as e:
            ok, detail = False, f"case raised: {type(e).__name__}: {e}"
        actual = "pass" if ok else "fail"
        if actual == expect:
            tag = "as-expected" if expect == "pass" else "still-broken"
        else:
            tag = "*** NEWLY BROKEN ***" if expect == "pass" else "*** NOW FIXED ***"
            deviations.append((cid, fid, expect, actual, detail))
        print(f"  [{actual:4}] {cid} {fid:5} {tag:22} {desc}")
        print(f"         -> {detail}")

    print()
    print("=" * 100)
    if deviations:
        print(f"{len(deviations)} DEVIATION(S) from expected state:")
        for cid, fid, exp, act, detail in deviations:
            print(f"  {cid} ({fid}): expected {exp}, got {act} — {detail}")
        print("\nIf a case flipped to NOW FIXED, change its expect to \"pass\" to guard it.")
    else:
        n_open = sum(1 for c in CASES if c[3] == "fail")
        print(f"No deviations. {len(CASES) - n_open} case(s) behaving correctly"
              + (f", {n_open} open finding(s) still reproducing." if n_open
                 else " — nothing has regressed."))
    _clean_tmp()
    return 1 if deviations else 0


if __name__ == "__main__":
    sys.exit(main())
