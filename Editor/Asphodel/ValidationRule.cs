// Editor/Asphodel/ValidationRule.cs
namespace ArcForge.Hades.Editor.Asphodel
{
    public class ValidationRule
    {
        public string QueryType { get; set; }
        public string Query { get; set; }
        public int MinCount { get; set; }
        public string FailureMessage { get; set; }
        public int SourceLineStart { get; set; }
        public int SourceLineEnd { get; set; }
    }

    public class ValidationResult
    {
        public string Filename { get; set; }
        public string Status { get; set; }
        public int RulesChecked { get; set; }
        public int RulesPassed { get; set; }
        public int RulesWarning { get; set; }
        public int RulesSkipped { get; set; }
        public string[] Warnings { get; set; }
    }
}
