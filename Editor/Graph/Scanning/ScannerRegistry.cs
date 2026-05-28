// Editor/Graph/Scanning/ScannerRegistry.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class ScannerRegistry
    {
        readonly Dictionary<string, IAssetScanner> _extensionMap = new Dictionary<string, IAssetScanner>();
        readonly List<IAssetScanner> _allScanners = new List<IAssetScanner>();

        public ScannerRegistry()
        {
            DiscoverScanners();
            ApplyScannerPriority();
        }

        void DiscoverScanners()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException) { continue; }

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(IAssetScanner).IsAssignableFrom(type)) continue;

                    try
                    {
                        var scanner = (IAssetScanner)Activator.CreateInstance(type);
                        _allScanners.Add(scanner);
                        foreach (var ext in scanner.SupportedExtensions)
                        {
                            _extensionMap[ext.ToLowerInvariant()] = scanner;
                        }
                    }
                    catch { }
                }
            }
        }

        void ApplyScannerPriority()
        {
            _extensionMap.Remove(".cs");
        }

        public IAssetScanner GetScannerForPath(string assetPath)
        {
            var ext = System.IO.Path.GetExtension(assetPath)?.ToLowerInvariant();
            if (ext == null) return null;
            _extensionMap.TryGetValue(ext, out var scanner);
            return scanner;
        }

        public IReadOnlyList<IAssetScanner> GetAll() => _allScanners.AsReadOnly();

        public HashSet<string> GetCoveredExtensions()
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scanner in _allScanners)
            {
                foreach (var ext in scanner.SupportedExtensions)
                    extensions.Add(ext.ToLowerInvariant());
            }
            // .cs files are handled by the Node.js scanner, not registered in _extensionMap
            extensions.Add(".cs");
            return extensions;
        }
    }
}
