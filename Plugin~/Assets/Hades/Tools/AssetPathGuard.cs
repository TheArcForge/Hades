// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// Shared write-path validation for every command that creates or overwrites an asset at a
    /// caller-supplied path - material.create, scene.create/duplicate, prefab.create/create_variant,
    /// animation.create_controller, and asset.move's destPath. One gate instead of one ad-hoc check
    /// per tool, closing the 2026-08-14 internal test round's F16/F17/F20 findings:
    ///
    ///  - F16: a relative path containing '..' was never canonicalised, so e.g. material.create
    ///    could write outside 'Assets/' - even outside the project entirely - while a plain absolute
    ///    path happened to be rejected already (Unity's own AssetDatabase machinery, not anything in
    ///    this plugin). Two different gaps that looked like one. Separately, 'create' overwrote
    ///    whatever already existed at the target, of any type, with no check at all.
    ///  - F17: a several-hundred-character filename passed straight through to Unity's own
    ///    Temp-write-then-move asset-import pipeline, which raises a MODAL "File name too long"
    ///    dialog on the way in - harmless interactively, fatal in a headless/batchmode Editor, where
    ///    nothing can click past it (the same hazard PrefabCommands' own doc comment describes for
    ///    InteractionMode.AutomatedAction, arriving here through Unity's import pipeline instead of a
    ///    Hades-raised dialog). The only place this can be caught is before the first byte is
    ///    written, which is what this guard is for.
    ///  - F20: repeating a 'create' at the same path silently replaced it with byte-identical
    ///    "success" everywhere except animation.create_controller, which already refused with an
    ///    actionable message. This class generalises that one tool's check into the shared gate every
    ///    create-family tool now runs through.
    ///
    /// <para><b>Canonicalisation rule.</b> A caller-supplied path is resolved against the project
    /// root (the same 'Directory.GetParent(Application.dataPath)' convention HadesBoot.BuildHello and
    /// AssetCommands.ToAbsolutePath already use) and converted back to a project-relative 'Assets/...'
    /// form. If that round trip does not produce the EXACT string the caller passed - any '..', a
    /// leading '/' or drive-style absolute form, a './' segment, a doubled slash, or leading/trailing
    /// whitespace - the call is refused. One blunt rule instead of a blocklist of bad patterns: it
    /// costs the same whether it is defending against one trick or twenty, and an LLM caller gets one
    /// deterministic instruction back ("pass a plain Assets/... path") instead of a different error
    /// shape per malformed input.</para>
    /// </summary>
    internal static class AssetPathGuard
    {
        /// <summary>Safe bound, not the OS limit itself - macOS NAME_MAX is 255 BYTES (not
        /// characters) per path component. Staying well clear of the edge means a multi-byte UTF-8
        /// name, plus whatever suffix Unity's own import step or a '.meta' sibling adds, never tips a
        /// component over the real ceiling - see this class's own doc comment (F17) for why the
        /// failure mode on the other side of that ceiling is a blocking modal dialog, not a clean
        /// error.</summary>
        internal const int MaxPathComponentBytes = 240;

        /// <summary>Structural validation only: well-formed, project-relative, under 'Assets/', no
        /// traversal, no path component over <see cref="MaxPathComponentBytes"/>. No existence check
        /// - see <see cref="RequireNewAssetPath"/> for the create-family variant that adds one. This
        /// overload is for asset.move's destPath, which must keep its EXISTING refusals (self-move,
        /// missing parent - both already come from <see cref="UnityEditor.AssetDatabase.MoveAsset"/>
        /// itself) unchanged - just phrased through the same message shape as every other tool.
        /// Returns <paramref name="path"/> unchanged (a well-formed path is required to equal its own
        /// canonical form) - the return value exists only for a fluent call at each call site.</summary>
        public static string RequireWellFormedProjectPath(string path, string context)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("'" + context + "' requires a non-empty asset path.");

            // Checked separately from the canonicalisation round-trip below: .NET's Path.GetFullPath
            // does not normalise away leading/trailing whitespace on macOS (unlike some of what it
            // collapses on Windows), so a whitespace-padded path can round-trip byte-identical and
            // slip past that check entirely.
            if (path.Trim() != path)
            {
                throw new ArgumentException(
                    "'" + context + "': path '" + path + "' has leading or trailing whitespace. Pass a plain "
                    + "project-relative path, e.g. 'Assets/Sub/Name.ext'.");
            }

            var canonical = Canonicalize(path);
            if (canonical == null || canonical != path)
            {
                throw new ArgumentException(
                    "'" + context + "': path '" + path + "' is not a plain project-relative path under 'Assets/'"
                    + (canonical != null ? " (it resolves to '" + canonical + "')" : "")
                    + ". Pass a path exactly in the form 'Assets/Sub/Name.ext' - no '..', './', doubled slashes, "
                    + "or an absolute prefix.");
            }

            foreach (var segment in canonical.Split('/'))
            {
                var byteCount = Encoding.UTF8.GetByteCount(segment);
                if (byteCount > MaxPathComponentBytes)
                {
                    throw new ArgumentException(
                        "'" + context + "': path component '" + segment + "' is " + byteCount + " bytes, over the "
                        + MaxPathComponentBytes + "-byte safe limit (macOS NAME_MAX is 255 bytes per component). "
                        + "Shorten it - Unity's own import step would otherwise hit this on the far side of a "
                        + "write, where the failure is a blocking \"File name too long\" dialog instead of a "
                        + "clean error.");
                }
            }

            return canonical;
        }

        /// <summary>Everything <see cref="RequireWellFormedProjectPath"/> checks, PLUS: refuse if
        /// anything - file or folder, imported by Unity or not - already exists on disk at the
        /// target. Checked directly on disk rather than through AssetDatabase so a file dropped there
        /// by some other process and never imported still counts (the same on-disk-existence
        /// reasoning AssetCommands.DoImportAsset already uses for its own check). <paramref
        /// name="assetKind"/>/<paramref name="editAdvice"/> build the SAME message shape
        /// animation.create_controller's pre-existing check already used ("AnimatorController already
        /// exists at '...'. Use animation_edit_controller to modify it.") - each caller names its own
        /// kind and the real edit-op alternative, rather than one generic phrase that would fit none
        /// of them.</summary>
        public static string RequireNewAssetPath(string path, string context, string assetKind, string editAdvice)
        {
            var canonical = RequireWellFormedProjectPath(path, context);

            var absolute = ToAbsolute(canonical);
            if (File.Exists(absolute) || Directory.Exists(absolute))
            {
                throw new ArgumentException(
                    assetKind + " already exists at '" + canonical + "'. Use " + editAdvice + " to modify it.");
            }

            return canonical;
        }

        /// <summary>Resolves <paramref name="path"/> against the project root and back to a
        /// project-relative 'Assets/...' form, or null if it does not resolve under 'Assets/' at all
        /// (outside the project, or a sibling folder that merely starts with the same letters).
        /// Path.Combine already discards <paramref name="path"/> in favour of an absolute SECOND
        /// argument when it is rooted, and Path.GetFullPath already collapses '..'/'.'/doubled
        /// separators - between the two, this handles the relative-traversal, absolute, and
        /// malformed-separator cases with the same few lines rather than one branch per case. Never
        /// includes the resolved absolute filesystem path in a caller-facing message (see every
        /// throw site above) - only this project-relative form, which reveals nothing beyond what the
        /// caller already knows about the project's own layout.</summary>
        static string Canonicalize(string path)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

            var assetsRoot = Path.GetFullPath(Application.dataPath);
            var assetsRootWithSep = assetsRoot + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(assetsRootWithSep, StringComparison.Ordinal)) return null;

            var relative = fullPath.Substring(assetsRootWithSep.Length).Replace(Path.DirectorySeparatorChar, '/');
            return "Assets/" + relative;
        }

        /// <summary>Project-relative "Assets/..." path to an absolute filesystem path - same
        /// convention as <see cref="Canonicalize"/> and AssetCommands.ToAbsolutePath.</summary>
        static string ToAbsolute(string projectRelativePath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
