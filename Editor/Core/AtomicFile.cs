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
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, content);
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tmpPath, filePath);
        }
    }
}
