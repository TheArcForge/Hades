// Editor/Graph/Scanning/IAssetScanner.cs
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public interface IAssetScanner
    {
        string[] SupportedExtensions { get; }
        string ScannerName { get; }
        int Version { get; }
        ScanResult Scan(string assetPath);
    }
}
