// Editor/Graph/Scanning/ScriptScanner.cs
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class ScriptScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".cs" };
        public string ScannerName => "ScriptScanner";
        public int Version => 1;

        static readonly Regex NamespaceRegex = new Regex(
            @"namespace\s+([\w.]+)", RegexOptions.Compiled);

        static readonly Regex TypeRegex = new Regex(
            @"(?:public|internal|private|protected)?\s*(?:abstract|sealed|static|partial)?\s*(?:class|struct|interface|enum)\s+(\w+)(?:<[^>]+>)?(?:\s*:\s*([^\{]+))?",
            RegexOptions.Compiled);

        static readonly Regex MethodRegex = new Regex(
            @"(?:public|private|protected|internal|static|virtual|override|abstract|async|sealed|\s)*\s+(?:[\w<>\[\],\s]+)\s+(\w+)\s*\(([^)]*)\)",
            RegexOptions.Compiled);

        static readonly Regex FieldRegex = new Regex(
            @"\[SerializeField\]\s*(?:private|protected|public)?\s*([\w<>\[\]]+)\s+(\w+)",
            RegexOptions.Compiled);

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();

            if (!File.Exists(assetPath)) return result;

            var content = File.ReadAllText(assetPath);
            var lines = content.Split('\n');
            var fileName = Path.GetFileName(assetPath);

            var scriptNode = new NodeRecord("Script")
            {
                Name = fileName,
                Path = assetPath
            };
            result.Nodes.Add(scriptNode);

            var currentNamespace = ExtractNamespace(content);

            var typeMatches = TypeRegex.Matches(content);
            foreach (Match typeMatch in typeMatches)
            {
                var typeName = typeMatch.Groups[1].Value;
                var baseTypes = typeMatch.Groups[2].Success
                    ? typeMatch.Groups[2].Value.Trim().Split(',').Select(b => b.Trim()).ToArray()
                    : new string[0];

                var lineNumber = GetLineNumber(content, typeMatch.Index);
                var endLine = FindTypeEndLine(lines, lineNumber);

                var typeNode = new NodeRecord("ScriptType")
                {
                    Name = typeName,
                    Path = assetPath,
                    SourceRange = $"{assetPath}:{lineNumber}:{endLine}",
                    Properties = new Dictionary<string, object>
                    {
                        { "namespace", currentNamespace ?? "" }
                    }
                };

                if (baseTypes.Length > 0)
                {
                    var primaryBase = baseTypes[0].Split('<')[0].Trim();
                    typeNode.Properties["base_type"] = primaryBase;
                    if (baseTypes.Length > 1)
                        typeNode.Properties["interfaces"] = baseTypes.Skip(1).Select(b => b.Trim()).ToArray();
                }

                result.Nodes.Add(typeNode);

                result.Edges.Add(new EdgeRecord("defines",
                    scriptNode.Guid, 0, typeNode.Guid, 0));

                var typeBody = ExtractTypeBody(content, typeMatch.Index);
                if (typeBody != null)
                {
                    var methodMatches = MethodRegex.Matches(typeBody);
                    foreach (Match methodMatch in methodMatches)
                    {
                        var methodName = methodMatch.Groups[1].Value;
                        if (IsKeyword(methodName)) continue;

                        var methodLine = lineNumber + GetLineNumber(typeBody, methodMatch.Index) - 1;

                        var methodNode = new NodeRecord("ScriptMethod")
                        {
                            Name = methodName,
                            Path = assetPath,
                            SourceRange = $"{assetPath}:{methodLine}",
                            Properties = new Dictionary<string, object>
                            {
                                { "parameters", methodMatch.Groups[2].Value.Trim() }
                            }
                        };
                        result.Nodes.Add(methodNode);

                        result.Edges.Add(new EdgeRecord("defines",
                            typeNode.Guid, 0, methodNode.Guid, 0));
                    }
                }
            }

            return result;
        }

        string ExtractNamespace(string content)
        {
            var match = NamespaceRegex.Match(content);
            return match.Success ? match.Groups[1].Value : null;
        }

        int GetLineNumber(string content, int charIndex)
        {
            int line = 1;
            for (int i = 0; i < charIndex && i < content.Length; i++)
                if (content[i] == '\n') line++;
            return line;
        }

        int FindTypeEndLine(string[] lines, int startLine)
        {
            int braceCount = 0;
            bool foundOpen = false;
            for (int i = startLine - 1; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{') { braceCount++; foundOpen = true; }
                    if (c == '}') braceCount--;
                    if (foundOpen && braceCount == 0) return i + 1;
                }
            }
            return lines.Length;
        }

        string ExtractTypeBody(string content, int typeStart)
        {
            int braceStart = content.IndexOf('{', typeStart);
            if (braceStart < 0) return null;

            int braceCount = 1;
            int i = braceStart + 1;
            while (i < content.Length && braceCount > 0)
            {
                if (content[i] == '{') braceCount++;
                if (content[i] == '}') braceCount--;
                i++;
            }
            return content.Substring(braceStart + 1, i - braceStart - 2);
        }

        bool IsKeyword(string name)
        {
            return name == "if" || name == "for" || name == "while" || name == "switch" ||
                   name == "foreach" || name == "catch" || name == "using" || name == "return" ||
                   name == "new" || name == "get" || name == "set" || name == "typeof" || name == "nameof";
        }
    }
}
