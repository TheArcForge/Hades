# reference_scan.py

A read-only, standard-library-only (Python 3.9+) tool that builds a GUID reference map for a
Unity project by reading files on disk. It exists so that someone can check another tool's
answers by hand: every fact it reports is traceable to an exact file, line and column.

It is deliberately independent of Hades: it shares no code with it and reads nothing but the
project's own files, so its output can serve as a second opinion when checking what Hades
reports about a project.

Its author was briefed only on Unity's on-disk format. The brief named no tool to be checked,
no expected results and no comparison. It was not fully blind, though, and the distinction
matters to anyone weighing its independence: the authoring session's context included this
repository's memory index and skill descriptions, which name Hades and mention a project graph.
So "independent" here means the author could not know what Hades reports for any query and
could not tilt an answer key toward it — not that the name was unknown. The output is
mechanical and every fact carries a file, line and column, so the tool's correctness does not
rest on its author's ignorance; check it directly instead.

It reports **raw textual facts only**. It does not resolve prefab variants, nested prefab
overrides, stripped objects, or any other semantic relationship, and it never deduplicates.
If a GUID appears five times in a file, you get five edges with five line numbers.
Where something cannot be determined reliably it is reported as unknown (`?` in text,
`null` in JSON), never inferred.

## Usage

```
python3 reference_scan.py --project /path/to/UnityProject who-references <guid | asset path>
python3 reference_scan.py --project /path/to/UnityProject references-of <file>
python3 reference_scan.py --project /path/to/UnityProject dump
```

Common flags (accepted before or after the subcommand):

| flag | meaning |
|---|---|
| `--project PATH` | project root (the folder containing `Assets/`); required |
| `--json` | machine-readable JSON on stdout |
| `--quiet` | no progress or summary on stderr |
| `--all-warnings` | print every warning to stderr (default: first 25) |

Results go to stdout; progress, the scan summary and warnings go to stderr. Paths given on
the command line are relative to the **project root** (not the cwd) and use `/`; absolute
paths inside the project are also accepted. Matching is case-sensitive.

Exit codes: `0` success (including "no references found"), `2` bad usage or target not found
or not a scanned file, `3` the requested file is binary and could not be scanned.

* `who-references X`: `X` is a 32-hex GUID or an asset path (file or folder that has a
  `.meta`). Every edge whose referenced GUID matches is listed, sorted by file, line, column.
  An unknown GUID is allowed (useful for dangling references).
* `references-of FILE`: every edge whose source is `FILE`. `FILE` must be a scanned file: a
  `.meta` or an extension in `SCANNABLE_EXTENSIONS`. Only that file is scanned for edges
  (the `.meta` files are still read to build the GUID map).
* `dump`: the whole edge list, ordered by source path then line. JSON output is streamed.

### Human-readable line

```
Assets/Scenes/Main.unity:9:5  doc=&500  property=m_Materials[]  guid=d4d4...  -> Assets/Materials/Red.mat  target_fileID=2100000
```

`source:line:column`, enclosing document fileID (`&id`, or `?`), property name (or `?`),
GUID, resolved path (`<unknown path>` or `<AMBIGUOUS: a | b>` when not exactly one asset
declares the GUID), the `fileID` found in the same `{...}` map (or `?`), and a trailing
`[block-key]` / `[other-text]` tag when the occurrence is not a normal inline map.

### JSON edge fields

| field | meaning |
|---|---|
| `source_file` | project-relative path of the file containing the text |
| `source_doc_file_id` | fileID from the nearest preceding `--- !u!<class> &<fileID>` header; `null` if there is none or it could not be parsed (always `null` for `.meta`) |
| `line`, `column` | 1-based line; 1-based **character** offset of the `guid:` key |
| `guid` | lowercased 32-hex GUID |
| `referenced_path` | the single asset path declaring that GUID, else `null` |
| `referenced_paths` | all asset paths declaring it (`[]` unknown, 2+ ambiguous) |
| `target_file_id` | `fileID:` inside the same `{}` map, if present on the line |
| `property` | immediate YAML key (see below), or `null` |
| `property_via` | how `property` was found: `inline-key`, `list-owner-lookup`, `block-parent-lookup`, or `null` |
| `form` | `flow-map` (`{fileID: .., guid: ..}`), `block-key` (a `guid:` key on its own), `other-text` (anywhere else) |

`who-references` and `references-of` JSON also carry a `query` object; all JSON outputs carry a
`summary` with counts, the list of binary files skipped, unreadable files and all warnings.

## Design decisions on ambiguous cases

* **GUID map**: every `.meta` file is read; the first unindented `guid: <hex>` line is the
  asset's identity. The asset path is the `.meta` path minus `.meta`. Folders are included.
* **`.meta` identity line is not an edge.** It is a definition, not a reference. Every other
  `guid:` in a `.meta` (importer settings such as `externalObjects`) is an edge with the
  `.meta` as source. A `.meta` with two unindented `guid:` lines yields the first as identity
  and the second as an edge.
* **What counts as a reference**: any `guid:` key followed by exactly 32 hex characters
  (uppercase accepted, lowercased in output). Case-sensitive on the key. A reference without
  a `guid` (`{fileID: 123}`) is same-file and is never reported.
* **Prose is not silently dropped.** A `guid: <hex>` that is inside a quoted string, a comment
  or otherwise not in a flow map or block key is still emitted, tagged `form: other-text`,
  with unknown property. The alternative (dropping it) risks a missing edge.
* **Malformed values** (`guid: nothex`, wrong length, quoted) are not edges but produce a
  warning with file and line (except when classified `other-text`, to avoid noise from prose).
* **Document id**: only from an exactly matching header (`--- !u!N &ID`, optional `stripped`;
  negative IDs allowed). An unparseable header resets the id to unknown rather than
  inheriting the previous document's.
* **Property name** is the *immediate* key, not a path.
  - `m_Script: {..guid..}` gives `m_Script`; `- target: {..}` gives `target` (`inline-key`, reliable).
  - A bare list item `- {..guid..}` gives the owning key plus `[]`, e.g. `m_Materials[]`, found by
    indentation lookback (`list-owner-lookup`). If the nearest key at or above the dash's
    indent has an inline value, the property is unknown.
  - A block-style `guid:` key gives its enclosing key (`block-parent-lookup`).
  - Flow sequences (`[{..}, {..}]`) and anything else: unknown.
  - It is deliberately not the semantic meaning: in a prefab modification list, `objectReference`
    is reported as is; the sibling `propertyPath` is not consulted.
* **SKIP_DIRS apply at the project root only** (`Library`, `Temp`, `Obj`, `obj`, `Build`,
  `Builds`, `Logs`, `UserSettings`, `.git`). `Assets/Builds/` is ordinary asset content, so
  nested directories with those names are scanned; the summary lists them. Flip
  `SKIP_DIRS_ROOT_ONLY` to skip at any depth.
* **Binary detection**: NUL byte in the first 8 KiB, or anywhere later while streaming. A late
  NUL discards that whole file's edges (they are buffered per file) so no partial output from
  a binary file leaks. Skipped files are listed in the summary.
* **Ambiguity is explicit**: a GUID declared by two `.meta` files resolves to no single path and
  is reported with all candidates plus a warning.

## Limitations

Read these. Several make the tool look weak; they are real.

**Blind spots (edges that exist in Unity but are not reported)**

1. **Binary assets are invisible.** Unity writes some assets in binary even when the project
   uses text serialization (for example lightmap and terrain data, and anything saved while
   Asset Serialization is set to Force Binary or Mixed). Any file containing a NUL byte is skipped
   with no edges. Their outgoing references are missing from this tool's output. Their incoming
   references are still found (they live in text files). The skipped files are listed.
   UTF-16 text files contain NULs and are therefore also treated as binary.
2. **Only the listed extensions are scanned** (`SCANNABLE_EXTENSIONS`) plus `.meta`. Files with
   other extensions are not read even if they contain GUIDs, including `.asmdef` / `.asmref`
   (`"GUID:..."`, different syntax and case), `.shadergraph`, `.vfx`, `.uxml` / `.uss`
   (`project://database/...guid=...` URLs), `.inputactions`, `.json`, `.cs`, and any custom extension.
   Extend the constant to read more, but the extractor only understands the `guid:` key.
3. **Only the `guid:` key syntax is recognised**: not `GUID:`, not URL query strings, not
   base64/encoded data, not hex split over lines, not GUIDs in strings that are not `guid:`-prefixed.
4. **Path-based and code-based references do not exist for this tool**: `Resources.Load`, Addressables
   address strings, `AssetDatabase.GUIDToAssetPath("...")` in C#, scene names in build settings by
   path, etc.
5. **Library/ is skipped, so package assets are unmapped.** Registry/git packages live in
   `Library/PackageCache` and their `.meta` files are not read. References to them show as
   `<unknown path>`. Embedded packages under `Packages/` are fine. Unity built-in resources
   (e.g. `0000000000000000f000000000000000`, `...e000...`, `...d000...`) have no `.meta` and are
   always unknown paths.
6. **Skipped names at the root only** (see above): a project that really keeps assets under a root
   folder named `Build` or `Logs` would lose them; flip `SKIP_DIRS_ROOT_ONLY` or edit `SKIP_DIRS`.
7. **Symlinked directories are not followed** (a warning is emitted). Symlinked files are read.
8. **Disk, not editor state.** Unsaved scenes, in-memory edits and anything not yet written are not seen.
   Unity ignores files and folders starting with `.` or ending with `~`, and some others; this
   tool does not, so it may report edges from files Unity itself would ignore (they usually lack
   a `.meta`, so are only scanned if they carry a scannable extension).

**Possible wrong or imprecise facts**

9. **Path resolution comes from the `.meta` filename**, not from checking the asset exists. An
   orphaned `.meta` (asset deleted) still maps its GUID to the vanished path. Duplicate GUIDs
   (copy/paste of `.meta` files, merge damage) resolve to "ambiguous".
10. **False positives are possible**: a `guid: <32hex>` inside a string is emitted as
    `other-text`; one inside a quoted string that also happens to look like `{...,` can be
    classified `flow-map`, because bracket counting ignores quoting.
11. **Property names are heuristic for list items and block keys.** The lookback is based on
    indentation and the previous key lines; multi-line or folded scalars, block scalars (`|`, `>`)
    whose continuation lines look like `key:`, odd indentation or hand-edited YAML can make it
    wrong. Only `inline-key` is read straight from the same line. `property_via` tells you which one
    you have. Keys containing spaces or quotes are not recognised as keys.
12. **`target_file_id`** is the first `fileID:` inside the same braces on the same line. Maps spanning
    several lines are not supported (Unity does not write them), so it would be null there.
13. **Document fileID** is only as good as the header; `.meta` files have none; a stray `---` line
    resets it to unknown.
14. **Columns** are character offsets of the decoded line (invalid UTF-8 is replaced with U+FFFD, so
    byte offsets can differ). Lines are split on `\n` only. Files that use bare `\r` line endings
    are read as one line (a warning is emitted) and line numbers will be wrong.
15. Raw facts only: **no semantics**. The tool will not tell you that a variant overrides a property,
    that a prefab instance's `m_SourcePrefab` is a "parent", or that a stripped object is a
    placeholder. Interpreting those is the job of whoever uses this output.

**Operational**

16. **Pure Python is slow**, and each invocation rescans: there is no cache or index file (it must
    not write inside the project). `references-of` is cheaper because it only parses the one
    file, after reading every `.meta`. See the timing note below.

    Timing note (one measurement, Python 3.13, macOS, local SSD): a generated 4.9 GB / 4,000-file
    project (2,000 `.meta` + 2,000 prefabs, 8,000,000 edges) took 6m15s for `dump` (about 13 MB/s)
    with ~23 MB resident memory. Real projects with fewer GUIDs per MB will be faster; this is a
    single data point, not a benchmark.

    Real-project measurement (same machine): the AutoDRIVE Simulator project (Unity 2022.3.52f1,
    2,489 text assets plus 7,494 `.meta`, 1.6 GB scanned, largest scene 171 MB) took 35s for
    `dump`, about 46 MB/s, producing 1,884,501 edges.
17. **A single very long line is read into memory whole** (for example a huge serialized hex string in
    an `.asset`). Peak memory is roughly the longest line, not the file size. A file with a
    multi-gigabyte single line without NULs would be a problem.
18. Reading files updates access times on filesystems that track them. Nothing is created, modified,
    moved or deleted inside the project.
19. **Run against a real project, but never checked against Unity's own answers.** Beyond the
    synthetic fixtures, it has been run over the AutoDRIVE Simulator project (Unity 2022.3.52f1,
    1.6 GB of text assets): 1,884,501 edges, 98 binary files skipped, zero warnings and zero
    unreadable files. That shows the parser does not choke on real 2022.3 output. It does **not**
    show the edge list is correct — nothing here has been compared with what Unity itself
    reports. Treat its results with the same suspicion you apply to the tool you are checking.

## Extending

* More file types: add to `SCANNABLE_EXTENSIONS` (compared case-insensitively).
* More skipped folders: add to `SKIP_DIRS`; set `SKIP_DIRS_ROOT_ONLY = False` for any depth.

## Tests

```
python3 test_reference_scan.py
```

No pytest or third-party packages. It builds a synthetic project in a temp directory and removes it afterwards.
