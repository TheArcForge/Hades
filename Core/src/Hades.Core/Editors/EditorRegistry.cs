using Hades.Contract.Wire;

namespace Hades.Core.Editors;

/// <summary>One attached Unity Editor: the hello it sent at connect time, when it connected, and
/// the live session used to talk to it.</summary>
public sealed record AttachedEditor
{
    public required Hello Hello { get; init; }
    public required DateTimeOffset ConnectedAtUtc { get; init; }
    public required EditorSession Session { get; init; }
}

/// <summary>
/// Which Unity Editors are attached, keyed by project GUID (spec #1 §6). Connection-derived, not
/// heartbeat-derived: a hello registers, a disconnect deregisters, and there is no separate
/// liveness timer, transient/stale state machine, or eviction sweep - a dropped socket already
/// means detached, immediately.
///
/// At most one editor per project. Reopening a project while the previous Editor's connection is
/// still tearing down - its socket has not yet noticed it is dead - would otherwise register two
/// editors for one project. Policy: NEWEST WINS. <see cref="Register"/> always overwrites any
/// existing registration for the same project outright; there is no "reject the new connection"
/// option, because the new connection has already completed a real handshake (token + hello) by
/// the time this runs, and refusing it would only leave the user staring at a stale registration
/// while a perfectly good Editor sits connected and ignored. The old session's own eventual
/// <see cref="Deregister"/> call - once its socket finally notices the drop - is guarded to a
/// no-op so it cannot evict the registration that superseded it; see that method.
///
/// Thread-safe: every method takes the same lock for the duration of its dictionary access, so
/// registration, deregistration, and lookups/enumeration are all safe to call concurrently as
/// connections come and go.
/// </summary>
public sealed class EditorRegistry
{
    readonly Dictionary<string, AttachedEditor> _editors = [];
    readonly Lock _gate = new();

    /// <summary>Registers <paramref name="editor"/> under its project GUID. See the class doc
    /// comment for the newest-wins policy this implements.</summary>
    /// <exception cref="ArgumentException"><paramref name="editor"/>'s hello has no project
    /// GUID - the one thing this registry cannot key on.</exception>
    public void Register(AttachedEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var projectGuid = editor.Hello.ProjectGuid;
        if (string.IsNullOrEmpty(projectGuid))
            throw new ArgumentException("AttachedEditor.Hello.ProjectGuid must not be null or empty.", nameof(editor));

        lock (_gate)
        {
            _editors[projectGuid] = editor;
        }
    }

    /// <summary>
    /// Removes the registration for <paramref name="projectGuid"/>, but only while it is still
    /// <paramref name="session"/> - see the class doc comment. A session that already lost the
    /// newest-wins race is never the current registration by the time its own disconnect calls
    /// this, so it becomes a no-op instead of evicting whatever replaced it.
    /// </summary>
    /// <returns><c>true</c> when <paramref name="session"/> was still the current registration
    /// and was actually removed; <c>false</c> for the no-op case above (including an unknown
    /// <paramref name="projectGuid"/>). A caller with its OWN per-project belief that must not
    /// outlive a stale, superseded connection - see <see cref="EditorListener"/>'s lease-clearing
    /// use of this - should gate on this return value rather than re-deriving "was this session
    /// still current" itself, which would reopen the exact race this method's own lock already
    /// closes atomically.</returns>
    public bool Deregister(string projectGuid, EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(projectGuid)) return false;

        lock (_gate)
        {
            if (_editors.TryGetValue(projectGuid, out var current) && ReferenceEquals(current.Session, session))
            {
                _editors.Remove(projectGuid);
                return true;
            }

            return false;
        }
    }

    /// <summary>The attached editor for one project, or null when none is attached.</summary>
    public AttachedEditor? Get(string projectGuid)
    {
        if (string.IsNullOrEmpty(projectGuid)) return null;

        lock (_gate)
        {
            return _editors.GetValueOrDefault(projectGuid);
        }
    }

    /// <summary>Every attached editor. A snapshot copy, safe to enumerate while connections come
    /// and go.</summary>
    public IReadOnlyList<AttachedEditor> All()
    {
        lock (_gate)
        {
            return [.. _editors.Values];
        }
    }
}
