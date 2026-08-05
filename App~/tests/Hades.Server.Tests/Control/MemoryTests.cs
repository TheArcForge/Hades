using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Hades.Server.Control;

namespace Hades.Server.Tests.Control;

/// <summary>
/// <c>GET /control/memory</c> (documents + the proposal queue in one response, since spec #3 §3.4
/// is one shell view), <c>GET</c>/<c>POST /control/memory/document</c> (read/write one authored
/// document directly - the shell's own text-editor save path), and
/// <c>POST /control/memory/proposals/{accept,dismiss,defer}</c> - over real HTTP against a
/// directly-constructed <see cref="ControlListener"/>. Same style as every other Control
/// *EndpointHttpTests class in this directory.
///
/// <b>Why <c>name</c>/<c>fileName</c> are query-string parameters, not route segments.</b> A
/// traversal attack string like "sub/dir/file" or "/etc/passwd" would not reliably reach a
/// <c>{name}</c> ROUTE parameter as one opaque value - ASP.NET Core's own routing treats an
/// unencoded "/" as a segment boundary, so the attack string this task explicitly requires testing
/// could be rejected by ROUTING itself before ever reaching <see cref="MemoryEndpoint"/>'s own
/// validation, which would prove nothing about that validation. A query-string value has no such
/// ambiguity - it reaches the handler exactly as given, so these tests prove
/// <see cref="MemoryEndpoint"/>'s OWN basename validation (via
/// <see cref="Hades.Core.Memory.MemoryStore.ValidatedChildPath"/>), not routing's incidental
/// behaviour.
/// </summary>
public sealed class MemoryEndpointHttpTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff88800008";

    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    readonly ProjectService _projects;

    public MemoryEndpointHttpTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        _projects.Adopt(_projectRoot);
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? origin = null, object? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (jsonBody is not null) request.Content = JsonContent.Create(jsonBody);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    ControlListener StartListener() => new(ConnectionFilePath, projects: _projects);

    // ---------------------------------------------------------------- auth / Origin sweep

    public static IEnumerable<object[]> GetRoutes()
    {
        yield return ["/control/memory"];
        yield return ["/control/memory/document?name=conventions.md"];
    }

    [Theory]
    [MemberData(nameof(GetRoutes))]
    public async Task Get_NoToken_IsRefused(string path)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"{path}{(path.Contains('?') ? "&" : "?")}project={ProjectGuid}", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GetRoutes))]
    public async Task Get_ForeignOrigin_IsRejectedWith403(string path)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"{path}{(path.Contains('?') ? "&" : "?")}project={ProjectGuid}", listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostDocument_NoToken_IsRefused()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/memory/document?project={ProjectGuid}&name=x.md", token: null, jsonBody: new { content = "x" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------- GET /control/memory

    [Fact]
    public async Task Get_NoMemoryYet_IsAWellFormedEmptyResponse()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("documents").GetArrayLength());
        Assert.Equal(0, body.GetProperty("proposals").GetArrayLength());
    }

    [Fact]
    public async Task Get_DocumentsAndProposals_BothAppear_InOneResponse()
    {
        _projects.WriteMemoryDocument(ProjectGuid, "conventions.md", "---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n");
        _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "Use object pooling.", "Seen 3 times.");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var doc = Assert.Single(body.GetProperty("documents").EnumerateArray());
        Assert.Equal("conventions.md", doc.GetProperty("name").GetString());
        Assert.Equal("2026-05-12", doc.GetProperty("lastReviewed").GetString());

        var proposal = Assert.Single(body.GetProperty("proposals").EnumerateArray());
        Assert.Equal("patterns.md", proposal.GetProperty("targetFile").GetString());
        Assert.Equal("pending", proposal.GetProperty("status").GetString());
        Assert.Equal("Use object pooling.", proposal.GetProperty("content").GetString());
    }

    // ---------------------------------------------------------------- Plan 11 Task 7 audit fixes:
    // sizeDisplay and createdAgo - sizeBytes/createdAtUtc alone forced a shell to convert bytes to
    // KB/MB, or subtract a raw timestamp from "now", itself (the "formatting a duration"/"raw
    // timestamps" violations the audit looks for) just to draw a document list or a proposal queue -
    // exactly the two views spec #3 §3.4 describes as one shell surface.

    [Fact]
    public async Task Get_DocumentSizeDisplay_IsAHumanReadableUnit_NotJustRawBytesTheShellMustConvert()
    {
        _projects.WriteMemoryDocument(ProjectGuid, "small.md", new string('a', 500));
        _projects.WriteMemoryDocument(ProjectGuid, "medium.md", new string('a', 2048));

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var docs = body.GetProperty("documents").EnumerateArray()
            .ToDictionary(d => d.GetProperty("name").GetString()!, d => d);

        Assert.Equal(500, docs["small.md"].GetProperty("sizeBytes").GetInt64());
        Assert.Equal("500 B", docs["small.md"].GetProperty("sizeDisplay").GetString());
        Assert.Equal("2.0 KB", docs["medium.md"].GetProperty("sizeDisplay").GetString());
    }

    [Fact]
    public async Task Get_ProposalCreatedAgo_IsAResolvedRelativeTimeString_NotARawTimestampTheShellMustFormat()
    {
        _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "content", "why");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var proposal = Assert.Single(body.GetProperty("proposals").EnumerateArray());
        Assert.True(proposal.TryGetProperty("createdAtUtc", out _));
        // Created moments ago by ProposeMemoryUpdate itself, on the same real clock this listener's
        // own default utcNow uses - the exact second is not pinned (real-clock timing), only the
        // shape: a resolved "<n>s ago" string, never a raw timestamp left for the shell to subtract.
        Assert.Matches(@"^\d+s ago$", proposal.GetProperty("createdAgo").GetString());
    }

    // ---------------------------------------------------------------- GET /control/memory/document

    [Fact]
    public async Task GetDocument_DoesNotExist_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory/document?project={ProjectGuid}&name=nope.md", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDocument_Exists_ReturnsRawContentByteForByte()
    {
        const string content = "---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n\nSome prose.\n";
        _projects.WriteMemoryDocument(ProjectGuid, "conventions.md", content);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory/document?project={ProjectGuid}&name=conventions.md", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("conventions.md", body.GetProperty("name").GetString());
        Assert.Equal(content, body.GetProperty("content").GetString());
    }

    // ---------------------------------------------------------------- POST /control/memory/document (write one)

    [Fact]
    public async Task WriteDocument_CreatesIt_SubsequentGetReflectsIt()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var writeResponse = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/document?project={ProjectGuid}&name=decisions.md", listener.Token,
            jsonBody: new { content = "# Decisions\n\nWe chose X because Y.\n" }));
        Assert.Equal(HttpStatusCode.OK, writeResponse.StatusCode);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/control/memory/document?project={ProjectGuid}&name=decisions.md", listener.Token));
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("# Decisions\n\nWe chose X because Y.\n", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task WriteDocument_Overwrites_AnExistingDocument()
    {
        _projects.WriteMemoryDocument(ProjectGuid, "decisions.md", "old content");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        await client.SendAsync(Request(HttpMethod.Post, $"/control/memory/document?project={ProjectGuid}&name=decisions.md", listener.Token,
            jsonBody: new { content = "new content" }));

        var file = _projects.ReadMemoryDocument(ProjectGuid, "decisions.md");
        Assert.Equal("new content", file!.RawText);
    }

    // ---------------------------------------------------------------- basename validation on every write path
    // (mirrors MemoryStoreTests.Write_RejectsAnyNameThatCouldEscapeTheMemoryDirectory's own theory data)

    public static IEnumerable<object[]> UnsafeNames()
    {
        yield return [""];
        yield return ["   "];
        yield return [".."];
        yield return ["../escape"];
        yield return ["/etc/passwd"];
        yield return ["sub/dir/file"];
    }

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public async Task GetDocument_UnsafeName_Returns400_NeverA500OrAFileOutsideMemoryDir(string name)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/control/memory/document?project={ProjectGuid}&name={Uri.EscapeDataString(name)}", listener.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public async Task WriteDocument_UnsafeName_Returns400_NeverA500OrAFileOutsideMemoryDir(string name)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/document?project={ProjectGuid}&name={Uri.EscapeDataString(name)}", listener.Token,
            jsonBody: new { content = "malicious" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists("/etc/passwd-hades-test-marker")); // sanity: nothing written outside the sandbox
    }

    [Fact]
    public async Task WriteDocument_EmptyName_OmittedEntirely_Returns400()
    {
        // The empty-string case above still sends "name=" on the wire; this proves the OTHER real
        // shape - the query key missing altogether, binding 'name' to null - is equally rejected,
        // not a NullReferenceException.
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/memory/document?project={ProjectGuid}", listener.Token,
            jsonBody: new { content = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- proposals: accept

    [Fact]
    public async Task AcceptProposal_UnknownFileName_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/memory/proposals/accept?project={ProjectGuid}&fileName=nope.md", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public async Task AcceptProposal_UnsafeFileName_Returns400(string fileName)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/accept?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AcceptProposal_NoExistingTargetDocument_CreatesItFromTheProposalContent()
    {
        var written = _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "Use object pooling for bullets.", "Seen 3 times.")!;
        var fileName = written.FileName; // defect fix: already the bare basename, no stripping needed

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/accept?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());

        var target = _projects.ReadMemoryDocument(ProjectGuid, "patterns.md");
        Assert.NotNull(target);
        Assert.Equal("Use object pooling for bullets.", target!.RawText);
    }

    [Fact]
    public async Task AcceptProposal_ExistingTargetDocument_AppendsRatherThanOverwriting()
    {
        _projects.WriteMemoryDocument(ProjectGuid, "patterns.md", "# Patterns\n\nExisting entry.\n");
        var written = _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "New proposed entry.", "Why")!;
        var fileName = written.FileName; // defect fix: already the bare basename, no stripping needed

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/accept?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));

        var target = _projects.ReadMemoryDocument(ProjectGuid, "patterns.md");
        Assert.NotNull(target);
        Assert.Contains("Existing entry.", target!.RawText);
        Assert.Contains("New proposed entry.", target.RawText);
        // Nothing a human already authored may be discarded - the pre-existing text stays intact,
        // the new text is appended after it, never the reverse and never a replacement.
        Assert.True(target.RawText.IndexOf("Existing entry.", StringComparison.Ordinal)
            < target.RawText.IndexOf("New proposed entry.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptProposal_NeverDeletesTheProposalFile_OnlyMarksItAccepted()
    {
        // "Accepting a proposal is the only path that writes an authored document, and nothing
        // deletes without an explicit confirm flag" - accept takes no confirm flag at all, so it
        // must never be the thing that removes the proposal from disk.
        var written = _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "content", "why")!;
        var fileName = written.FileName; // defect fix: already the bare basename, no stripping needed

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/accept?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));

        var proposal = _projects.ReadMemoryProposal(ProjectGuid, fileName);
        Assert.NotNull(proposal);
        Assert.Equal("accepted", proposal!.Status);
    }

    // ---------------------------------------------------------------- proposals: dismiss

    [Fact]
    public async Task DismissProposal_WithoutConfirm_Returns400_AndTheProposalStillExists()
    {
        var written = _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "content", "why")!;
        var fileName = written.FileName; // defect fix: already the bare basename, no stripping needed

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/dismiss?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(_projects.ReadMemoryProposal(ProjectGuid, fileName));
    }

    [Fact]
    public async Task DismissProposal_WithConfirmTrue_DeletesIt()
    {
        var written = _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "content", "why")!;
        var fileName = written.FileName; // defect fix: already the bare basename, no stripping needed

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/dismiss?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}&confirm=true", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Null(_projects.ReadMemoryProposal(ProjectGuid, fileName));
    }

    [Fact]
    public async Task DismissProposal_UnknownFileName_EvenWithConfirm_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/dismiss?project={ProjectGuid}&fileName=nope.md&confirm=true", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public async Task DismissProposal_UnsafeFileName_Returns400_EvenWithConfirm(string fileName)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/dismiss?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}&confirm=true", listener.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- proposals: defer

    [Fact]
    public async Task DeferProposal_MarksItDeferred_NeverDeletesOrWritesAnAuthoredDocument()
    {
        _projects.WriteMemoryDocument(ProjectGuid, "patterns.md", "original");
        var written = _projects.ProposeMemoryUpdate(ProjectGuid, "patterns.md", "content", "why")!;
        var fileName = written.FileName; // defect fix: already the bare basename, no stripping needed

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/defer?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("success").GetBoolean());

        var proposal = _projects.ReadMemoryProposal(ProjectGuid, fileName);
        Assert.NotNull(proposal);
        Assert.Equal("deferred", proposal!.Status);

        // The authored document must be untouched - defer is pure bookkeeping.
        Assert.Equal("original", _projects.ReadMemoryDocument(ProjectGuid, "patterns.md")!.RawText);
    }

    [Fact]
    public async Task DeferProposal_UnknownFileName_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/memory/proposals/defer?project={ProjectGuid}&fileName=nope.md", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public async Task DeferProposal_UnsafeFileName_Returns400(string fileName)
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/control/memory/proposals/defer?project={ProjectGuid}&fileName={Uri.EscapeDataString(fileName)}", listener.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _tempDir, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// Pure, deterministic tests of the two Plan 11 Task 7 audit fixes - <see cref="MemoryEndpoint.FormatSize"/>
/// and <see cref="MemoryEndpoint.FormatRelativeAge"/> - hand-typed literals, no clock, no I/O, same
/// "verbatim" discipline as SummaryResolveTests/ProjectsResolveTests. See
/// <see cref="MemoryEndpointHttpTests"/>'s own sizeDisplay/createdAgo tests for proof these are
/// actually wired into the real <c>GET /control/memory</c> response.
/// </summary>
public sealed class MemoryEndpointFormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(1_048_575, "1024.0 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(5_242_880, "5.0 MB")]
    public void FormatSize_ReturnsTheExpectedUnitAndPrecision(long bytes, string expected)
    {
        Assert.Equal(expected, MemoryEndpoint.FormatSize(bytes));
    }

    [Theory]
    [InlineData(0, "0s ago")]
    [InlineData(12, "12s ago")]
    [InlineData(59, "59s ago")]
    [InlineData(60, "1m ago")]
    [InlineData(300, "5m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(10_800, "3h ago")]
    [InlineData(86_400, "1d ago")]
    [InlineData(172_800, "2d ago")]
    public void FormatRelativeAge_ReturnsTheExpectedUnitTier(double seconds, string expected)
    {
        Assert.Equal(expected, MemoryEndpoint.FormatRelativeAge(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatRelativeAge_NegativeAge_ClampsToZero_NeverPrintsAMinusSign()
    {
        // A future createdAt (clock skew between processes) must never render as e.g. "-3s ago" -
        // same clamping convention SummaryEndpoint.FormatAge/ProjectsEndpoint.FormatAge already use.
        Assert.Equal("0s ago", MemoryEndpoint.FormatRelativeAge(TimeSpan.FromSeconds(-5)));
    }
}
