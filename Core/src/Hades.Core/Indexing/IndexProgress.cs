namespace Hades.Core.Indexing;

/// <summary>
/// One incremental report from a long index: which phase is running, how far through it is, and how
/// many files that phase has to do.
///
/// <para><b>Per phase, not one global total.</b> Scripts and assets are separate walks over
/// different file sets, and the second one's size is not known while the first is still running. A
/// single "N of M" would therefore have to either guess M or grow it mid-run, and a total that
/// climbs while you watch is worse than no total. Naming the phase keeps every number it reports
/// true at the moment it is reported.</para>
///
/// <para>Rendering is the caller's business: <see cref="Format"/> exists so the several places that
/// show this to a person word it identically, not because the shape carries presentation.</para>
/// </summary>
/// <param name="Phase">"Scripts", "Assets" - what is being walked.</param>
/// <param name="Completed">Files finished in this phase.</param>
/// <param name="Total">Files this phase will visit. Known before the phase starts, because the walk
/// is materialised up front - the enumeration is cheap next to the parsing that follows it.</param>
public readonly record struct IndexProgressUpdate(string Phase, int Completed, int Total)
{
    /// <summary>The one authored wording, so the shell, the CLI and anything else agree.</summary>
    public string Format() =>
        Total > 0
            ? $"{Phase}: {Completed:N0} of {Total:N0} files"
            : $"{Phase}: {Completed:N0} files";
}
