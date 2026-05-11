namespace ArcForge.Hades.Editor.Graph.Models
{
    public enum WarningSeverity { Info, Warning, Error }

    public class ScanWarning
    {
        public WarningSeverity Severity { get; set; }
        public string Message { get; set; }
        public string AssetPath { get; set; }
        public string Details { get; set; }

        public ScanWarning(WarningSeverity severity, string message, string assetPath = null, string details = null)
        {
            Severity = severity;
            Message = message;
            AssetPath = assetPath;
            Details = details;
        }
    }
}
