#!/usr/bin/env python3
"""reference_scan.py - build a GUID reference map for a Unity project from files on disk.

Read-only, standard library only, Python 3.9+.

What it does
  1. Reads every `.meta` file and builds  guid -> asset path.
  2. Streams every scannable text asset (plus every `.meta`) line by line and records
     every textual occurrence of a `guid: <32 hex>` key as one edge.
  3. Answers three queries: who references X, what does file F reference, dump all edges.

What it deliberately does NOT do
  It reports raw textual facts only.  It never resolves prefab variants, nested prefab
  overrides, stripped objects, or any other semantic relationship, and it never
  deduplicates: N occurrences on N lines are N edges.  Anything it cannot determine
  reliably is reported as unknown (None / "?"), never inferred.

See README.md for the full limitations list.
"""

import argparse
import json
import os
import re
import sys
import time

# ---------------------------------------------------------------------------
# Configuration (module-level constants, meant to be edited / extended)
# ---------------------------------------------------------------------------

# Extensions of Unity text-serialized assets that get scanned for references.
# Compared case-insensitively against the file's last extension.  Extend freely.
# (`.meta` files are always scanned as well; they are handled separately.)
SCANNABLE_EXTENSIONS = frozenset([
    ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim",
    ".overrideController", ".physicMaterial", ".playable", ".signal", ".mixer",
    ".renderTexture", ".terrainlayer", ".preset", ".guiskin", ".fontsettings",
    ".spriteatlas", ".lighting", ".cubemap", ".flare", ".brush", ".shadervariants",
])

META_EXTENSION = ".meta"

# Directory names that are skipped entirely.
SKIP_DIRS = frozenset([
    "Library", "Temp", "Obj", "obj", "Build", "Builds", "Logs", "UserSettings", ".git",
])

# JUDGEMENT CALL: SKIP_DIRS apply only to direct children of the project root.
# A folder like Assets/Builds/ or Assets/Art/Build/ is ordinary asset content and
# silently dropping it would create missing edges, which is worse than scanning a few
# extra files.  Set to False to skip these names at any depth.  Nested directories
# that match a skip name but are scanned anyway are listed in the scan summary.
SKIP_DIRS_ROOT_ONLY = True

# How many leading bytes are inspected for NUL bytes to detect binary files up front.
BINARY_SNIFF_BYTES = 8192

# ---------------------------------------------------------------------------
# Regular expressions
# ---------------------------------------------------------------------------

# A `guid:` key.  The lookbehind rejects keys such as `m_guid:` / `myguid:`.
# Case-sensitive on purpose: Unity writes lowercase `guid:`; JSON-style `GUID:` (asmdef)
# is a different syntax that this tool does not claim to read.
# The captured value stops at whitespace, `,`, `}` or `]` so that flow-map neighbours
# are not swallowed.  Validation of the value happens separately.
GUID_KEY_RE = re.compile(r"(?<!\w)guid:[ \t]*([^\s,}\]]*)")
GUID_FULL_RE = re.compile(r"[0-9a-fA-F]{32}")

# `--- !u!<classId> &<fileID>` optionally followed by ` stripped`.
# fileIDs can be negative, so `-?` is allowed.  Any header that does not match this
# exactly yields an UNKNOWN document id (we do not guess).
DOC_HEADER_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)(?: stripped)?\s*$")

# A YAML mapping key at the start of `rest` (after indentation and any `- `).
# group(1) = key, group(2) = inline value (None / "" when the key has no value on
# its line).  Keys with spaces or quotes are intentionally not recognised.
KEY_RE = re.compile(r"([\w$.\-/]+):(?: +(.*))?$")

# Text before a `guid:` key that makes it a block-style key: only indentation and
# list dashes.
BLOCK_PREFIX_RE = re.compile(r" *(?:- +)*")

# `key:` immediately before an opening brace, e.g. `m_Script:` in `m_Script: {`.
INLINE_KEY_TAIL_RE = re.compile(r"(?:^|[ ,{\[])([\w$.\-/]+):$")

# Only a list dash (and indentation) before the brace: `  - {`.
BARE_ITEM_RE = re.compile(r" *-")

FILE_ID_RE = re.compile(r"(?<!\w)fileID:[ \t]*(-?\d+)")


class BinaryFileError(Exception):
    """Raised when a file turns out to contain NUL bytes (i.e. is not text)."""


# ---------------------------------------------------------------------------
# Edge record
# ---------------------------------------------------------------------------

class Edge(object):
    """One textual `guid:` occurrence.  Fields are raw facts; None means unknown."""

    __slots__ = ("source", "doc_file_id", "line", "column", "guid",
                 "target_file_id", "prop", "prop_via", "form")

    def __init__(self, source, doc_file_id, line, column, guid,
                 target_file_id, prop, prop_via, form):
        self.source = source                  # project-relative path, '/' separators
        self.doc_file_id = doc_file_id        # str of enclosing `--- !u!c &ID`, or None
        self.line = line                      # 1-based line number
        self.column = column                  # 1-based char offset of the `guid:` key
        self.guid = guid                      # lowercased 32-hex
        self.target_file_id = target_file_id  # `fileID:` inside the same {} map, or None
        self.prop = prop                      # immediate property name, or None
        self.prop_via = prop_via              # how prop was found (see scan_stream)
        self.form = form                      # 'flow-map' | 'block-key' | 'other-text'


# ---------------------------------------------------------------------------
# Single-file scanner
# ---------------------------------------------------------------------------

def _list_owner(stack, dash_indent):
    """Name of the key owning a list item whose dash sits at `dash_indent`.

    Candidate = the most recent key line with indent <= dash_indent (YAML allows the
    dash at the same indent as its key, which is what Unity writes, or deeper).  It
    only counts if that key had no inline value; otherwise the owner is unknown.
    """
    for indent, name, empty in reversed(stack):
        if indent <= dash_indent:
            return name if empty else None
    return None


def _parent_key(stack, key_indent):
    """Name of the enclosing key of a block-style key sitting at `key_indent`."""
    for indent, name, empty in reversed(stack):
        if indent < key_indent:
            return name if empty else None
    return None


def _map_close(line, open_idx):
    """Index of the `}` matching the `{` at open_idx, or None if not on this line."""
    depth = 0
    for i in range(open_idx, len(line)):
        ch = line[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return i
    return None


def scan_stream(f, rel, is_meta):
    """Scan an open binary file object line by line.

    Returns (identity_guid_or_None, [Edge], [warning strings]).
    Raises BinaryFileError if a NUL byte is seen anywhere.
    Edges are buffered so a late NUL discards the whole file's edges.
    """
    edges = []
    warns = []
    identity = None
    doc_id = None          # fileID of the current `--- !u!` document, None if unknown
    stack = []             # [(indent, key, has_no_inline_value)], strictly increasing indent
    warned_cr = False

    for n, raw in enumerate(f, 1):
        if b"\x00" in raw:
            raise BinaryFileError(n)
        # Lines split on b"\n" only; CRLF handled by the rstrip.  Invalid UTF-8 is
        # replaced (hex GUID matching is unaffected).
        line = raw.decode("utf-8", "replace").rstrip("\r\n")
        if "\r" in line and not warned_cr:
            warned_cr = True
            warns.append("%s:%d: contains bare CR; line numbers may differ from editors" % (rel, n))

        # --- document header -------------------------------------------------
        if line == "---" or line.startswith("--- "):
            m = DOC_HEADER_RE.match(line)
            # Unparseable header => unknown id (never carry over the previous one).
            doc_id = m.group(2) if m else None
            stack = []
            continue

        # --- key / list-owner bookkeeping (needed to name properties) ---------
        stripped = line.lstrip(" ")
        if stripped and stripped[0] != "#":
            indent = len(line) - len(stripped)
            rest = stripped
            if rest == "-" or rest.startswith("- "):
                # A new list item closes everything strictly deeper than its dash.
                while stack and stack[-1][0] > indent:
                    stack.pop()
                rest = rest[1:].lstrip(" ")
            km = KEY_RE.match(rest)
            if km:
                pos = len(line) - len(rest)
                while stack and stack[-1][0] >= pos:
                    stack.pop()
                val = km.group(2)
                empty = val is None or val.strip() == "" or val.lstrip().startswith("#")
                stack.append((pos, km.group(1), empty))

        # --- guid occurrences ---------------------------------------------------
        if "guid:" not in line:
            continue

        brackets = []      # open '{' / '[' seen so far in this line: (char, index)
        scanned = 0
        for gm in GUID_KEY_RE.finditer(line):
            start = gm.start()
            for i in range(scanned, start):
                ch = line[i]
                if ch == "{" or ch == "[":
                    brackets.append((ch, i))
                elif ch == "}" or ch == "]":
                    if brackets:
                        brackets.pop()
            scanned = start

            # Classify the context of this `guid:` key.
            # Bracket counting ignores quotes (see README limitations).
            if brackets:
                top, open_idx = brackets[-1]
                prev = line[:start].rstrip()[-1:]
                # A real flow-map key follows `{` or `,`.  Anything else (e.g. text
                # inside a quoted string) is reported as other-text, not dropped.
                form = "flow-map" if (top == "{" and prev in ("{", ",")) else "other-text"
            elif BLOCK_PREFIX_RE.fullmatch(line[:start]):
                form = "block-key"
            else:
                form = "other-text"

            value = gm.group(1)
            if not GUID_FULL_RE.fullmatch(value):
                # Not a 32-hex GUID: not an edge.  Surface it so it is not silently
                # lost (skipped for other-text to avoid noise from prose).
                if form != "other-text":
                    warns.append("%s:%d: malformed guid value %r (not a 32-hex GUID); not recorded as an edge"
                                 % (rel, n, value))
                continue
            guid = value.lower()  # JUDGEMENT CALL: uppercase hex is accepted and lowercased.

            # JUDGEMENT CALL: in a .meta file the first unindented, block-style
            # `guid:` line is the file's own identity (a definition), not a
            # reference to another asset, so it feeds the guid map instead of the
            # edge list.  Every other `guid:` in a .meta (importer settings etc.)
            # is a normal edge.
            if is_meta and identity is None and form == "block-key" and start == 0:
                identity = guid
                continue

            prop = None
            prop_via = None
            target_file_id = None

            if form == "flow-map":
                before = line[:open_idx].rstrip()
                km = INLINE_KEY_TAIL_RE.search(before)
                if km:
                    # Reliable: the key sits on this very line, right before the `{`.
                    prop, prop_via = km.group(1), "inline-key"
                elif BARE_ITEM_RE.fullmatch(before):
                    # `- {...}`: the property is the key owning the list, found by
                    # indentation lookback.  Reported with a `[]` suffix.
                    owner = _list_owner(stack, len(before) - 1)
                    if owner is not None:
                        prop, prop_via = owner + "[]", "list-owner-lookup"
                # Anything else (flow sequences `[{...}, {...}]`, odd layouts):
                # property stays unknown.
                close = _map_close(line, open_idx)
                if close is not None:
                    fm = FILE_ID_RE.search(line, open_idx, close)
                    if fm:
                        target_file_id = fm.group(1)
            elif form == "block-key":
                # `start` is the indent of the key text (prefix is only spaces and dashes);
                # the enclosing key is the nearest shallower key line.
                owner = _parent_key(stack, start)
                if owner is not None:
                    prop, prop_via = owner, "block-parent-lookup"
            # other-text: no property, no target fileID.

            edges.append(Edge(rel, doc_id, n, start + 1, guid, target_file_id,
                              prop, prop_via, form))

    return identity, edges, warns


# ---------------------------------------------------------------------------
# Project scanner
# ---------------------------------------------------------------------------

class Progress(object):
    """Throttled progress reporting to stderr."""

    def __init__(self, enabled):
        self.enabled = enabled
        self._last = 0.0

    def tick(self, label, done, total, force=False):
        if not self.enabled:
            return
        now = time.time()
        if force or now - self._last >= 1.0:
            self._last = now
            sys.stderr.write("[refscan] %s: %d/%d files\n" % (label, done, total))
            sys.stderr.flush()

    def note(self, text):
        if self.enabled:
            sys.stderr.write("[refscan] %s\n" % text)
            sys.stderr.flush()


class Scanner(object):
    def __init__(self, project, progress=False):
        self.project = os.path.abspath(project)
        self.progress = Progress(progress)
        self.warnings = []
        self.skipped_binary = []     # files skipped because they are binary
        self.unreadable = []         # files that raised OSError
        self.nested_skip_named = []  # nested dirs matching SKIP_DIRS but scanned
        self.meta_files = []
        self.asset_files = []
        self.guid_to_paths = {}      # guid -> sorted list of asset paths
        self.path_to_guid = {}       # asset path -> guid
        self.edge_count = 0
        self._meta_cache = {}        # meta rel path -> edges (from pass 1)
        self._scannable = set()
        self.file_status = {}        # rel -> 'ok' | 'binary' | 'unreadable'
        self._built = False
        self._ext_set = frozenset(e.lower() for e in SCANNABLE_EXTENSIONS)

    # -- discovery -------------------------------------------------------------

    def _discover(self):
        root = self.project

        def onerror(err):
            self.warnings.append("cannot list directory %s: %s" % (getattr(err, "filename", "?"), err))

        for dirpath, dirnames, filenames in os.walk(root, followlinks=False, onerror=onerror):
            at_root = os.path.abspath(dirpath) == root
            rel_dir = "" if at_root else os.path.relpath(dirpath, root).replace(os.sep, "/")
            kept = []
            for d in sorted(dirnames):
                full = os.path.join(dirpath, d)
                relp = (rel_dir + "/" + d) if rel_dir else d
                if d in SKIP_DIRS:
                    if at_root or not SKIP_DIRS_ROOT_ONLY:
                        continue
                    self.nested_skip_named.append(relp)
                if os.path.islink(full):
                    self.warnings.append("symlinked directory not followed: %s" % relp)
                    continue
                kept.append(d)
            dirnames[:] = kept
            for name in sorted(filenames):
                relp = (rel_dir + "/" + name) if rel_dir else name
                if name.endswith(META_EXTENSION):
                    if name == META_EXTENSION:
                        self.warnings.append("ignored file literally named '.meta': %s" % relp)
                        continue
                    self.meta_files.append(relp)
                elif os.path.splitext(name)[1].lower() in self._ext_set:
                    self.asset_files.append(relp)
        self._scannable = set(self.meta_files) | set(self.asset_files)

    # -- one file ---------------------------------------------------------------

    def _scan_file(self, rel, is_meta):
        """Returns (status, identity, edges, warns). Never raises for I/O problems."""
        full = os.path.join(self.project, *rel.split("/"))
        try:
            with open(full, "rb") as f:          # read-only, always
                head = f.read(BINARY_SNIFF_BYTES)
                if b"\x00" in head:
                    return "binary", None, [], []
                f.seek(0)
                identity, edges, warns = scan_stream(f, rel, is_meta)
                return "ok", identity, edges, warns
        except BinaryFileError:
            return "binary", None, [], []
        except OSError as e:
            return "unreadable", None, [], ["cannot read %s: %s" % (rel, e)]

    def _record_status(self, rel, status):
        self.file_status[rel] = status
        if status == "binary":
            self.skipped_binary.append(rel)
        elif status == "unreadable":
            self.unreadable.append(rel)

    # -- pass 1: guid map -----------------------------------------------------------

    def build(self):
        """Discover files and build guid -> path from every .meta file."""
        if self._built:
            return
        self.progress.note("discovering files under %s" % self.project)
        self._discover()
        total = len(self.meta_files)
        guid_map = {}
        for i, rel in enumerate(self.meta_files, 1):
            status, identity, edges, warns = self._scan_file(rel, True)
            self._record_status(rel, status)
            self.warnings.extend(warns)
            self._meta_cache[rel] = edges
            if status == "ok":
                asset_path = rel[:-len(META_EXTENSION)]
                if identity is None:
                    self.warnings.append("%s: no top-level `guid:` line found; asset not in guid map" % rel)
                else:
                    guid_map.setdefault(identity, []).append(asset_path)
                    self.path_to_guid[asset_path] = identity
            self.progress.tick("reading .meta files", i, total)
        self.progress.tick("reading .meta files", total, total, force=True)
        for g, paths in guid_map.items():
            paths.sort()
            if len(paths) > 1:
                self.warnings.append("duplicate guid %s declared by %d assets: %s"
                                     % (g, len(paths), ", ".join(paths)))
        self.guid_to_paths = guid_map
        self._built = True

    # -- pass 2: edges ------------------------------------------------------------------

    def paths_for(self, guid):
        return self.guid_to_paths.get(guid, [])

    def is_scannable(self, rel):
        return rel in self._scannable

    def scan(self, only=None):
        """Yield every Edge, in sorted source-path order, then line order.

        `only`: restrict to one project-relative file (must be scannable).
        """
        self.build()
        files = [only] if only is not None else sorted(self._scannable)
        total = len(files)
        meta_set = set(self.meta_files)
        for i, rel in enumerate(files, 1):
            if rel in meta_set:
                edges = self._meta_cache[rel]
            else:
                status, _identity, edges, warns = self._scan_file(rel, False)
                self._record_status(rel, status)
                self.warnings.extend(warns)
            for e in edges:
                self.edge_count += 1
                yield e
            self.progress.tick("scanning for references", i, total)
        self.progress.tick("scanning for references", total, total, force=True)

    # -- reporting -----------------------------------------------------------------

    def edge_to_dict(self, e):
        paths = self.paths_for(e.guid)
        return {
            "source_file": e.source,
            "source_doc_file_id": e.doc_file_id,
            "line": e.line,
            "column": e.column,
            "guid": e.guid,
            "referenced_path": paths[0] if len(paths) == 1 else None,
            "referenced_paths": paths,      # >1 entries means the guid is ambiguous
            "target_file_id": e.target_file_id,
            "property": e.prop,
            "property_via": e.prop_via,
            "form": e.form,
        }

    def format_edge(self, e):
        paths = self.paths_for(e.guid)
        if not paths:
            tgt = "<unknown path>"
        elif len(paths) == 1:
            tgt = paths[0]
        else:
            tgt = "<AMBIGUOUS: %s>" % " | ".join(paths)
        s = "%s:%d:%d  doc=%s  property=%s  guid=%s  -> %s  target_fileID=%s" % (
            e.source, e.line, e.column,
            ("&" + e.doc_file_id) if e.doc_file_id is not None else "?",
            e.prop if e.prop is not None else "?",
            e.guid, tgt,
            e.target_file_id if e.target_file_id is not None else "?")
        if e.form != "flow-map":
            s += "  [%s]" % e.form
        return s

    def summary(self):
        return {
            "project": self.project,
            "meta_files": len(self.meta_files),
            "asset_files": len(self.asset_files),
            "guids_mapped": len(self.guid_to_paths),
            "edges_emitted": self.edge_count,
            "scanned_extensions": sorted(SCANNABLE_EXTENSIONS),
            "skipped_dirs": sorted(SKIP_DIRS),
            "skip_dirs_root_only": SKIP_DIRS_ROOT_ONLY,
            "nested_dirs_matching_skip_names_but_scanned": self.nested_skip_named,
            "skipped_binary_files": self.skipped_binary,
            "unreadable_files": self.unreadable,
            "warnings": self.warnings,
        }


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def normalize_rel(project, target):
    """Turn a user-supplied path into a project-relative '/' path, or None if outside.

    Relative paths are interpreted relative to the PROJECT ROOT (not the cwd).
    """
    if os.path.isabs(target):
        t = os.path.join(os.path.realpath(os.path.dirname(target)), os.path.basename(target))
        rel = os.path.relpath(t, os.path.realpath(project))
    else:
        rel = target
    rel = rel.replace(os.sep, "/")
    while rel.startswith("./"):
        rel = rel[2:]
    rel = rel.rstrip("/")
    if rel == ".." or rel.startswith("../") or rel == "":
        return None
    return rel


def _emit_summary(scanner, args):
    if args.quiet:
        return
    s = scanner.summary()
    err = sys.stderr
    err.write("[refscan] %d .meta + %d asset files; %d guids mapped; %d edges; %d binary skipped; %d unreadable; %d warnings\n"
              % (s["meta_files"], s["asset_files"], s["guids_mapped"], s["edges_emitted"],
                 len(s["skipped_binary_files"]), len(s["unreadable_files"]), len(s["warnings"])))
    for p in s["skipped_binary_files"][:20]:
        err.write("[refscan]   skipped binary (invisible to this tool): %s\n" % p)
    if len(s["skipped_binary_files"]) > 20:
        err.write("[refscan]   ... %d more binary files (see --json summary)\n" % (len(s["skipped_binary_files"]) - 20))
    for p in s["nested_dirs_matching_skip_names_but_scanned"][:10]:
        err.write("[refscan]   note: nested dir matches a skip name but was scanned: %s\n" % p)
    limit = None if args.all_warnings else 25
    for w in s["warnings"][:limit]:
        err.write("[refscan]   warning: %s\n" % w)
    if limit is not None and len(s["warnings"]) > limit:
        err.write("[refscan]   ... %d more warnings (use --all-warnings or --json)\n" % (len(s["warnings"]) - limit))


def _dump_json(obj):
    return json.dumps(obj, sort_keys=False)


def cmd_dump(scanner, args, out):
    if args.json:
        out.write('{"edges": [\n')
        first = True
        for e in scanner.scan():
            out.write(("" if first else ",\n") + _dump_json(scanner.edge_to_dict(e)))
            first = False
        out.write('\n], "summary": ' + _dump_json(scanner.summary()) + "}\n")
    else:
        for e in scanner.scan():
            out.write(scanner.format_edge(e) + "\n")
    return 0


def cmd_who_references(scanner, args, out):
    scanner.build()
    target = args.target
    if GUID_FULL_RE.fullmatch(target):
        guid = target.lower()
        path = None
        paths = scanner.paths_for(guid)
        if len(paths) == 1:
            path = paths[0]
    else:
        rel = normalize_rel(scanner.project, target)
        guid = scanner.path_to_guid.get(rel) if rel is not None else None
        if guid is None:
            sys.stderr.write("error: no .meta-derived GUID for path %r (looked up %r, relative to the project root). "
                             "Is there a `<path>.meta` file, outside the skipped directories?\n" % (target, rel))
            return 2
        path = rel
    edges = [e for e in scanner.scan() if e.guid == guid]
    edges.sort(key=lambda e: (e.source, e.line, e.column))
    if args.json:
        out.write(_dump_json({
            "query": {"type": "who-references", "target": target, "guid": guid,
                      "path": path, "guid_paths": scanner.paths_for(guid)},
            "edges": [scanner.edge_to_dict(e) for e in edges],
            "summary": scanner.summary()}) + "\n")
    else:
        out.write("References to guid %s (%s): %d\n" % (
            guid, path if path else "path unknown / ambiguous", len(edges)))
        for e in edges:
            out.write(scanner.format_edge(e) + "\n")
    return 0


def cmd_references_of(scanner, args, out):
    scanner.build()
    rel = normalize_rel(scanner.project, args.file)
    if rel is None or not scanner.is_scannable(rel):
        sys.stderr.write(
            "error: %r is not a scanned file. It must exist under the project, outside the skipped "
            "directories, and be a `.meta` file or have an extension in SCANNABLE_EXTENSIONS "
            "(looked up %r relative to the project root).\n" % (args.file, rel))
        return 2
    edges = list(scanner.scan(only=rel))
    status = scanner.file_status.get(rel, "ok")
    if status != "ok":
        sys.stderr.write("error: %s could not be scanned (%s); it is invisible to this tool.\n" % (rel, status))
        return 3
    if args.json:
        out.write(_dump_json({
            "query": {"type": "references-of", "file": rel},
            "edges": [scanner.edge_to_dict(e) for e in edges],
            "summary": scanner.summary()}) + "\n")
    else:
        out.write("References made by %s: %d\n" % (rel, len(edges)))
        for e in edges:
            out.write(scanner.format_edge(e) + "\n")
    return 0


def build_parser():
    parser = argparse.ArgumentParser(
        prog="reference_scan.py",
        description="Read-only GUID reference map for a Unity project. Reports raw textual "
                    "`guid:` occurrences with file and line; does no semantic interpretation.")

    def add_common(p, sub):
        # On subparsers the defaults are SUPPRESS so a value given before the
        # subcommand is not overwritten; both positions are accepted.
        p.add_argument("--project", metavar="PATH", default=argparse.SUPPRESS if sub else None,
                       help="Unity project root (the folder containing Assets/) [required]")
        p.add_argument("--json", action="store_true", default=argparse.SUPPRESS if sub else False,
                       help="machine-readable JSON on stdout")
        p.add_argument("--quiet", action="store_true", default=argparse.SUPPRESS if sub else False,
                       help="no progress or summary on stderr")
        p.add_argument("--all-warnings", action="store_true", default=argparse.SUPPRESS if sub else False,
                       help="print every warning to stderr (default: first 25)")

    add_common(parser, False)
    subs = parser.add_subparsers(dest="command", required=True)

    p = subs.add_parser("who-references", help="list every file that references a GUID or asset path")
    add_common(p, True)
    p.add_argument("target", help="32-hex GUID, or asset path relative to the project root "
                                  "(e.g. Assets/Prefabs/Player.prefab)")
    p.set_defaults(func=cmd_who_references)

    p = subs.add_parser("references-of", help="list every GUID reference made by one file")
    add_common(p, True)
    p.add_argument("file", help="path of a scanned file (relative to project root, or absolute)")
    p.set_defaults(func=cmd_references_of)

    p = subs.add_parser("dump", help="dump the whole edge list")
    add_common(p, True)
    p.set_defaults(func=cmd_dump)
    return parser


def main(argv=None):
    try:
        sys.stdout.reconfigure(errors="backslashreplace")
        sys.stderr.reconfigure(errors="backslashreplace")
    except Exception:
        pass
    parser = build_parser()
    args = parser.parse_args(argv)
    if not args.project:
        parser.error("--project is required")
    if not os.path.isdir(args.project):
        sys.stderr.write("error: --project %r is not a directory\n" % args.project)
        return 2
    if not os.path.isdir(os.path.join(args.project, "Assets")):
        sys.stderr.write("warning: no Assets/ directory under %s; is this a Unity project root?\n" % args.project)
    scanner = Scanner(args.project, progress=not args.quiet)
    try:
        rc = args.func(scanner, args, sys.stdout)
        sys.stdout.flush()
    except BrokenPipeError:
        # Reader closed the pipe (e.g. `| head`); exit quietly.
        try:
            sys.stdout = None
        finally:
            return 0
    _emit_summary(scanner, args)
    return rc


if __name__ == "__main__":
    sys.exit(main())
