using System.Text.Json;
using System.Text.RegularExpressions;
using Hades.Core.Indexing;

namespace Hades.Core.Projects;

/// <summary>
/// The C# preprocessor define set <see cref="Resolve"/> reconstructs for a project — what
/// <see cref="Scanning.RoslynScriptScanner"/> needs to evaluate <c>#if</c> the way Unity's own
/// compiler would, instead of the Plan 15 Task 3 defect: no <c>CSharpParseOptions</c> at all,
/// which makes Roslyn treat every <c>#if</c> as false and silently drop the guarded code from the
/// graph (repro: project_aurora's MathematicsDrawers.cs, 64 declarations behind one <c>#if
/// UNITY_EDITOR</c>).
///
/// <para><b>The shortcut this deliberately does NOT take.</b> Stripping <c>#if</c>/<c>#else</c>/
/// <c>#endif</c> so every branch lands in the graph is trivial and maximises coverage, but it is
/// wrong: an <c>#if A</c> / <c>#else</c> pair would then both exist in the graph, producing a
/// declaration true of no real compile. Hades reports exact reference counts and people act on
/// them ("what breaks if I delete this") — phantom declarations would trade away the property a
/// real ground-truth validation run proved exact (12/12 hand-verified reference counts). This
/// class reconstructs the REAL define set instead, from three cheap, deterministic sources.</para>
///
/// <para><b>Four layers, cheapest and most certain first:</b>
/// <list type="number">
/// <item><description><c>UNITY_EDITOR</c> — unconditional. Hades only ever runs as an
/// editor-time indexer, so this holds for everything it indexes, always.</description></item>
/// <item><description>The version ladder (<see cref="VersionLadder"/>), mechanically derived from
/// <see cref="ProjectIdentity.TryReadUnityVersion"/> — see that method's own doc comment for where
/// the version string comes from (ProjectSettings/ProjectVersion.txt, written by Unity itself,
/// regardless of asset serialization mode).</description></item>
/// <item><description><c>scriptingDefineSymbols</c> from ProjectSettings.asset — user-authored,
/// committed, often load-bearing for the project's OWN code (see this class's own
/// <see cref="ScriptingDefineSymbolsBuildTarget"/> for which of Unity's several per-build-target
/// lists is read, and why).</description></item>
/// <item><description>
/// Plan 15 Task 4: every <c>versionDefines</c> entry, from every asmdef every scan root reaches
/// (<see cref="ResolveVersionDefines"/>), whose named package resolves to a version satisfying its
/// own <c>expression</c> (<see cref="VersionDefineSatisfied"/>) — Unity's per-package/module
/// conditional-compilation mechanism (docs.unity3d.com/6000.3/Documentation/Manual/
/// assembly-definition-includes.html, checked 2026-08-07), and the "insidious half" of the Plan 15
/// defect: not Editor-only code, but code gated on a genuinely INSTALLED optional dependency.
/// Concrete, live repro: project_aurora's <c>Packages/com.arongranberg.astar/
/// AstarPathfindingProject.asmdef</c> carries <c>{"name":"com.unity.entities","expression":
/// "1.0.0-pre.47","define":"MODULE_ENTITIES"}</c>, and that project's <c>Packages/manifest.json</c>
/// declares <c>"com.unity.entities":"1.4.2"</c> — MODULE_ENTITIES is genuinely defined in real
/// compiles, and <c>Core/ECS/Components/AutoRepathPolicy.cs</c> (entirely <c>#if MODULE_ENTITIES</c>)
/// was 100% invisible before this layer existed.</description></item>
/// </list></para>
///
/// <para><b>The caveat that must survive to a user, not just this comment.</b> Unity's real
/// compiler applies a DIFFERENT define set per assembly (each asmdef can carry its own
/// <c>defineConstraints</c>/<c>versionDefines</c>) — Hades does not currently track which files
/// belong to which asmdef, so every symbol resolved here (Layer 4's versionDefines included) is
/// unioned across the WHOLE project. A symbol true for one assembly but not another is, from this
/// reconstruction's point of view, true everywhere — an over-inclusive approximation, not an exact
/// per-assembly match. Layer 4 makes this concretely WIDER than layers 1-3: a project-wide union of
/// scriptingDefineSymbols already over-includes a little, but versionDefines are declared per-asmdef
/// by design (a package's OWN gate on ANOTHER package's version), so unioning them project-wide
/// over-includes more. For a navigation index that trade is still the right one (a superset beats a
/// silent gap — the same reasoning <c>find_orphan_scripts</c> already uses), but it is a real
/// deviation from what the compiler sees, which is why
/// <see cref="Hades.Core.ProjectSummary.AppliedDefines"/> reports the resolved set explicitly
/// rather than leaving it invisible: a gap a caller can see is a different thing from one it
/// cannot.</para>
///
/// <para><b>Deliberately out of scope.</b> Platform symbols (<c>UNITY_ANDROID</c>,
/// <c>UNITY_IOS</c>, <c>UNITY_WEBGL</c>, ...) are combinatorial, drift every Unity release, and buy
/// little for a navigation index — Hades applies none of them. Generated <c>.csproj</c>
/// <c>&lt;DefineConstants&gt;</c> are separate, later work. Within Layer 4 specifically: a
/// <c>versionDefines</c> entry keyed to a BUILT-IN Unity module (<see cref="IsBuiltinModuleName"/> —
/// e.g. <c>com.unity.modules.physics</c>) is never resolved, because Unity compares a built-in
/// module's version against the EDITOR's own version, not the meaningless "1.0.0" placeholder every
/// built-in module carries in manifest.json/packages-lock.json — implementing that correctly needs
/// more certainty than this increment has established. And <see cref="VersionDefineSatisfied"/>'s
/// own doc comment lists exactly which <c>expression</c> grammar forms are, and are not,
/// supported.</para>
/// </summary>
public static partial class ProjectDefines
{
    /// <summary>
    /// Always applied, unconditionally — see this class's own doc comment, layer 1.
    /// </summary>
    public const string UnityEditorSymbol = "UNITY_EDITOR";

    /// <summary>
    /// <c>scriptingDefineSymbols</c> is keyed per build-target group in ProjectSettings.asset
    /// (e.g. <c>Standalone</c>, <c>Android</c>, <c>iPhone</c>) — a project can define different
    /// symbols for each. Hades reads only <c>Standalone</c>: the same "pick the editor's own
    /// platform and stop" principle Plan 15 Task 3 applies to platform symbols generally (see this
    /// class's own doc comment) applies here too. Hades always RUNS as a desktop process
    /// alongside the Unity Editor (macOS, Windows, or Linux) regardless of which platform the
    /// PROJECT ships to, and "Standalone" is the build-target-group name Unity itself has used for
    /// the desktop group since this key's introduction — confirmed against both project_aurora and
    /// Hades-Unity-Client's real ProjectSettings.asset (6000.3.2f1, 2026-08-07), where this is the
    /// only key present in either file. Reading whichever platform the project happens to have
    /// last built for (Android, iOS, ...) would be an arbitrary, unstable choice with no
    /// relationship to what Hades itself is doing.
    /// </summary>
    public const string ScriptingDefineSymbolsBuildTarget = "Standalone";

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// The full define set for the project rooted at <paramref name="projectRoot"/> — reads
    /// ProjectVersion.txt and ProjectSettings.asset fresh off disk every call (both are small,
    /// project-level files, never the source corpus this reconstruction is applied to), so the
    /// reported set always reflects the last SAVED state, the same convention every other
    /// read-through fact in Hades follows. Never throws: a missing or unreadable file at either
    /// layer just means that layer contributes nothing, exactly like
    /// <see cref="ProjectIdentity.TryReadProductGuid"/>'s own graceful-degradation posture.
    /// </summary>
    public static DefineSet Resolve(string projectRoot)
    {
        var symbols = new SortedSet<string>(StringComparer.Ordinal) { UnityEditorSymbol };

        var unityVersion = ProjectIdentity.TryReadUnityVersion(projectRoot);
        if (unityVersion is not null)
        {
            foreach (var symbol in VersionLadder(unityVersion)) symbols.Add(symbol);
        }

        var userDefines = TryReadScriptingDefineSymbols(projectRoot, ScriptingDefineSymbolsBuildTarget);
        if (userDefines is not null)
        {
            foreach (var symbol in userDefines) symbols.Add(symbol);
        }

        foreach (var symbol in ResolveVersionDefines(projectRoot)) symbols.Add(symbol);

        return new DefineSet
        {
            Symbols = symbols.ToList(),
            UnityVersion = unityVersion,
            ScriptingDefineSymbolsTarget = userDefines is not null ? ScriptingDefineSymbolsBuildTarget : null,
        };
    }

    /// <summary>
    /// Unity's own documented version-symbol format (docs.unity3d.com/6000.3/Documentation/Manual/
    /// scripting-symbol-reference.html, checked 2026-08-07 rather than inferred from one example):
    /// given a version <c>X.Y.Z</c> (the doc's own worked example for a Unity 6 version is
    /// "6000.0.33"), Unity exposes <c>UNITY_X</c> ("release version"), <c>UNITY_X_Y</c> ("major
    /// version"), and <c>UNITY_X_Y_Z</c> ("minor version"), plus one <c>#define</c> in the format
    /// <c>UNITY_X_Y_OR_NEWER</c>. The algorithm is scheme-agnostic — X is whatever leading integer
    /// the version string carries, "6000" for Unity 6 or "2022" for the year-based scheme, never
    /// specially abbreviated (Unity 6 is NOT "UNITY_6"; it is "UNITY_6000").
    ///
    /// <para><b>The one place this class does not chase the full historical ladder.</b> Unity's
    /// docs do not specify whether <c>UNITY_X_Y_OR_NEWER</c> is retroactively defined for every
    /// EARLIER major version too (running 6000.3 presumably also defines
    /// <c>UNITY_2023_1_OR_NEWER</c> on a real Editor) — and reconstructing that would need a
    /// hardcoded table of every major.minor Unity has ever shipped, which is external release
    /// history, not something that "derives mechanically" from one ProjectVersion.txt string the
    /// way this whole task is scoped. What IS mechanical, and is exactly what this method builds,
    /// is the descending chain WITHIN the current major version: <c>UNITY_X_0_OR_NEWER</c> through
    /// <c>UNITY_X_Y_OR_NEWER</c>. Code gated on an OR_NEWER symbol from a PRIOR major era is a real,
    /// stated gap — see this class's own caveat section — not a silent one: it is exactly the kind
    /// of thing <see cref="Hades.Core.ProjectSummary.AppliedDefines"/> exists to make visible.</para>
    /// </summary>
    public static IReadOnlyList<string> VersionLadder(string editorVersion)
    {
        var match = VersionPattern().Match(editorVersion);
        if (!match.Success) return [];

        var major = match.Groups[1].Value;
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Value;

        var symbols = new List<string>
        {
            $"UNITY_{major}",
            $"UNITY_{major}_{minor}",
            $"UNITY_{major}_{minor}_{patch}",
        };

        for (var y = 0; y <= minor; y++) symbols.Add($"UNITY_{major}_{y}_OR_NEWER");

        return symbols;
    }

    /// <summary>
    /// Reads <c>scriptingDefineSymbols</c>'s <paramref name="buildTarget"/> entry straight off
    /// ProjectSettings.asset with a small line-indent scan rather than a general YAML parse: the
    /// value is always one flat <c>;</c>-joined scalar (never nested structure, quoting, or
    /// anchors), and — unlike <see cref="ProjectIdentity.TryReadProductGuid"/>, which only reads
    /// the file's head — this key can sit well past the first 8&#160;KB of a real project's
    /// PlayerSettings block (line 590 of project_aurora's own 20&#160;KB file), so the whole file
    /// is read. Still cheap: this file is a small, fixed, project-level settings document, never
    /// the multi-hundred-megabyte source corpus indexing itself walks. Returns null when the file
    /// does not exist, is unreadable, or the map has no entry for <paramref name="buildTarget"/> at
    /// all — including an ordinary empty map, <c>scriptingDefineSymbols: {}</c> (the real shape of
    /// Hades-Unity-Client's own ProjectSettings.asset). Returns an empty (non-null) list only for
    /// the rare case where the target's own entry exists but names nothing (<c>Standalone:</c> with
    /// nothing after the colon) — both outcomes mean "apply no extra symbols from this layer", but
    /// only null is reported as "target not found" via <see cref="DefineSet.ScriptingDefineSymbolsTarget"/>.
    /// </summary>
    static IReadOnlyList<string>? TryReadScriptingDefineSymbols(string projectRoot, string buildTarget)
    {
        var path = ProjectIdentity.SettingsPath(projectRoot);
        if (!File.Exists(path)) return null;

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        const string blockKey = "scriptingDefineSymbols:";
        var lines = content.Split('\n');
        var blockIndent = -1;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart(' ', '\t');
            var indent = line.Length - trimmed.Length;

            if (blockIndent < 0)
            {
                if (trimmed.StartsWith(blockKey, StringComparison.Ordinal)) blockIndent = indent;
                continue;
            }

            // A line back at (or before) scriptingDefineSymbols' own indentation ends its block —
            // whether that is the very next line (an empty "{}" map) or several target entries in.
            if (indent <= blockIndent) return null;

            var targetKey = buildTarget + ":";
            if (!trimmed.StartsWith(targetKey, StringComparison.Ordinal)) continue;

            var value = trimmed[targetKey.Length..].Trim();
            return value.Length == 0
                ? []
                : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return null;
    }

    // --- Layer 4: versionDefines (Plan 15 Task 4) ----------------------------------------------
    // See this class's own doc comment for the full picture (what this layer is, the concrete
    // AutoRepathPolicy/MODULE_ENTITIES repro, and why it widens the per-assembly-union caveat).

    /// <summary>One raw <c>versionDefines</c> entry as an asmdef declares it.</summary>
    readonly record struct VersionDefineEntry(string Name, string Expression, string Define);

    /// <summary>
    /// A parsed <c>MAJOR.MINOR.PATCH[-LABEL]</c> version designator — Unity's own format for both
    /// installed-package versions and the bounds inside a <c>versionDefines</c> expression
    /// (docs.unity3d.com/6000.3/Documentation/Manual/assembly-definition-includes.html, checked
    /// 2026-08-07): "Package and module version designators have four parts, following the
    /// Semantic Versioning format: MAJOR.MINOR.PATCH-LABEL." <see cref="Minor"/> is required —
    /// Unity's own docs: "you must use at least the major and minor components of a version in an
    /// expression" — <see cref="Patch"/> defaults to 0 when absent: lenient rather than a strict
    /// validator, since <see cref="TryParseVersion"/> only ever wants a defined comparison, never a
    /// rejection.
    /// </summary>
    readonly record struct SemVer(int Major, int Minor, int Patch, string? Label);

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?(?:-(.+))?$")]
    private static partial Regex PackageVersionPattern();

    static SemVer? TryParseVersion(string text)
    {
        var match = PackageVersionPattern().Match(text.Trim());
        if (!match.Success) return null;

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        var label = match.Groups[4].Success ? match.Groups[4].Value : null;

        return new SemVer(major, minor, patch, label);
    }

    /// <summary>
    /// SemVer precedence for the numeric MAJOR.MINOR.PATCH triple, then: a release (no label)
    /// outranks any pre-release label at the same triple (semver.org #11.4 — confirmed against
    /// Unity's own worked example, <c>"(0.2.4,5.6.2-preview.2]"</c> evaluating to
    /// <c>"0.2.4 &lt; x &lt;= 5.6.2-preview.2"</c>, which only makes sense if the release 5.6.2
    /// sorts ABOVE 5.6.2-preview.2 and so EXCEEDS that inclusive upper bound). Two DIFFERENT
    /// labels fall back to plain ordinal string comparison — a deliberate simplification: Unity's
    /// docs do not specify a comparison algorithm for two differently-labelled versions at the
    /// same numeric triple, and no real fixture this project validates against needs one (every
    /// real <c>versionDefines</c> entry found in project_aurora only ever needs the numeric
    /// triple). This means two labels that differ only in a trailing number can compare
    /// "backwards" of numeric truth — e.g. "preview.9" sorts AFTER "preview.10" ordinally — a
    /// known, accepted gap, not a claim of full generic SemVer pre-release precedence.
    /// </summary>
    static int CompareVersions(SemVer a, SemVer b)
    {
        var byMajor = a.Major.CompareTo(b.Major);
        if (byMajor != 0) return byMajor;

        var byMinor = a.Minor.CompareTo(b.Minor);
        if (byMinor != 0) return byMinor;

        var byPatch = a.Patch.CompareTo(b.Patch);
        if (byPatch != 0) return byPatch;

        if (a.Label is null && b.Label is null) return 0;
        if (a.Label is null) return 1;
        if (b.Label is null) return -1;
        return string.CompareOrdinal(a.Label, b.Label);
    }

    /// <summary>
    /// True when <paramref name="installedVersion"/> satisfies a <c>versionDefines</c>
    /// <paramref name="expression"/>, per Unity's own documented grammar (docs.unity3d.com/6000.3/
    /// Documentation/Manual/assembly-definition-includes.html, checked 2026-08-07; see
    /// <see cref="SemVer"/>'s own doc comment for the version-designator format it references).
    ///
    /// <para><b>Forms supported</b>, each confirmed against Unity's own worked examples: empty
    /// (any version — the caller has already established the resource is installed at all, by
    /// resolving SOME raw version string for it, before this method is even reached); a
    /// bracketed/parenthesised range with independent inclusive <c>[</c> / exclusive <c>(</c>
    /// bounds on each side, e.g. <c>[1.3,3.4.1]</c>, <c>(1.3.0,3.4)</c>, <c>[1.1,3.4)</c>; a
    /// single bracketed version meaning exact equality, e.g. <c>[2.4.5]</c>; and a bare version
    /// with no brackets — a shortcut for "this version or later" — e.g.
    /// <c>2.1.0-preview.7</c>.</para>
    ///
    /// <para><b>Forms NOT supported</b> — never satisfied, rather than guessed at: wildcard
    /// characters (Unity's own docs: "No wildcard characters are supported"); comparison-operator
    /// syntax like <c>&gt;=1.2.3</c> (not part of Unity's grammar, which is interval notation
    /// only); a one-sided open range with an explicit empty bound, e.g. <c>[1.5,)</c>
    /// (undocumented, and not observed in any real asmdef this project checked against); and a
    /// single EXCLUSIVE bracket/paren around one version, e.g. <c>(2.4.5)</c> (Unity's docs give a
    /// meaning only to the INCLUSIVE <c>[x]</c> form). See <see cref="CompareVersions"/>'s own doc
    /// comment for the one supported-but-simplified case: two DIFFERENT pre-release labels at the
    /// same numeric triple.</para>
    /// </summary>
    public static bool VersionDefineSatisfied(string expression, string installedVersion)
    {
        var trimmed = expression.Trim();
        if (trimmed.Length == 0) return true;

        var isBracketed = trimmed.Length >= 2
            && (trimmed[0] == '[' || trimmed[0] == '(')
            && (trimmed[^1] == ']' || trimmed[^1] == ')');

        if (isBracketed) return RangeSatisfied(trimmed, installedVersion);

        // Bare version, no brackets — shortcut for ">= this version".
        if (TryParseVersion(trimmed) is not { } bare) return false;
        if (TryParseVersion(installedVersion) is not { } installed) return false;
        return CompareVersions(installed, bare) >= 0;
    }

    static bool RangeSatisfied(string bracketed, string installedVersion)
    {
        if (TryParseVersion(installedVersion) is not { } installed) return false;

        var lowInclusive = bracketed[0] == '[';
        var highInclusive = bracketed[^1] == ']';
        var parts = bracketed[1..^1].Split(',');

        if (parts.Length == 1)
        {
            // "[x]" — exact version. Unity's docs give only the inclusive form a meaning
            // ("x = 2.4.5"); an exclusive single-value form has none documented, so it is
            // unsupported rather than guessed at.
            if (!lowInclusive || !highInclusive) return false;
            return TryParseVersion(parts[0]) is { } exact && CompareVersions(installed, exact) == 0;
        }

        if (parts.Length != 2) return false;   // malformed — more than one comma

        if (parts[0].Length > 0)
        {
            if (TryParseVersion(parts[0]) is not { } low) return false;
            var cmp = CompareVersions(installed, low);
            if (cmp < 0 || (cmp == 0 && !lowInclusive)) return false;
        }

        if (parts[1].Length > 0)
        {
            if (TryParseVersion(parts[1]) is not { } high) return false;
            var cmp = CompareVersions(installed, high);
            if (cmp > 0 || (cmp == 0 && !highInclusive)) return false;
        }

        return true;
    }

    /// <summary>
    /// Unity's own naming convention for a built-in module (Physics, UI, IMGUI, ...): always
    /// declared with a fixed, meaningless "1.0.0" placeholder version in BOTH manifest.json and
    /// packages-lock.json — confirmed against project_aurora's own real manifest.json
    /// (2026-08-07: <c>"com.unity.modules.physics": "1.0.0"</c>, alongside every other built-in
    /// module, while a genuinely-versioned built-in package like com.unity.test-framework carries
    /// its real "1.6.0"). Unity's REAL versionDefines resolution compares a built-in module's
    /// version against the EDITOR's own version, not this placeholder — deliberately NOT
    /// implemented here (see this file's own class doc comment) — so a name in this shape is
    /// always treated as unresolvable, never as "installed at 1.0.0", which could otherwise
    /// produce a define no real compile sets.
    /// </summary>
    static bool IsBuiltinModuleName(string name) => name.StartsWith("com.unity.modules.", StringComparison.Ordinal);

    /// <summary>
    /// Every package this project has installed, name to raw version string exactly as
    /// manifest.json/packages-lock.json record it (a git or file: URL included — this method does
    /// no version parsing at all; <see cref="VersionDefineSatisfied"/> decides what it can use).
    ///
    /// <para><b>Reads packages-lock.json AND manifest.json, not manifest.json alone — a
    /// deliberate widening past this task's own literal brief.</b> <c>packages-lock.json</c> is
    /// Unity's own record of what actually got resolved and is genuinely sitting in
    /// Library/PackageCache — the closer of the two to "what the compiler used", especially if
    /// manifest.json was hand-edited since the last resolve. It is PREFERRED; manifest.json's
    /// direct dependency list only fills names the lock does not have (or when no lock file
    /// exists at all, e.g. a fresh clone never opened in the Editor). Concrete evidence this
    /// matters, found in project_aurora itself (2026-08-07): Packages/manifest.json never lists
    /// com.unity.burst, com.unity.mathematics, or com.unity.collections directly — all three are
    /// pulled in TRANSITIVELY by com.unity.entities and appear only in packages-lock.json (at
    /// <c>"depth":1</c>). Manifest-only resolution would silently miss every versionDefines entry
    /// keyed to any of them — concretely, the astar package's own MODULE_BURST/MODULE_MATHEMATICS/
    /// MODULE_COLLECTIONS, and its VersionedMonoBehaviour.cs base-type choice (
    /// <c>Drawing.MonoBehaviourGizmos</c> vs plain <c>MonoBehaviour</c>), which is gated on all
    /// three at once via <c>#if MODULE_BURST &amp;&amp; MODULE_MATHEMATICS &amp;&amp;
    /// MODULE_COLLECTIONS</c>.</para>
    ///
    /// <para>Built-in modules are excluded from both sources — see
    /// <see cref="IsBuiltinModuleName"/>.</para>
    /// </summary>
    static IReadOnlyDictionary<string, string> ReadInstalledPackageVersions(string projectRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, version) in ReadLockedDependencies(projectRoot))
        {
            if (!IsBuiltinModuleName(name)) result[name] = version;
        }

        foreach (var (name, version) in ReadManifestDependencies(projectRoot))
        {
            if (!IsBuiltinModuleName(name)) result.TryAdd(name, version);
        }

        return result;
    }

    static IReadOnlyList<(string Name, string Version)> ReadManifestDependencies(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(path)) return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<(string, string)>();
            foreach (var property in deps.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    result.Add((property.Name, property.Value.GetString() ?? ""));
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static IReadOnlyList<(string Name, string Version)> ReadLockedDependencies(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "Packages", "packages-lock.json");
        if (!File.Exists(path)) return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<(string, string)>();
            foreach (var property in deps.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("version", out var versionElement)
                    && versionElement.ValueKind == JsonValueKind.String)
                {
                    result.Add((property.Name, versionElement.GetString() ?? ""));
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every symbol this project's asmdef <c>versionDefines</c> resolve to, project-wide. Walks
    /// the SAME scan roots the C# corpus itself is walked through
    /// (<see cref="Indexing.ProjectWalker.ResolveScanRoots"/>) — so the same local "file:"
    /// packages and the same exclusions apply: a registry package resolved into
    /// Library/PackageCache is never scanned for .cs either, so its asmdef is correctly never
    /// reached here. A throwaway warnings list is passed through: this call's own warnings would
    /// only duplicate ones <see cref="Indexing.ScriptIndexer"/> already surfaces from its own
    /// identical call, and <see cref="DefineSet"/> has never reported warnings from any of its
    /// other layers either.
    /// </summary>
    static IReadOnlyList<string> ResolveVersionDefines(string projectRoot)
    {
        var installed = ReadInstalledPackageVersions(projectRoot);
        if (installed.Count == 0) return [];

        var symbols = new List<string>();
        var warnings = new List<string>();

        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            foreach (var asmdefPath in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.asmdef"))
            {
                foreach (var entry in TryReadVersionDefines(asmdefPath))
                {
                    if (!installed.TryGetValue(entry.Name, out var installedVersion)) continue;
                    if (VersionDefineSatisfied(entry.Expression, installedVersion)) symbols.Add(entry.Define);
                }
            }
        }

        return symbols;
    }

    /// <summary>
    /// One asmdef's <c>versionDefines</c> array, read defensively — malformed JSON, an I/O
    /// failure, a missing/wrong-shaped <c>versionDefines</c> key, or any entry missing a
    /// non-empty <c>name</c>/<c>define</c> (an empty or absent <c>expression</c> is valid — see
    /// <see cref="VersionDefineSatisfied"/>) all degrade to contributing nothing, matching every
    /// other read in this class's "never throws" posture.
    /// </summary>
    static IReadOnlyList<VersionDefineEntry> TryReadVersionDefines(string asmdefPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(asmdefPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("versionDefines", out var array) || array.ValueKind != JsonValueKind.Array)
                return [];

            var entries = new List<VersionDefineEntry>();
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                if (!element.TryGetProperty("expression", out var exprEl) || exprEl.ValueKind != JsonValueKind.String) continue;
                if (!element.TryGetProperty("define", out var defineEl) || defineEl.ValueKind != JsonValueKind.String) continue;

                var name = nameEl.GetString();
                var define = defineEl.GetString();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(define)) continue;

                entries.Add(new VersionDefineEntry(name, exprEl.GetString() ?? "", define));
            }
            return entries;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>One project's resolved define set — see <see cref="ProjectDefines.Resolve"/>.</summary>
public sealed record DefineSet
{
    /// <summary>Every symbol applied, sorted (ordinal) and deduplicated.</summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    /// <summary>The Editor version the ladder was derived from, or null when
    /// ProjectVersion.txt was missing, unreadable, or did not parse — in which case
    /// <see cref="Symbols"/> still contains <see cref="ProjectDefines.UnityEditorSymbol"/> and any
    /// scriptingDefineSymbols, just no version ladder.</summary>
    public string? UnityVersion { get; init; }

    /// <summary>
    /// <see cref="ProjectDefines.ScriptingDefineSymbolsBuildTarget"/> when ProjectSettings.asset
    /// had an entry for it (even an empty one), or null when the file was missing, unreadable, or
    /// had no such entry at all.
    /// </summary>
    public string? ScriptingDefineSymbolsTarget { get; init; }
}
