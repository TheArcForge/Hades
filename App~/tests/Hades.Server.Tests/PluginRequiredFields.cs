namespace Hades.Server.Tests;

using System.Text.RegularExpressions;

/// <summary>
/// Mines Plugin~/Assets/Hades/Tools/*.cs, at test-run time, for every <c>JsonParams.RequireString</c>/
/// <c>JsonParams.RequireInt</c> call - the plugin's OWN required-field check (see
/// <c>Hades.Tools.JsonParams</c>, defined in SceneCommands.cs: <c>RequireString(@params, key,
/// context)</c> throws <c>"'" + context + "' requires a non-empty string '" + key + "' parameter."</c>
/// when 'key' is missing/blank) - and groups the field names each call requires by its own
/// 'context' argument (e.g. "material.set_property" -&gt; ["materialPath", "propertyName"]).
///
/// <para><b>Why parse instead of hand-copy.</b> A hand-copied table of "wire method -&gt; required
/// fields" is exactly as stale as the last time someone remembered to update it after editing
/// Plugin~ - which is how the live defect this mechanism exists to prevent happened in the first
/// place (an app-side field name silently stopped matching a plugin-side rename). Reading the
/// requirement straight from the plugin's own RequireString/RequireInt call, every time the test
/// suite runs, means a plugin-side rename changes this table automatically - no test-project edit
/// required - so the very next test that exercises the affected op fails immediately, instead of
/// only surfacing in a live Editor session. See PluginWireContract.cs for the other half (which
/// context(s) each consolidated tool's op maps to) and PluginWireContractTests.cs for direct proof
/// this mechanism reacts to a rename on either side.</para>
/// </summary>
internal static class PluginRequiredFields
{
    static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> RealPluginTable = new(() =>
    {
        var toolsDir = FindPluginToolsDirectory() ?? throw new InvalidOperationException(
            "Could not locate Plugin~/Assets/Hades/Tools by walking up from " + AppContext.BaseDirectory
            + " - PluginWireContract's fake-Unity enforcement (EditorToolTestBase) and "
            + "PluginWireContractTests.cs both depend on it. Is Plugin~ present alongside App~ in this "
            + "checkout?");
        return Parse(toolsDir);
    });

    /// <summary>Every context found in the REAL Plugin~ source, right now, mapped to the field
    /// names JsonParams.RequireString/RequireInt enforce for it. Parsed once per test run (Lazy),
    /// not once per call.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByContext => RealPluginTable.Value;

    /// <summary>The required fields for <paramref name="context"/> - throws immediately, with an
    /// actionable message, rather than returning an empty list, when the context itself is not
    /// found. An empty list would silently turn "the plugin renamed this whole context string" into
    /// "this op now requires nothing", which is the OPPOSITE of what this mechanism exists to
    /// catch - a loud failure here means either a genuine plugin-side rename (fix the structural map
    /// in PluginWireContract.cs) or this parser needs to learn a new source pattern.</summary>
    public static IReadOnlyList<string> RequiredFieldsFor(string context)
    {
        if (ByContext.TryGetValue(context, out var fields)) return fields;

        throw new InvalidOperationException(
            $"No JsonParams.RequireString/RequireInt call was found in Plugin~/Assets/Hades/Tools/*.cs "
            + $"with context '{context}'. Either the plugin renamed/removed this context string (update "
            + "PluginWireContract.cs's structural map to match), or PluginRequiredFields.cs's parser "
            + "needs updating for a new source pattern. Known contexts: "
            + string.Join(", ", ByContext.Keys.OrderBy(k => k, StringComparer.Ordinal)));
    }

    /// <summary>Walks up from the test assembly's own output directory looking for a directory that
    /// has BOTH an 'App~' sibling and a 'Plugin~/Assets/Hades/Tools' sibling - i.e. the repo root -
    /// rather than assuming a fixed number of parent hops, which would break the moment the build
    /// output nesting changes (Debug/Release, target framework moniker, a test runner copying
    /// output elsewhere, ...).</summary>
    internal static string? FindPluginToolsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Plugin~", "Assets", "Hades", "Tools");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "App~")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // 'op'/'@params' etc - a bare identifier, optionally '@'-prefixed. Every RequireString/RequireInt
    // call in Plugin~/Tools passes a simple local variable here, never a nested expression.
    static readonly Regex RequireCallPattern = new(
        @"JsonParams\.(?:RequireString|RequireInt)\(\s*[A-Za-z@_][A-Za-z0-9_]*\s*,\s*""(?<field>[^""]+)""\s*,\s*(?:""(?<literalCtx>[^""]*)""|(?<ctxVar>[A-Za-z_][A-Za-z0-9_]*))\s*\)",
        RegexOptions.Compiled);

    // Every 'ctx'-variable indirection in Plugin~/Tools follows this exact local-const shape
    // (SceneApplyCommands.cs's own per-op DoXxx methods each declare their own, right before using
    // it) - see this class's own doc comment for why a full C# parser is not needed here.
    static readonly Regex CtxDeclPattern = new(
        @"const\s+string\s+ctx\s*=\s*""(?<value>[^""]*)""\s*;",
        RegexOptions.Compiled);

    /// <summary>Core parser, taking an explicit directory rather than always resolving the real
    /// Plugin~ - so a test can point it at a small, hand-written scratch fixture to prove the parser
    /// reacts to a rename, without ever touching the real plugin source (see
    /// PluginWireContractTests.cs).</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string toolsDir)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(toolsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(file);

            var ctxDecls = CtxDeclPattern.Matches(text)
                .Select(m => (Index: m.Index, Value: m.Groups["value"].Value))
                .OrderBy(x => x.Index)
                .ToList();

            string? ResolveCtxAt(int index)
            {
                string? best = null;
                foreach (var decl in ctxDecls)
                {
                    if (decl.Index > index) break;
                    best = decl.Value;
                }
                return best;
            }

            foreach (Match m in RequireCallPattern.Matches(text))
            {
                var field = m.Groups["field"].Value;
                var context = m.Groups["literalCtx"].Success ? m.Groups["literalCtx"].Value : ResolveCtxAt(m.Index);

                if (string.IsNullOrEmpty(context))
                {
                    throw new InvalidOperationException(
                        $"{file}: found JsonParams.RequireString/RequireInt(..., \"{field}\", ctx) with no "
                        + "preceding 'const string ctx = \"...\";' in scope - PluginRequiredFields.cs's "
                        + "parser cannot resolve its context (offset " + m.Index + ").");
                }

                if (!result.TryGetValue(context, out var list)) result[context] = list = new List<string>();
                if (!list.Contains(field)) list.Add(field);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }
}
