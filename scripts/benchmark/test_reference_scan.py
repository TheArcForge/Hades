#!/usr/bin/env python3
"""Tests for reference_scan.py.  Run with:  python3 test_reference_scan.py

Builds a small synthetic Unity-like project in a temp directory, asserts exact
expected results, and cleans up.  Standard library only.
"""

import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import reference_scan as rs  # noqa: E402

SCRIPT = os.path.join(HERE, "reference_scan.py")

# 32-hex GUIDs, one per synthetic asset.
S = "a1" * 16          # Assets/Scripts/Player.cs
P = "b2" * 16          # Assets/Prefabs/Player.prefab
V = "c3" * 16          # Assets/Prefabs/PlayerVariant.prefab
M = "d4" * 16          # Assets/Materials/Red.mat
SCN1 = "e5" * 16       # Assets/Scenes/Main.unity
SCN2 = "f6" * 16       # Assets/Scenes/Other.unity
TEX = "0a" * 16        # Assets/Art/icon.png
BIN = "77" * 16        # Assets/Data/blob.asset (binary)
LATE = "78" * 16       # Assets/Data/late.asset (NUL after the sniff window)
CRLF = "79" * 16       # Assets/Scenes/Crlf.unity
NEST = "7a" * 16       # Assets/Builds/Nested.prefab
DUP = "dd" * 16        # declared by two metas
GHOST = "7b" * 16      # Library/... (must be skipped)
DANGLING = "99" * 16   # referenced, declared nowhere
BUILTIN = "0000000000000000f000000000000000"

HEADER = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:"]


def meta(guid, importer="DefaultImporter:", extra=()):
    return "\n".join(["fileFormatVersion: 2", "guid: " + guid, importer] + list(extra)) + "\n"


class ProjectCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = tempfile.mkdtemp(prefix="refscan_test_")
        cls.project = os.path.join(cls.root, "proj")
        os.makedirs(cls.project)
        cls._build()
        cls.scanner = rs.Scanner(cls.project)
        cls.scanner.build()
        cls.edges = list(cls.scanner.scan())

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.root, ignore_errors=True)

    # -- fixture helpers ---------------------------------------------------------

    @classmethod
    def _write(cls, rel, data):
        full = os.path.join(cls.project, *rel.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "wb") as f:
            f.write(data if isinstance(data, bytes) else data.encode("utf-8"))

    @classmethod
    def _asset(cls, rel, content, guid, meta_text=None):
        cls._write(rel, content)
        cls._write(rel + ".meta", meta_text if meta_text is not None else meta(guid))

    @classmethod
    def _build(cls):
        a = cls._asset

        # A .cs script (not a scannable extension) with a GUID-looking comment.
        a("Assets/Scripts/Player.cs", "public class Player {} // guid: %s\n" % S, S,
          meta(S, "MonoImporter:", ["  externalObjects: {}"]))

        # Prefab: the script is referenced twice (two MonoBehaviours), plus an icon.
        # Lines 8 and 12 are same-file references (no guid) and must not be edges.
        a("Assets/Prefabs/Player.prefab", "\n".join(HEADER + [
            "--- !u!1 &1000",                                                   # 3
            "GameObject:",                                                      # 4
            "  m_Name: Player",                                                 # 5
            "--- !u!114 &2000",                                                 # 6
            "MonoBehaviour:",                                                   # 7
            "  m_GameObject: {fileID: 1000}",                                   # 8
            "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % S,            # 9
            "--- !u!114 &3000",                                                 # 10
            "MonoBehaviour:",                                                   # 11
            "  m_GameObject: {fileID: 1000}",                                   # 12
            "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % S,            # 13
            "  m_Icon: {fileID: 21300000, guid: %s, type: 3}" % TEX,            # 14
        ]) + "\n", P)

        # Scene referencing the prefab (PrefabInstance) and a material twice (list items,
        # dash at the same indent as its key, as Unity writes it).
        a("Assets/Scenes/Main.unity", "\n".join(HEADER + [
            "--- !u!1001 &100100000",                                           # 3
            "PrefabInstance:",                                                  # 4
            "  m_SourcePrefab: {fileID: 100100000, guid: %s, type: 3}" % P,     # 5
            "--- !u!23 &500",                                                   # 6
            "MeshRenderer:",                                                    # 7
            "  m_Materials:",                                                   # 8
            "  - {fileID: 2100000, guid: %s, type: 2}" % M,                     # 9
            "  - {fileID: 2100000, guid: %s, type: 2}" % M,                     # 10
            "  m_Other: {fileID: 0}",                                           # 11
        ]) + "\n", SCN1)

        # Second scene: material in a deeper-indented list, a dangling reference, and the
        # GUID inside prose (other-text: reported, but flagged, with unknown property).
        a("Assets/Scenes/Other.unity", "\n".join(HEADER + [
            "--- !u!23 &600",                                                   # 3
            "MeshRenderer:",                                                    # 4
            "  m_Materials:",                                                   # 5
            "    - {fileID: 2100000, guid: %s, type: 2}" % M,                   # 6
            "  m_Missing: {fileID: 1, guid: %s, type: 3}" % DANGLING,           # 7
            '  m_Note: "see guid: %s for details"' % M,                         # 8
        ]) + "\n", SCN2)

        # Prefab variant: references its parent three ways.  `stripped` header on 12.
        a("Assets/Prefabs/PlayerVariant.prefab", "\n".join(HEADER + [
            "--- !u!1001 &7000",                                                # 3
            "PrefabInstance:",                                                  # 4
            "  m_Modification:",                                                # 5
            "    m_Modifications:",                                             # 6
            "    - target: {fileID: 1000, guid: %s, type: 3}" % P,              # 7
            "      propertyPath: m_Name",                                       # 8
            "      value: Variant",                                             # 9
            "      objectReference: {fileID: 0}",                               # 10
            "  m_SourcePrefab: {fileID: 100100000, guid: %s, type: 3}" % P,     # 11
            "--- !u!1 &8000 stripped",                                          # 12
            "GameObject:",                                                      # 13
            "  m_CorrespondingSourceObject: {fileID: 1000, guid: %s, type: 3}" % P,  # 14
        ]) + "\n", V)

        # CRLF line endings.
        a("Assets/Scenes/Crlf.unity", "\r\n".join(HEADER + [
            "--- !u!114 &42",                                                   # 3
            "MonoBehaviour:",                                                   # 4
            "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % S,            # 5
        ]) + "\r\n", CRLF)

        # Material: builtin (unmapped) shader guid and a texture.
        a("Assets/Materials/Red.mat", "\n".join(HEADER + [
            "--- !u!21 &2100000",                                               # 3
            "Material:",                                                        # 4
            "  m_Shader: {fileID: 46, guid: %s, type: 0}" % BUILTIN,            # 5
            "  m_SavedProperties:",                                             # 6
            "    m_TexEnvs:",                                                   # 7
            "    - _MainTex:",                                                  # 8
            "        m_Texture: {fileID: 2800000, guid: %s, type: 3}" % TEX,    # 9
        ]) + "\n", M, meta(M, "NativeFormatImporter:"))

        # Texture whose .meta (importer settings) references the material at line 7.
        # Line 2 is the identity line and must NOT be an edge.
        a("Assets/Art/icon.png", b"\x89PNG\r\n\x1a\n\x00\x00\x00fake", TEX,
          "\n".join([
              "fileFormatVersion: 2",                                           # 1
              "guid: " + TEX,                                                   # 2
              "TextureImporter:",                                               # 3
              "  externalObjects:",                                             # 4
              "  - first:",                                                     # 5
              "      type: UnityEngine:Material",                               # 6
              "    second: {fileID: 2100000, guid: %s, type: 2}" % M,           # 7
              "  spriteSheet:",                                                 # 8
          ]) + "\n")

        # Binary asset: NUL early, with text that looks like references.
        a("Assets/Data/blob.asset",
          b"Unity\x00\x01\x02 {fileID: 1, guid: " + S.encode() + b", type: 3}\x00\n", BIN)

        # Text for > BINARY_SNIFF_BYTES, valid reference, THEN a NUL: whole file skipped.
        late = ("\n".join(HEADER + [
            "--- !u!114 &1",
            "MonoBehaviour:",
            "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % S,
            "  m_Pad: " + "x" * (rs.BINARY_SNIFF_BYTES + 500),
        ]) + "\n").encode() + b"tail\x00binary\n"
        a("Assets/Data/late.asset", late, LATE)

        # Nested dir named like a skip dir: scanned (SKIP_DIRS apply at root only).
        # Also: ambiguous guid, malformed guid, same-file ref.
        a("Assets/Builds/Nested.prefab", "\n".join(HEADER + [
            "--- !u!114 &1",                                                   # 3
            "MonoBehaviour:",                                                   # 4
            "  m_Icon: {fileID: 21300000, guid: %s, type: 3}" % TEX,            # 5
            "  m_Dup: {fileID: 1, guid: %s, type: 3}" % DUP,                    # 6
            "  m_Bad: {fileID: 1, guid: nothex, type: 3}",                      # 7
            "  m_Self: {fileID: 1}",                                            # 8
        ]) + "\n", NEST)

        # Two assets with the same GUID -> ambiguous.
        cls._write("Assets/Dup/A.txt", "a")
        cls._write("Assets/Dup/A.txt.meta", meta(DUP))
        cls._write("Assets/Dup/B.txt", "b")
        cls._write("Assets/Dup/B.txt.meta", meta(DUP))

        # Skipped root directories: must not be scanned or mapped.
        a("Library/Cache/ghost.prefab", "\n".join(HEADER + [
            "--- !u!114 &1", "MonoBehaviour:",
            "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % S]) + "\n", GHOST)
        a("Temp/t.prefab", "  m_Script: {fileID: 1, guid: %s, type: 3}\n" % S, GHOST)

    # -- helpers for assertions -------------------------------------------------------

    def key(self, e):
        return (e.source, e.doc_file_id, e.line, e.guid, e.prop, e.form)

    def edges_for(self, guid):
        return sorted(((e.source, e.doc_file_id, e.line) for e in self.edges if e.guid == guid))

    # -- tests ---------------------------------------------------------------------------

    def test_guid_map(self):
        g = self.scanner.guid_to_paths
        self.assertEqual(g[S], ["Assets/Scripts/Player.cs"])
        self.assertEqual(g[P], ["Assets/Prefabs/Player.prefab"])
        self.assertEqual(g[TEX], ["Assets/Art/icon.png"])
        self.assertEqual(g[DUP], ["Assets/Dup/A.txt", "Assets/Dup/B.txt"])
        self.assertNotIn(GHOST, g)           # Library/ and Temp/ skipped
        self.assertNotIn(BUILTIN, g)
        self.assertNotIn(DANGLING, g)
        self.assertEqual(self.scanner.path_to_guid["Assets/Prefabs/Player.prefab"], P)

    def test_exact_full_edge_list(self):
        expected = sorted([
            ("Assets/Art/icon.png.meta", None, 7, M, "second", "flow-map"),
            ("Assets/Builds/Nested.prefab", "1", 5, TEX, "m_Icon", "flow-map"),
            ("Assets/Builds/Nested.prefab", "1", 6, DUP, "m_Dup", "flow-map"),
            ("Assets/Materials/Red.mat", "2100000", 5, BUILTIN, "m_Shader", "flow-map"),
            ("Assets/Materials/Red.mat", "2100000", 9, TEX, "m_Texture", "flow-map"),
            ("Assets/Prefabs/Player.prefab", "2000", 9, S, "m_Script", "flow-map"),
            ("Assets/Prefabs/Player.prefab", "3000", 13, S, "m_Script", "flow-map"),
            ("Assets/Prefabs/Player.prefab", "3000", 14, TEX, "m_Icon", "flow-map"),
            ("Assets/Prefabs/PlayerVariant.prefab", "7000", 7, P, "target", "flow-map"),
            ("Assets/Prefabs/PlayerVariant.prefab", "7000", 11, P, "m_SourcePrefab", "flow-map"),
            ("Assets/Prefabs/PlayerVariant.prefab", "8000", 14, P, "m_CorrespondingSourceObject", "flow-map"),
            ("Assets/Scenes/Crlf.unity", "42", 5, S, "m_Script", "flow-map"),
            ("Assets/Scenes/Main.unity", "100100000", 5, P, "m_SourcePrefab", "flow-map"),
            ("Assets/Scenes/Main.unity", "500", 9, M, "m_Materials[]", "flow-map"),
            ("Assets/Scenes/Main.unity", "500", 10, M, "m_Materials[]", "flow-map"),
            ("Assets/Scenes/Other.unity", "600", 6, M, "m_Materials[]", "flow-map"),
            ("Assets/Scenes/Other.unity", "600", 7, DANGLING, "m_Missing", "flow-map"),
            ("Assets/Scenes/Other.unity", "600", 8, M, None, "other-text"),
        ])
        self.assertEqual(sorted(self.key(e) for e in self.edges), expected)

    def test_script_referenced_by_prefab(self):
        in_prefab = [(src, doc, ln) for (src, doc, ln) in self.edges_for(S)
                     if src.endswith(".prefab")]
        self.assertEqual(in_prefab, [("Assets/Prefabs/Player.prefab", "2000", 9),
                                     ("Assets/Prefabs/Player.prefab", "3000", 13)])

    def test_prefab_referenced_by_scene(self):
        scenes = [t for t in self.edges_for(P) if t[0].endswith(".unity")]
        self.assertEqual(scenes, [("Assets/Scenes/Main.unity", "100100000", 5)])

    def test_variant_references_parent(self):
        variant = [t for t in self.edges_for(P) if t[0] == "Assets/Prefabs/PlayerVariant.prefab"]
        # Three raw facts, none collapsed or interpreted; the `stripped` doc keeps its id.
        self.assertEqual(variant, [("Assets/Prefabs/PlayerVariant.prefab", "7000", 7),
                                   ("Assets/Prefabs/PlayerVariant.prefab", "7000", 11),
                                   ("Assets/Prefabs/PlayerVariant.prefab", "8000", 14)])

    def test_material_referenced_by_two_scenes(self):
        scenes = sorted(set(src for (src, _d, _l) in self.edges_for(M) if src.endswith(".unity")))
        self.assertEqual(scenes, ["Assets/Scenes/Main.unity", "Assets/Scenes/Other.unity"])

    def test_same_guid_twice_in_one_file_is_two_facts(self):
        main = [t for t in self.edges_for(M) if t[0] == "Assets/Scenes/Main.unity"]
        self.assertEqual(main, [("Assets/Scenes/Main.unity", "500", 9),
                                ("Assets/Scenes/Main.unity", "500", 10)])
        prefab = [t for t in self.edges_for(S) if t[0] == "Assets/Prefabs/Player.prefab"]
        self.assertEqual([t[2] for t in prefab], [9, 13])

    def test_same_file_reference_without_guid_is_not_an_edge(self):
        no_guid_lines = {
            "Assets/Prefabs/Player.prefab": {8, 12},
            "Assets/Scenes/Main.unity": {11},
            "Assets/Builds/Nested.prefab": {8},
            "Assets/Prefabs/PlayerVariant.prefab": {10},
        }
        for e in self.edges:
            self.assertNotIn(e.line, no_guid_lines.get(e.source, set()), self.key(e))

    def test_binary_files_skipped_without_crash(self):
        self.assertEqual(self.scanner.skipped_binary,
                         ["Assets/Data/blob.asset", "Assets/Data/late.asset"])
        self.assertFalse([e for e in self.edges if e.source.startswith("Assets/Data/")])

    def test_meta_references_recorded_but_identity_line_is_not(self):
        meta_edges = [e for e in self.edges if e.source == "Assets/Art/icon.png.meta"]
        self.assertEqual([(e.line, e.guid) for e in meta_edges], [(7, M)])
        self.assertIsNone(meta_edges[0].doc_file_id)
        self.assertEqual(meta_edges[0].target_file_id, "2100000")
        # The Player.cs.meta identity line is not a reference to S.
        self.assertFalse([e for e in self.edges if e.source == "Assets/Scripts/Player.cs.meta"])

    def test_skipped_directories_and_nested_skip_names(self):
        sources = set(e.source for e in self.edges)
        self.assertFalse([s for s in sources if s.startswith(("Library/", "Temp/"))])
        self.assertIn("Assets/Builds/Nested.prefab", sources)   # nested Builds/ is scanned
        self.assertEqual(self.scanner.summary()["nested_dirs_matching_skip_names_but_scanned"],
                         ["Assets/Builds"])

    def test_unknown_and_ambiguous_targets(self):
        d = {e.guid: self.scanner.edge_to_dict(e) for e in self.edges if e.guid in (DANGLING, DUP, BUILTIN)}
        self.assertIsNone(d[DANGLING]["referenced_path"])
        self.assertEqual(d[DANGLING]["referenced_paths"], [])
        self.assertIsNone(d[BUILTIN]["referenced_path"])
        self.assertIsNone(d[DUP]["referenced_path"])
        self.assertEqual(d[DUP]["referenced_paths"], ["Assets/Dup/A.txt", "Assets/Dup/B.txt"])
        warnings = "\n".join(self.scanner.warnings)
        self.assertIn("duplicate guid " + DUP, warnings)
        self.assertIn("Assets/Builds/Nested.prefab:7: malformed guid value 'nothex'", warnings)

    def test_columns_and_target_file_ids(self):
        e = next(e for e in self.edges if e.source == "Assets/Prefabs/Player.prefab" and e.line == 9)
        line = "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % S
        self.assertEqual(e.column, line.index("guid:") + 1)
        self.assertEqual(e.target_file_id, "11500000")
        e = next(e for e in self.edges if e.source == "Assets/Scenes/Other.unity" and e.line == 8)
        self.assertIsNone(e.target_file_id)
        self.assertIsNone(e.prop)
        self.assertIsNone(e.prop_via)

    def test_property_provenance(self):
        by = {(e.source, e.line): e for e in self.edges}
        self.assertEqual(by[("Assets/Scenes/Main.unity", 9)].prop_via, "list-owner-lookup")
        self.assertEqual(by[("Assets/Scenes/Other.unity", 6)].prop, "m_Materials[]")
        self.assertEqual(by[("Assets/Prefabs/Player.prefab", 9)].prop_via, "inline-key")

    # -- parser edge cases without a project ---------------------------------------------

    def _scan_text(self, text, is_meta=False):
        return rs.scan_stream(io.BytesIO(text.encode()), "x.asset", is_meta)

    def test_unparseable_doc_header_gives_unknown_id(self):
        text = "\n".join(["--- !u!114 &5", "A:", "  a: {fileID: 1, guid: %s}" % S,
                          "--- !u!114 &notanumber", "B:", "  b: {fileID: 1, guid: %s}" % S]) + "\n"
        _ident, edges, _w = self._scan_text(text)
        self.assertEqual([(e.doc_file_id, e.prop) for e in edges], [("5", "a"), (None, "b")])

    def test_flow_sequence_property_is_unknown_not_guessed(self):
        text = "--- !u!1 &1\nA:\n  m_List: [{fileID: 1, guid: %s}, {fileID: 2, guid: %s}]\n" % (S, P)
        _i, edges, _w = self._scan_text(text)
        self.assertEqual([(e.guid, e.prop, e.target_file_id) for e in edges],
                         [(S, None, "1"), (P, None, "2")])
        self.assertEqual(edges[0].column < edges[1].column, True)

    def test_list_item_after_valued_key_is_unknown(self):
        text = "--- !u!1 &1\nA:\n  m_Name: foo\n  - {fileID: 1, guid: %s}\n" % S
        _i, edges, _w = self._scan_text(text)
        self.assertEqual([e.prop for e in edges], [None])

    # -- CLI -----------------------------------------------------------------------------

    def run_cli(self, *args):
        p = subprocess.run([sys.executable, SCRIPT] + list(args), stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, universal_newlines=True)
        return p.returncode, p.stdout, p.stderr

    def snapshot(self):
        snap = {}
        for dp, dns, fns in os.walk(self.project):
            for n in dns + fns:
                full = os.path.join(dp, n)
                st = os.lstat(full)
                snap[full] = (st.st_size, st.st_mtime_ns, st.st_mode)
        return snap

    def test_cli_who_references_by_path_and_guid_and_readonly(self):
        before = self.snapshot()
        rc, out, _err = self.run_cli("--project", self.project, "--json", "who-references",
                                     "Assets/Prefabs/Player.prefab", "--quiet")
        self.assertEqual(rc, 0)
        doc = json.loads(out)
        self.assertEqual(doc["query"]["guid"], P)
        self.assertEqual([(e["source_file"], e["line"]) for e in doc["edges"]],
                         [("Assets/Prefabs/PlayerVariant.prefab", 7),
                          ("Assets/Prefabs/PlayerVariant.prefab", 11),
                          ("Assets/Prefabs/PlayerVariant.prefab", 14),
                          ("Assets/Scenes/Main.unity", 5)])
        rc2, out2, _ = self.run_cli("--project", self.project, "--json", "--quiet", "who-references", P)
        self.assertEqual(json.loads(out2)["edges"], doc["edges"])
        rc3, out3, _ = self.run_cli("--project", self.project, "--quiet", "who-references", P)
        self.assertIn("Assets/Scenes/Main.unity:5:", out3)
        self.assertIn("-> Assets/Prefabs/Player.prefab", out3)
        self.assertEqual(self.snapshot(), before)   # read-only

    def test_cli_references_of(self):
        rc, out, _ = self.run_cli("--project", self.project, "--json", "--quiet",
                                  "references-of", "Assets/Prefabs/Player.prefab")
        self.assertEqual(rc, 0)
        edges = json.loads(out)["edges"]
        self.assertEqual([(e["line"], e["guid"], e["property"], e["referenced_path"]) for e in edges],
                         [(9, S, "m_Script", "Assets/Scripts/Player.cs"),
                          (13, S, "m_Script", "Assets/Scripts/Player.cs"),
                          (14, TEX, "m_Icon", "Assets/Art/icon.png")])

    def test_cli_references_of_errors(self):
        rc, _o, err = self.run_cli("--project", self.project, "--quiet", "references-of", "Assets/Nope.prefab")
        self.assertEqual(rc, 2)
        rc, _o, err = self.run_cli("--project", self.project, "--quiet", "references-of", "Assets/Data/blob.asset")
        self.assertEqual(rc, 3)
        self.assertIn("binary", err)
        rc, _o, err = self.run_cli("--project", self.project, "--quiet", "who-references", "Assets/Nope.prefab")
        self.assertEqual(rc, 2)

    def test_cli_dump_json_matches_api(self):
        rc, out, _ = self.run_cli("--project", self.project, "--json", "--quiet", "dump")
        self.assertEqual(rc, 0)
        doc = json.loads(out)
        self.assertEqual(len(doc["edges"]), len(self.edges))
        self.assertEqual(doc["summary"]["skipped_binary_files"],
                         ["Assets/Data/blob.asset", "Assets/Data/late.asset"])
        rc, out, _ = self.run_cli("--project", self.project, "--quiet", "dump")
        self.assertEqual(len(out.splitlines()), len(self.edges))


if __name__ == "__main__":
    unittest.main(verbosity=2)
