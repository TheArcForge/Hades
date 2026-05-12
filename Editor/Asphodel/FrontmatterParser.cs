// Editor/Asphodel/FrontmatterParser.cs
using System.Collections.Generic;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    public static class FrontmatterParser
    {
        public static MemoryFile Parse(string markdown)
        {
            var file = new MemoryFile();
            if (string.IsNullOrEmpty(markdown))
                return file;

            var lines = markdown.Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
            {
                file.Body = markdown;
                return file;
            }

            int closingIndex = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd('\r') == "---")
                {
                    closingIndex = i;
                    break;
                }
            }

            if (closingIndex < 0)
            {
                file.Body = markdown;
                return file;
            }

            for (int i = 1; i < closingIndex; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0)
                    continue;

                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();

                if (key.Length > 0 && !key.Contains(" "))
                    file.Frontmatter[key] = value;
            }

            if (file.Frontmatter.Count == 0)
            {
                Debug.LogWarning("[Hades Asphodel] Malformed frontmatter detected, treating as plain markdown");
                file.Body = markdown;
                return file;
            }

            int bodyStartIndex = 0;
            for (int i = 0; i <= closingIndex; i++)
                bodyStartIndex += lines[i].Length + 1;

            if (bodyStartIndex < markdown.Length)
                file.Body = markdown.Substring(bodyStartIndex);
            else
                file.Body = "";

            return file;
        }
    }
}
