using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcForge.Hades.Editor.Graph.Models
{
    public class NodeRecord
    {
        public long Id { get; set; }
        public string Type { get; }
        public string Guid { get; set; }

        /// <summary>
        /// GUID of the asset that OWNS this node. Root asset nodes own themselves
        /// (OwnerGuid == Guid). Sub-object nodes (GameObject/Component children of a
        /// scene or prefab; ScriptType/ScriptMethod children of a script) carry their
        /// owning asset's GUID even though their own Guid is null. Deletion is keyed on
        /// this so an asset's entire node set is removed as a unit on re-scan/delete.
        /// </summary>
        public string OwnerGuid { get; set; }

        public long? FileId { get; set; }
        public long? ParentNodeId { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string SourceRange { get; set; }

        // Lazy Properties: bulk reads (ReadNodeFromStatement) set the raw JSON and defer the
        // Newtonsoft parse until a caller actually touches Properties. Flagship queries that
        // load thousands of nodes but never read Properties (find_references, trace_dependencies)
        // pay zero deserialization cost. _parsed==true means _properties is authoritative;
        // false means only _rawPropertiesJson is set and a parse is owed on first access.
        Dictionary<string, object> _properties;
        string _rawPropertiesJson;
        bool _parsed = true;

        public Dictionary<string, object> Properties
        {
            get
            {
                if (!_parsed)
                {
                    _properties = _rawPropertiesJson == null
                        ? null
                        : JsonConvert.DeserializeObject<Dictionary<string, object>>(_rawPropertiesJson);
                    _parsed = true;
                }
                return _properties;
            }
            set { _properties = value; _parsed = true; _rawPropertiesJson = null; }
        }

        public NodeRecord(string type, string guid = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.Length == 0) throw new ArgumentException("Type must not be empty.", nameof(type));
            Type = type;
            Guid = guid;
        }

        public string PropertiesJson
        {
            // When only raw JSON was set (the bulk-read path), return it verbatim — no
            // serialize round-trip. Otherwise serialize the in-memory dictionary.
            get => _parsed
                ? (_properties == null ? null : JsonConvert.SerializeObject(_properties))
                : _rawPropertiesJson;
            set { _rawPropertiesJson = value; _parsed = false; }
        }
    }
}
