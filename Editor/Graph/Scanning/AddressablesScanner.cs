using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class AddressablesScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new string[0];
        public string ScannerName => "AddressablesScanner";
        public int Version => 1;

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();

            var settingsType = FindType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings");
            if (settingsType == null)
            {
                result.Warnings.Add(new ScanWarning(WarningSeverity.Info,
                    "Addressables package not installed", assetPath));
                return result;
            }

            try
            {
                var settings = AssetDatabase.LoadAssetAtPath(assetPath, settingsType);
                if (settings == null) return result;

                var groupsProp = settingsType.GetProperty("groups");
                var groups = groupsProp?.GetValue(settings) as System.Collections.IList;
                if (groups == null) return result;

                foreach (var group in groups)
                {
                    if (group == null) continue;
                    var groupType = group.GetType();
                    var groupName = groupType.GetProperty("Name")?.GetValue(group)?.ToString();
                    var groupGuid = groupType.GetProperty("Guid")?.GetValue(group)?.ToString();

                    var groupNode = new NodeRecord("AddressableGroup", groupGuid)
                    {
                        Name = groupName
                    };
                    result.Nodes.Add(groupNode);

                    var entries = groupType.GetProperty("entries")?.GetValue(group) as System.Collections.IList;
                    if (entries == null) continue;

                    foreach (var entry in entries)
                    {
                        var entryType = entry.GetType();
                        var entryGuid = entryType.GetProperty("guid")?.GetValue(entry)?.ToString();
                        var entryAddress = entryType.GetProperty("address")?.GetValue(entry)?.ToString();
                        var entryAssetPath = entryType.GetProperty("AssetPath")?.GetValue(entry)?.ToString();

                        // The entry's own `guid` equals the target asset's GUID. Using it as the
                        // node identity collided the entry with the real asset node and turned
                        // addressable_for into a self-edge. Give the entry a group-scoped synthetic
                        // identity so it is a distinct node; addressable_for still points at the
                        // real asset (its true GUID).
                        var entryNodeGuid = $"addr_entry:{groupGuid}:{entryGuid}";

                        var entryNode = new NodeRecord("AddressableEntry", entryNodeGuid)
                        {
                            Name = entryAddress,
                            Path = entryAssetPath,
                            Properties = new Dictionary<string, object>
                            {
                                { "address", entryAddress },
                                { "target_guid", entryGuid }
                            }
                        };
                        result.Nodes.Add(entryNode);

                        result.Edges.Add(new EdgeRecord("contains", groupGuid, 0, entryNodeGuid, 0));

                        if (entryAssetPath != null)
                        {
                            var targetGuid = AssetDatabase.AssetPathToGUID(entryAssetPath);
                            if (!string.IsNullOrEmpty(targetGuid))
                            {
                                result.Edges.Add(new EdgeRecord("addressable_for", entryNodeGuid, 0, targetGuid, 0));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(new ScanWarning(WarningSeverity.Warning,
                    $"Error scanning addressables: {ex.Message}", assetPath));
            }

            return result;
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }
    }
}
