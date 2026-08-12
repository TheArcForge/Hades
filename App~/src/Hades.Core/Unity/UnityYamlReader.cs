using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Hades.Core.Unity;

/// <summary>
/// Streams Unity YAML into <see cref="UnityObject"/>s.
///
/// Uses YamlDotNet's event-level <see cref="Parser"/> rather than the document model, for two
/// measured reasons. Animator controllers emit duplicate keys, which <c>YamlStream</c> rejects
/// outright ("Duplicate key data") and an event stream never notices, since it constructs no
/// mappings. And a 10.3 MB scene costs 395 MB of heap through the document model against 59 MB
/// here — 6.7x — which matters because Hades indexes whole projects in the background.
/// </summary>
public static class UnityYamlReader
{
    public static IReadOnlyList<UnityObject> Read(string content, string assetPath)
    {
        if (!UnityYamlPreprocessor.LooksLikeUnityYaml(content)) return [];

        // Class ids live in the document headers the preprocessor is about to rewrite, so
        // capture them first, in order. Document N in the parsed stream is header N here.
        var headers = UnityYamlPreprocessor.DocumentHeaderPattern().Matches(content)
            .Select(m => (
                ClassId: int.Parse(m.Groups["classId"].Value),
                IsStripped: m.Value.TrimEnd().EndsWith("stripped", StringComparison.Ordinal)))
            .ToList();

        var objects = new List<UnityObject>();

        try
        {
            var parser = new Parser(new StringReader(UnityYamlPreprocessor.MakeStandardYaml(content)));
            var documentIndex = -1;

            while (parser.MoveNext())
            {
                if (parser.Current is not DocumentStart) continue;

                documentIndex++;
                var header = documentIndex < headers.Count
                    ? headers[documentIndex]
                    : (ClassId: 0, IsStripped: false);

                if (ReadDocument(parser, header.ClassId, header.IsStripped) is { } obj)
                    objects.Add(obj);
            }
        }
        catch (YamlException)
        {
            // A malformed or truncated file (one being written as we index it) yields whatever
            // was read before the fault rather than failing the whole scan.
        }

        return objects;
    }

    /// <summary>
    /// Walks one document's events, collecting the anchor, m_Name, and every reference, tracking
    /// the property path as it descends so each reference knows where it came from.
    ///
    /// Unity's document shape is a single-key outer mapping — <c>GameObject:</c> — whose value is
    /// the body, so the anchor sits on the OUTER mapping and the type name is its only key.
    /// </summary>
    static UnityObject? ReadDocument(IParser parser, int classId, bool isStripped)
    {
        long? fileId = null;
        string? typeName = null;
        string? name = null;
        var references = new List<UnityReference>();
        var modifications = new List<UnityModification>();
        UnityReference? correspondingSource = null;

        // A PrefabInstance's m_Modifications entries are only meaningful as whole units — the
        // propertyPath and the objectReference beside it are one fact. Collected here rather
        // than emitted as loose references, which is what plan 2 did.
        UnityReference? modTarget = null;
        string? modPropertyPath = null;
        string? modValue = null;
        UnityReference? modObjectReference = null;
        var inModifications = false;
        var path = new List<string>();

        // Whether each open container pushed a path segment. MappingStart pushes only when it
        // follows a key, so MappingEnd must not pop unconditionally — that mismatch is what
        // produced paths like "GameObject.component" alongside "GameObject.m_Component.component".
        var pushedSegment = new Stack<bool>();

        // Kind of each currently open container, in lockstep with pushedSegment (true =
        // Sequence, false = Mapping). A key-less flow MappingStart needs this to tell "I am a
        // bare sequence element" (m_Materials's own entries) apart from "I am the document's
        // own body mapping" (also key-less) — depth alone cannot make that distinction.
        var containerIsSequence = new Stack<bool>();

        string? pendingKey = null;
        var depth = 0;

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart mappingStart:
                {
                    if (fileId is null
                        && mappingStart.Anchor.Value is { Length: > 0 } anchor
                        && long.TryParse(anchor, out var parsedAnchor))
                    {
                        fileId = parsedAnchor;
                    }

                    // A flow mapping under a key ("m_Script: {...}") is one reference shape. The
                    // other is a flow mapping with NO key, sitting directly inside a sequence
                    // ("m_Materials:\n  - {...}") — SequenceStart already consumed the key onto
                    // `path`, so containerIsSequence is what tells this apart from the equally
                    // key-less document body mapping. Either way this is what catches the 76% of
                    // references that wrap across lines: the parser has already reassembled
                    // them, so there is nothing line-shaped to miss.
                    var isDirectSequenceElement = pendingKey is null
                        && containerIsSequence.Count > 0 && containerIsSequence.Peek();

                    if (mappingStart.Style == MappingStyle.Flow && (pendingKey is not null || isDirectSequenceElement))
                    {
                        var propertyPath = pendingKey is not null ? Join(path, pendingKey) : string.Join('.', path);
                        pendingKey = null;

                        if (TryReadFlowScalars(parser, out var flow))
                        {
                            var reference = ToReference(flow, propertyPath);

                            // A bare sequence element with no guid is a same-file structural
                            // back-reference — m_Children, SceneRoots.m_Roots, a renderer's own
                            // m_RendererFeatures — the mirror image of a keyed field (m_Father)
                            // that already records the relationship from the other end.
                            // Recording it here too would double the hierarchy edges for every
                            // one of them without adding a fact the graph does not already have.
                            // The keyed path above is untouched — it still records every local
                            // reference it always did (m_Father, m_GameObject,
                            // m_CorrespondingSourceObject, m_Modification.m_TransformParent...).
                            if (isDirectSequenceElement && reference is { IsExternal: false })
                                reference = null;

                            if (inModifications && propertyPath.EndsWith(".target", StringComparison.Ordinal))
                                modTarget = reference;
                            else if (inModifications && propertyPath.EndsWith(".objectReference", StringComparison.Ordinal))
                                modObjectReference = reference;
                            else if (propertyPath == "m_CorrespondingSourceObject")
                            {
                                correspondingSource = reference;
                                if (reference is not null) references.Add(reference);
                            }
                            else if (reference is not null)
                                references.Add(reference);
                        }

                        continue;
                    }

                    var pushes = pendingKey is not null;
                    if (pushes) { path.Add(pendingKey!); pendingKey = null; }
                    pushedSegment.Push(pushes);
                    containerIsSequence.Push(false);
                    depth++;
                    break;
                }

                case MappingEnd:
                    depth--;

                    // One modification entry just closed.
                    if (inModifications && modTarget is not null && modPropertyPath is not null)
                    {
                        modifications.Add(new UnityModification
                        {
                            Target = modTarget,
                            PropertyPath = modPropertyPath,
                            Value = modValue ?? string.Empty,
                            ObjectReference = modObjectReference,
                        });
                        modTarget = null; modPropertyPath = null; modValue = null; modObjectReference = null;
                    }

                    if (pushedSegment.Count > 0)
                    {
                        containerIsSequence.Pop();
                        if (pushedSegment.Pop() && path.Count > 0)
                            path.RemoveAt(path.Count - 1);
                    }
                    if (depth <= 0) goto done;
                    break;

                case Scalar scalar:
                    if (pendingKey is null)
                    {
                        // The outer mapping's single key is the Unity type name ("GameObject:").
                        // It names the document, so it must NOT become a property-path segment —
                        // leaving it pending would prefix every path with it.
                        if (typeName is null && depth == 1)
                        {
                            typeName = scalar.Value;
                            break;
                        }

                        pendingKey = scalar.Value;
                    }
                    else
                    {
                        if (pendingKey == "m_Name") name = scalar.Value;
                        else if (inModifications && pendingKey == "propertyPath") modPropertyPath = scalar.Value;
                        else if (inModifications && pendingKey == "value") modValue = scalar.Value;
                        pendingKey = null;
                    }
                    break;

                case SequenceStart:
                {
                    var pushes = pendingKey is not null;
                    if (pushes) { path.Add(pendingKey!); pendingKey = null; }
                    pushedSegment.Push(pushes);
                    containerIsSequence.Push(true);
                    if (string.Join('.', path) == "m_Modification.m_Modifications") inModifications = true;
                    break;
                }

                case SequenceEnd:
                    if (string.Join('.', path) == "m_Modification.m_Modifications") inModifications = false;
                    if (pushedSegment.Count > 0)
                    {
                        containerIsSequence.Pop();
                        if (pushedSegment.Pop() && path.Count > 0)
                            path.RemoveAt(path.Count - 1);
                    }
                    break;

                case DocumentEnd:
                    goto done;
            }
        }

    done:
        if (fileId is null) return null;

        return new UnityObject
        {
            ClassId = classId,
            TypeName = classId > 0 ? UnityClassIds.NameFor(classId) : typeName ?? "Unknown",
            DeclaredTypeName = typeName,
            FileId = fileId.Value,
            Name = name,
            IsStripped = isStripped,
            References = references,
            Modifications = modifications,
            CorrespondingSourceObject = correspondingSource,
        };
    }

    /// <summary>
    /// Reads a flow mapping's scalar key/value pairs, having already consumed its MappingStart.
    /// Returns false when it is not a flat scalar mapping, having consumed through the matching
    /// MappingEnd either way so the caller's traversal stays aligned.
    /// </summary>
    static bool TryReadFlowScalars(IParser parser, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        var nested = 0;
        var flat = true;

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case Scalar scalar when nested == 0 && key is null:
                    key = scalar.Value;
                    break;

                case Scalar scalar when nested == 0:
                    values[key!] = scalar.Value;
                    key = null;
                    break;

                case MappingStart:
                case SequenceStart:
                    nested++;
                    flat = false;
                    break;

                case SequenceEnd:
                    nested--;
                    break;

                case MappingEnd when nested > 0:
                    nested--;
                    break;

                case MappingEnd:
                    return flat;
            }
        }

        return false;
    }

    /// <summary>The reference a flow mapping represents, or null when it is Unity's null
    /// ({fileID: 0} with no guid) or not a reference at all.</summary>
    static UnityReference? ToReference(Dictionary<string, string> flow, string propertyPath)
    {
        if (!flow.TryGetValue("fileID", out var rawFileId)) return null;
        if (!long.TryParse(rawFileId, out var fileId)) return null;

        flow.TryGetValue("guid", out var guid);

        // {fileID: 0} with no guid is Unity's null reference. Recording those would bury the
        // graph in meaningless edges — an empty slot is not a relationship.
        if (fileId == 0 && string.IsNullOrEmpty(guid)) return null;

        return new UnityReference
        {
            FileId = fileId,
            Guid = string.IsNullOrEmpty(guid) ? null : guid,
            Type = flow.TryGetValue("type", out var t) && int.TryParse(t, out var typeValue) ? typeValue : null,
            PropertyPath = propertyPath,
        };
    }

    static string Join(IReadOnlyList<string> path, string key) =>
        path.Count == 0 ? key : string.Join('.', path) + "." + key;
}
