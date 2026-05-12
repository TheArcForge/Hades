// Editor/Asphodel/MemoryFile.cs
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Asphodel
{
    public class MemoryFile
    {
        public string Filename { get; set; }
        public string FilePath { get; set; }
        public Dictionary<string, string> Frontmatter { get; set; } = new Dictionary<string, string>();
        public string Body { get; set; } = "";
        public List<ValidationRule> ValidationRules { get; set; } = new List<ValidationRule>();

        public string ValidationStatus
        {
            get
            {
                string val;
                return Frontmatter.TryGetValue("validation_status", out val) ? val : "ok";
            }
        }

        public string LastReviewed
        {
            get
            {
                string val;
                return Frontmatter.TryGetValue("last_reviewed", out val) ? val : null;
            }
        }

        public string LastValidated
        {
            get
            {
                string val;
                return Frontmatter.TryGetValue("last_validated_against_graph", out val) ? val : null;
            }
        }

        public string ToMarkdown()
        {
            var sb = new System.Text.StringBuilder();
            if (Frontmatter.Count > 0)
            {
                sb.AppendLine("---");
                foreach (var kv in Frontmatter)
                    sb.AppendLine($"{kv.Key}: {kv.Value}");
                sb.AppendLine("---");
            }
            sb.Append(Body);
            return sb.ToString();
        }
    }
}
