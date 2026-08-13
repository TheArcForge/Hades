#!/usr/bin/env python3
"""Hades regression suite — executable form of FINDINGS.md.

Adopted from an internal tester's findings bundle (2026-08-12) into this repo. Anchors below
are re-pointed at Hades-Unity-Client; everything else — cases, expectations, fixture — is
unchanged. See README.md in this directory for how to run it and how to read the output.

Two parts, because hades_regression cannot see most of the defects:

  PART A  replayed through the app's own `hades_regression` tool from a recorded fixture.
          Covers the Editor-routed mutation surface only — that is all the recorder captures
          (measured: of 6 mixed calls, only the 2 Editor-routed ones were recorded).

  PART B  run directly over MCP by this script, because the graph/read surface is invisible to
          the recorder and therefore cannot be expressed as a fixture at all.

Every case declares what it should do TODAY:

  expect="fail"  an open finding. Failing = the bug is still there. PASSING IS NEWS: the bug is
                 fixed and the case should be flipped to expect="pass" to guard it.
  expect="pass"  behaviour that is currently correct, asserted so a future change cannot break it
                 silently. F8 is here because it is a fixed bug worth pinning down.

Exit code is 0 when every case matched its expectation, 1 otherwise — so "all findings still
present, nothing regressed" is a green run. Read the table, not just the code.

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
# variants demonstrably reference this prefab as their source.
REFERENCED_PREFAB = "Assets/Demo/Prefabs/Enemy.prefab"

# Assets/Readme.asset:15:
#   icon: {fileID: 2800000, guid: 727a75301c3d24613a3ebcec4a24c2c8, type: 3}
# matches Assets/TutorialInfo/Icons/URP.png's own guid (URP.png.meta). This is the only
# texture anywhere in this project, and this Readme icon field is its only reference.
REFERENCED_TEXTURE = "Assets/TutorialInfo/Icons/URP.png"

# No material in this project references a texture: Assets/Demo/Materials/M_Enemy.mat's
# _BaseMap and _MainTex are both {fileID: 0} (verified) — the project has exactly one texture
# total (REFERENCED_TEXTURE above), used only as a Readme icon, never by a material. Falling
# back to the suite's other named option instead: the URP renderer asset.
# Assets/Settings/PC_Renderer.asset:87-95 references seven blue-noise textures
# (m_BlueNoise256Textures) and a shader (m_Shader) by GUID through its
# ScreenSpaceAmbientOcclusion renderer feature — real, resolvable references to unindexed
# asset kinds (Texture2D + Shader), the same shape of test as the original anchor. Those GUIDs
# are package-bundled (verified: none resolve to a file under this project's own Assets/),
# unlike the original project-local texture, because this project has no project-authored
# texture that any material or renderer asset actually uses.
MAT_WITH_TEXTURES = "Assets/Settings/PC_Renderer.asset"

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
    """F1: no tool may declare a boolean-form subschema at the top level of `properties`.
    One such field (`inspect_asset.outputSchema.properties.value`) makes Claude Code reject the
    entire 32-tool list, so this is the single highest-impact assertion in the suite."""
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
    """F1 (latent): the same boolean form nested under `items`. Inert against Claude Code
    2.1.220, which does not descend there, but a stricter validator would reject these too."""
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
    """F5: the initialize handshake should advertise the product version. It reports the
    assembly version instead, which is the only version string readable when F1 blocks the
    tool list — and it looks like a v1 server."""
    r = rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                           "clientInfo": {"name": "hades-suite", "version": "1"}})
    v = (r.get("serverInfo") or {}).get("version")
    if v and v != "1.0.0.0":
        return True, f"serverInfo.version={v}"
    return False, f"serverInfo.version={v!r} (expected the product version, e.g. 2.0.0-dev)"


def c_f6_trace_material():
    """F6: an asset with real, resolved texture/shader dependencies must not report zero
    dependencies. MAT_WITH_TEXTURES here is a URP renderer asset (no material in this project
    references a texture, see the anchor comment above) with seven texture bindings and a
    shader, all resolved by GUID."""
    r = call("trace_dependencies", {"assetPath": MAT_WITH_TEXTURES, "maxDepth": 2, "limit": 10})
    if "_err" in r:
        return False, r["_err"]
    n = r.get("totalReturned", 0)
    if n and n > 0:
        return True, f"{n} dependency(ies)"
    return False, "0 dependencies, while inspect_asset resolves its texture + shader bindings"


def c_f6_refs_to_texture():
    """F6: reverse lookup on a texture that something in the project references."""
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
    """F14: after a move, the graph must not answer about the old path and deny the new one."""
    _clean_tmp()
    call("scene_apply", {"operations": [{"op": "create", "name": "RegTmpSrc2"}]})
    call("prefab_apply", {"operations": [
        {"op": "create", "gameObjectPath": "RegTmpSrc2", "prefabPath": f"{TMP}/Before.prefab"}]})
    call("hades_rebuild_graph", timeout=600)          # ensure Before.prefab IS indexed
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


# --------------------------------------------------------------------------- registry

CASES = [
    # id, finding, needs_editor, expect, fn, one-line description
    ("P1", "F1",  False, "pass", c_f1_no_boolean_schemas,
     "no top-level boolean subschema in any tool schema (blocks the whole tool list)"),
    ("P2", "F1",  False, "pass", c_f1_nested_boolean_schemas,
     "no boolean subschemas anywhere (latent against a stricter validator)"),
    ("P3", "F5",  False, "pass", c_f5_server_version,
     "initialize advertises the product version, not the assembly version"),
    ("G1", "F6",  False, "fail", c_f6_trace_material,
     "trace_dependencies reports a material's texture/shader dependencies"),
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
        print("No deviations: every open finding still reproduces, nothing previously")
        print("correct has regressed.")
    _clean_tmp()
    return 1 if deviations else 0


if __name__ == "__main__":
    sys.exit(main())
