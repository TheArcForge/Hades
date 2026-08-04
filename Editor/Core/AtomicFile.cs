using System.IO;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Writes a file atomically: content lands in a sibling .tmp file first, then replaces the
    /// target. A crash mid-write leaves the previous file intact rather than a truncated one.
    /// Extracted from MCPClientConfig so HadesConfig can share it rather than copy it.
    /// </summary>
    public static class AtomicFile
    {
        public static void Write(string filePath, string content)
        {
            EnsureDir(filePath);

            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, content);
            File.Move(tmpPath, filePath, overwrite: true);
        }

        /// <summary>Byte-safe variant — for copying binary or pre-encoded files (e.g. the launcher
        /// bundle) without a text round-trip that could alter encoding.</summary>
        public static void Write(string filePath, byte[] content)
        {
            EnsureDir(filePath);

            var tmpPath = filePath + ".tmp";
            File.WriteAllBytes(tmpPath, content);
            File.Move(tmpPath, filePath, overwrite: true);
        }

        static void EnsureDir(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
