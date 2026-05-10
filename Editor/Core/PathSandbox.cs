using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    public static class PathSandbox
    {
        static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string Resolve(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentException("Path must not be null or empty.", nameof(relativePath));

            if (relativePath.StartsWith("/") || relativePath.StartsWith("\\") ||
                (relativePath.Length >= 2 && relativePath[1] == ':'))
                throw new ArgumentException($"Path '{relativePath}' must be relative, not absolute.", nameof(relativePath));

            var normalized = relativePath.Replace('\\', '/');
            var root = ProjectRoot;
            var combined = Path.Combine(root, normalized);
            var canonical = Path.GetFullPath(combined);

            var rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(rootWithSeparator, PathComparison) &&
                !string.Equals(canonical, root, PathComparison))
                throw new InvalidOperationException($"Path '{relativePath}' resolves outside the project root. Access denied.");

            return canonical;
        }

        public static string ResolveWritable(string relativePath)
        {
            var canonical = Resolve(relativePath);

            if (IsBlockedForWrite(relativePath))
            {
                var normalized = relativePath.Replace('\\', '/');
                var segment = normalized.Split('/')[0];
                throw new InvalidOperationException($"Writing to '{segment}' is not allowed.");
            }

            return canonical;
        }

        public static string MakeRelative(string absolutePath)
        {
            var root = ProjectRoot;

            var withSep = root + Path.DirectorySeparatorChar;
            if (absolutePath.StartsWith(withSep, PathComparison))
                return absolutePath.Substring(withSep.Length);

            var withAltSep = root + Path.AltDirectorySeparatorChar;
            if (absolutePath.StartsWith(withAltSep, PathComparison))
                return absolutePath.Substring(withAltSep.Length);

            return absolutePath;
        }

        static bool IsBlockedForWrite(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/');
            return normalized.Equals(".git", PathComparison) ||
                   normalized.StartsWith(".git/", PathComparison);
        }
    }
}
