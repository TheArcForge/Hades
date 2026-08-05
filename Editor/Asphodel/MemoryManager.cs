// Editor/Asphodel/MemoryManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    public class MemoryManager
    {
        readonly string _memoryDir;

        public string MemoryDir => _memoryDir;

        public MemoryManager(string memoryDir)
        {
            _memoryDir = memoryDir;
        }

        public void EnsureDirectory()
        {
            if (!Directory.Exists(_memoryDir))
                Directory.CreateDirectory(_memoryDir);
        }

        public void EnsureDefaults()
        {
            var templates = new Dictionary<string, string>
            {
                ["decisions"] = DefaultTemplate("Decisions", "Record architectural decisions with date, rationale, and alternatives considered."),
                ["patterns"] = DefaultTemplate("Patterns", "Document established patterns the project uses. Each entry can include validation rules."),
                ["conventions"] = DefaultTemplate("Conventions", "Naming conventions, file organization, and structural rules."),
                ["pitfalls"] = DefaultTemplate("Pitfalls", "Known traps, historical bug patterns, and things to avoid."),
                ["glossary"] = DefaultTemplate("Glossary", "Domain-specific terminology used in this project."),
                ["intent"] = DefaultTemplate("Intent", "The team's current focus, active goals, and priorities.")
            };

            foreach (var kv in templates)
            {
                var path = Path.Combine(_memoryDir, kv.Key + ".md");
                if (!File.Exists(path))
                    File.WriteAllText(path, kv.Value);
            }
        }

        public MemoryFile ReadFile(string name)
        {
            var path = Path.Combine(_memoryDir, name + ".md");
            if (!File.Exists(path)) return null;

            var content = File.ReadAllText(path);
            var file = FrontmatterParser.Parse(content);
            file.Filename = name + ".md";
            file.FilePath = path;
            return file;
        }

        public void WriteFile(string name, string content)
        {
            var path = Path.Combine(_memoryDir, name + ".md");
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmpPath, path);
        }

        public void WriteMemoryFile(MemoryFile file)
        {
            var name = file.Filename.EndsWith(".md")
                ? file.Filename.Substring(0, file.Filename.Length - 3)
                : file.Filename;
            WriteFile(name, file.ToMarkdown());
        }

        public List<MemoryFile> ListFiles()
        {
            var result = new List<MemoryFile>();
            if (!Directory.Exists(_memoryDir)) return result;

            foreach (var path in Directory.GetFiles(_memoryDir, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var file = ReadFile(name);
                if (file != null)
                    result.Add(file);
            }
            return result;
        }

        public void EnsureProposalsDirectory()
        {
            var dir = Path.Combine(_memoryDir, "proposals");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public string CreateProposal(string targetFile, string content, string rationale, string id = null)
        {
            ValidateMemoryName(targetFile);
            EnsureProposalsDirectory();
            id = id ?? $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{targetFile}";
            var proposalPath = Path.Combine(_memoryDir, "proposals", id + ".md");

            var proposalContent = $"---\ntarget_file: {targetFile}\ncreated_at: {DateTime.UtcNow:o}\nrationale: {rationale}\nstatus: pending\n---\n{content}";
            File.WriteAllText(proposalPath, proposalContent);
            return id;
        }

        public List<MemoryFile> ListProposals()
        {
            var result = new List<MemoryFile>();
            var dir = Path.Combine(_memoryDir, "proposals");
            if (!Directory.Exists(dir)) return result;

            foreach (var path in Directory.GetFiles(dir, "*.md"))
            {
                var content = File.ReadAllText(path);
                var file = FrontmatterParser.Parse(content);
                file.Filename = Path.GetFileName(path);
                file.FilePath = path;
                result.Add(file);
            }
            return result;
        }

        public bool AcceptProposal(string proposalId)
        {
            var proposalPath = Path.Combine(_memoryDir, "proposals", proposalId + ".md");
            if (!File.Exists(proposalPath)) return false;

            var proposal = FrontmatterParser.Parse(File.ReadAllText(proposalPath));
            string targetFile;
            if (!proposal.Frontmatter.TryGetValue("target_file", out targetFile))
                return false;
            try { ValidateMemoryName(targetFile); }
            catch (ArgumentException) { return false; }

            var existing = ReadFile(targetFile);
            if (existing == null)
            {
                WriteFile(targetFile, proposal.Body);
            }
            else
            {
                var combined = existing.Body.TrimEnd() + "\n\n" + proposal.Body;
                existing.Body = combined;
                WriteMemoryFile(existing);
            }

            File.Delete(proposalPath);
            return true;
        }

        public bool RejectProposal(string proposalId)
        {
            var proposalPath = Path.Combine(_memoryDir, "proposals", proposalId + ".md");
            if (!File.Exists(proposalPath)) return false;
            File.Delete(proposalPath);
            return true;
        }

        public void PruneExpiredProposals(int maxAgeDays = 30)
        {
            var dir = Path.Combine(_memoryDir, "proposals");
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            foreach (var path in Directory.GetFiles(dir, "*.md"))
            {
                if (File.GetCreationTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
        }

        static string DefaultTemplate(string title, string description)
        {
            return $"---\nlast_reviewed: {DateTime.UtcNow:yyyy-MM-dd}\nvalidation_status: ok\n---\n# {title}\n\n{description}\n";
        }

        // A memory file name must be a bare basename (no separators, no traversal, no rooting) that
        // resolves to a direct child of the memory dir. Valid Tier-1 targets are names like "patterns".
        static string ValidateMemoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Memory file name must be non-empty.", nameof(name));
            if (name != Path.GetFileName(name) || name.Contains("..") || Path.IsPathRooted(name))
                throw new ArgumentException($"Invalid memory file name: '{name}'", nameof(name));
            return name;
        }
    }
}
