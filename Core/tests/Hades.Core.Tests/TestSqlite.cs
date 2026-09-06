using Microsoft.Data.Sqlite;

namespace Hades.Core.Tests;

/// <summary>
/// Connection strings for the raw SQLite connections tests open alongside the product's own.
///
/// <para><b>Pooling is OFF, for the same reason the product turns it off.</b>
/// <c>Microsoft.Data.Sqlite</c> pools by default, so disposing a connection returns it to the pool
/// with the underlying file handle still open. Unix does not care — an unlinked open file is fine —
/// but Windows refuses to delete a file that anything still holds, so the fixture's
/// <c>Directory.Delete(_dir, recursive: true)</c> throws in <c>Dispose()</c>.</para>
///
/// <para>That failure is worth recognising because of how it presents: the test body passes and
/// xUnit reports the test as FAILED anyway, with an <c>IOException</c> whose stack trace is entirely
/// inside teardown. It looks like the feature under test is broken on Windows. It is not — five
/// tests failed this way while asserting perfectly correct behaviour.</para>
///
/// <para>The product fixed this in <see cref="Hades.Core.Graph.GraphDatabase"/>,
/// <c>MemoryIndex</c> and <c>TraceStore</c>; that fix could not reach connection strings written
/// here in the test assembly, which is why this exists rather than being solved once upstream.</para>
/// </summary>
public static class TestSqlite
{
    public static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
}
