using Hades.Core.Unity;

namespace Hades.Core.Reading;

/// <summary>
/// One raw object-reference field's value - <c>{fileID, guid, type}</c> - as read straight off
/// disk, with no graph involved. See <see cref="ReadThrough"/>'s class doc comment for why
/// read-through never touches the graph: resolving <see cref="Guid"/> to a project-relative path,
/// when it names another asset, is the caller's (ProjectService's) one graph touch, exactly like a
/// MonoBehaviour's raw <c>m_Script</c> guid in <see cref="ComponentSummary"/>.
/// </summary>
public sealed record ObjectReferenceInfo
{
    public required long FileId { get; init; }

    /// <summary>Null for a LOCAL reference - another object in the SAME file - and for Unity's own
    /// null reference. Set only for an EXTERNAL reference, naming another asset's .meta guid.</summary>
    public string? Guid { get; init; }
    public int? Type { get; init; }

    /// <summary>True for Unity's own null reference: <c>{fileID: 0}</c>, no guid.</summary>
    public required bool IsUnset { get; init; }
}

/// <summary>One <c>{fileID: 0}</c> occurrence found while scanning a whole file - see
/// <see cref="ReferenceReading.FindUnsetReferences"/>.</summary>
public sealed record UnsetReferenceHit
{
    public required long FileId { get; init; }
    public required string ObjectKind { get; init; }
    public string? ObjectName { get; init; }
    public required string PropertyPath { get; init; }
}

/// <summary>One persistent (Inspector-wired) listener entry from a UnityEvent's
/// <c>m_PersistentCalls</c> - see <see cref="ReferenceReading.GetEventListeners"/>.</summary>
public sealed record PersistentCallHit
{
    /// <summary>Dotted path to the UnityEvent field itself, e.g. "m_OnClick" - the object's own
    /// field, with the fixed <c>.m_PersistentCalls.m_Calls</c> machinery stripped off.</summary>
    public required string EventField { get; init; }

    /// <summary>Position within the event field's own <c>m_Calls</c> list.</summary>
    public required int Index { get; init; }

    public required ObjectReferenceInfo Target { get; init; }
    public string? TargetAssemblyTypeName { get; init; }
    public required string MethodName { get; init; }

    /// <summary>Unity's own numeric PersistentListenerMode, raw - left uninterpreted like every
    /// other scalar read-through returns (see <see cref="ReadThrough.GetAnimatorController"/>'s
    /// ConditionMode).</summary>
    public required string Mode { get; init; }

    /// <summary>Unity's own numeric UnityEventCallState, raw.</summary>
    public required string CallState { get; init; }

    /// <summary>
    /// <c>m_Arguments</c>, unfiltered - which of its five slots is actually meaningful depends on
    /// <see cref="Mode"/>, which this does not interpret. Same "never further interpreted" stance
    /// <see cref="ReadThrough.GetComponentProperties"/>'s own doc comment states for every other
    /// struct-valued field: deciding which slot is "the real one" would need the mode enum
    /// resolved to a meaning, which is a guess this stays out of.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
}

/// <summary>
/// Reference and UnityEvent-listener read-through: resolving one object-reference field, scanning
/// a whole file for Unity's null reference, and reading a UnityEvent's persistent-call list - the
/// same one-file-at-a-time mechanism as <see cref="ReadThrough"/> (see its class doc comment for
/// why read-through re-parses rather than indexing), split into its own file rather than growing
/// that already-large one further.
///
/// <see cref="GetReference"/> and <see cref="GetEventListeners"/> are built directly on the
/// existing public <see cref="ReadThrough.GetComponentProperties"/> - one object's raw field tree
/// is all either needs. <see cref="FindUnsetReferences"/> instead needs arbitrarily many objects'
/// raw trees at once, since it searches a WHOLE FILE rather than one named object; re-parsing the
/// file once per object the way a loop over <see cref="ReadThrough.GetComponentProperties"/> would
/// is exactly the cost <see cref="ReadThrough.ReadAllDocumentTrees"/> already exists to avoid (see
/// its own doc comment), so this uses that internal primitive directly instead.
/// </summary>
public static class ReferenceReading
{
    /// <summary>
    /// Reads one named field and interprets it as an object reference. Local-vs-external and
    /// unset are reported raw; resolving an external guid to a path is the caller's one graph
    /// touch (ProjectService.GetReference), never done here.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path escapes the project's scan roots, no object with <paramref name="fileId"/> exists,
    /// <paramref name="property"/> is not a field on it, or the field's value is not shaped like an
    /// object reference (<c>{fileID[, guid][, type]}</c>).
    /// </exception>
    /// <exception cref="FileNotFoundException">The file is no longer on disk.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not text Unity YAML, or does not parse cleanly all the way through.
    /// </exception>
    public static ObjectReferenceInfo GetReference(
        string projectRoot, string relativePath, long fileId, string property)
    {
        var properties = ReadThrough.GetComponentProperties(projectRoot, relativePath, fileId);

        if (!properties.TryGetValue(property, out var raw))
        {
            throw new ArgumentException(
                $"'{property}' is not a field on fileID {fileId} in '{relativePath}'. Available "
                + "fields: " + string.Join(", ", properties.Keys) + ". Call inspect_asset with 'target' and 'component' (no 'property') to confirm.",
                nameof(property));
        }

        if (!TryParseReference(raw, out var reference))
        {
            throw new ArgumentException(
                $"'{property}' on fileID {fileId} in '{relativePath}' is not an object-reference "
                + "field - its value does not have the {fileID, guid, type} shape. Call "
                + "inspect_asset with 'target', 'component', and 'property' to see its actual value.",
                nameof(property));
        }

        return reference;
    }

    /// <summary>
    /// Every <c>{fileID: 0}</c> object-reference field anywhere in one file's objects - the
    /// null-reference bug this exists to catch. Deliberately unfiltered: Unity's serialized YAML
    /// does not record WHY a reference is empty, so a field a developer deliberately left optional
    /// and one they simply forgot to wire up are byte-for-byte identical on disk - see
    /// reference_find_unset's <c>[Description]</c> (<c>Mcp.ReferenceTools</c>) for the full,
    /// caller-facing statement of this limitation, including that Unity's own structural
    /// bookkeeping fields (e.g. <c>m_Father</c> on every root GameObject) are reported exactly the
    /// same way as a user script's own fields, because nothing in the data distinguishes them
    /// either.
    /// </summary>
    /// <exception cref="ArgumentException">The path escapes the project's scan roots.</exception>
    /// <exception cref="FileNotFoundException">The file is no longer on disk.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not text Unity YAML, or does not parse cleanly all the way through.
    /// </exception>
    public static IReadOnlyList<UnsetReferenceHit> FindUnsetReferences(string projectRoot, string relativePath)
    {
        var content = ReadThrough.LoadValidatedContent(projectRoot, relativePath);
        var objects = UnityYamlReader.Read(content, relativePath);
        var trees = ReadThrough.ReadAllDocumentTrees(content);

        var hits = new List<UnsetReferenceHit>();

        foreach (var obj in objects)
        {
            if (!trees.TryGetValue(obj.FileId, out var tree)) continue;

            foreach (var (path, dict) in FindReferenceDicts(tree, ""))
            {
                if (!TryParseReference(dict, out var reference) || !reference.IsUnset) continue;

                hits.Add(new UnsetReferenceHit
                {
                    FileId = obj.FileId,
                    ObjectKind = obj.TypeName,
                    ObjectName = obj.Name,
                    PropertyPath = path,
                });
            }
        }

        return hits;
    }

    /// <summary>
    /// Every persistent listener across every UnityEvent-shaped field on one object, wherever they
    /// occur in its serialized tree (a direct field, like Button's <c>m_OnClick</c>, or nested
    /// inside a custom serializable wrapper). A call whose own target is unset is still reported,
    /// with <see cref="ObjectReferenceInfo.IsUnset"/> true - unlike event_find_all, which is
    /// graph-served and never sees such a call at all, because Unity's own reader drops a null
    /// reference before it ever becomes a graph edge. Target resolution (local vs. external vs.
    /// unset) is the same raw shape <see cref="GetReference"/> returns; the caller
    /// (ProjectService.GetEventListeners) takes the one graph touch to resolve an external target
    /// to a path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path escapes the project's scan roots, or no object with <paramref name="fileId"/>
    /// exists in the file.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file is no longer on disk.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not text Unity YAML, or does not parse cleanly all the way through.
    /// </exception>
    public static IReadOnlyList<PersistentCallHit> GetEventListeners(
        string projectRoot, string relativePath, long fileId)
    {
        var properties = ReadThrough.GetComponentProperties(projectRoot, relativePath, fileId);
        var hits = new List<PersistentCallHit>();

        foreach (var (eventField, calls) in FindPersistentCallLists(properties, ""))
        {
            for (var i = 0; i < calls.Count; i++)
            {
                if (calls[i] is not Dictionary<string, object?> call) continue;

                var target = TryParseReference(call.GetValueOrDefault("m_Target"), out var parsedTarget)
                    ? parsedTarget
                    : new ObjectReferenceInfo { FileId = 0, IsUnset = true };

                hits.Add(new PersistentCallHit
                {
                    EventField = eventField,
                    Index = i,
                    Target = target,
                    TargetAssemblyTypeName = call.GetValueOrDefault("m_TargetAssemblyTypeName") as string is { Length: > 0 } t ? t : null,
                    MethodName = call.GetValueOrDefault("m_MethodName") as string ?? "",
                    Mode = call.GetValueOrDefault("m_Mode") as string ?? "",
                    CallState = call.GetValueOrDefault("m_CallState") as string ?? "",
                    Arguments = call.GetValueOrDefault("m_Arguments") as Dictionary<string, object?>
                        ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                });
            }
        }

        return hits;
    }

    /// <summary>The <c>{fileID[, guid][, type]}</c> shape any Unity object reference is written
    /// as, parsed the same way <see cref="UnityYamlReader"/>'s own internal ToReference does -
    /// fileID required and numeric, guid/type optional. Returns false for anything else (not a
    /// dictionary, or one with no "fileID" key) rather than yielding a bogus reference for it.</summary>
    static bool TryParseReference(object? raw, out ObjectReferenceInfo reference)
    {
        reference = null!;
        if (raw is not Dictionary<string, object?> dict) return false;
        if (dict.GetValueOrDefault("fileID") is not string rawFileId) return false;
        if (!long.TryParse(rawFileId, out var fileId)) return false;

        var guid = dict.GetValueOrDefault("guid") as string is { Length: > 0 } g ? g : null;
        var type = dict.GetValueOrDefault("type") as string is { Length: > 0 } t && int.TryParse(t, out var parsedType)
            ? parsedType
            : (int?)null;

        reference = new ObjectReferenceInfo
        {
            FileId = fileId,
            Guid = guid,
            Type = type,
            IsUnset = fileId == 0 && guid is null,
        };
        return true;
    }

    /// <summary>Recursively finds every dictionary shaped like an object reference (has a
    /// "fileID" key) anywhere under <paramref name="node"/>, with its dotted/indexed path from the
    /// object's own top level. Does not need to recurse INTO a matched reference dict - fileID/
    /// guid/type are always scalars, never containers, so there is nothing further to find there.</summary>
    static IEnumerable<(string Path, Dictionary<string, object?> Dict)> FindReferenceDicts(object? node, string path)
    {
        switch (node)
        {
            case Dictionary<string, object?> dict when dict.ContainsKey("fileID"):
                yield return (path, dict);
                break;

            case Dictionary<string, object?> dict:
                foreach (var (key, value) in dict)
                {
                    foreach (var hit in FindReferenceDicts(value, path.Length == 0 ? key : $"{path}.{key}"))
                        yield return hit;
                }
                break;

            case List<object?> list:
                for (var i = 0; i < list.Count; i++)
                {
                    foreach (var hit in FindReferenceDicts(list[i], $"{path}[{i}]"))
                        yield return hit;
                }
                break;
        }
    }

    /// <summary>Recursively finds every UnityEventBase-shaped substructure anywhere under
    /// <paramref name="node"/> - a dictionary with an "m_PersistentCalls" key whose own value has
    /// an "m_Calls" list - yielding the enclosing field's dotted path and that list. Stops
    /// descending once a match is found: a UnityEvent's own internals hold no nested UnityEvent.</summary>
    static IEnumerable<(string EventField, List<object?> Calls)> FindPersistentCallLists(object? node, string path)
    {
        if (node is not Dictionary<string, object?> dict) yield break;

        if (dict.GetValueOrDefault("m_PersistentCalls") is Dictionary<string, object?> persistentCalls
            && persistentCalls.GetValueOrDefault("m_Calls") is List<object?> calls)
        {
            yield return (path, calls);
            yield break;
        }

        foreach (var (key, value) in dict)
        {
            foreach (var hit in FindPersistentCallLists(value, path.Length == 0 ? key : $"{path}.{key}"))
                yield return hit;
        }
    }
}
