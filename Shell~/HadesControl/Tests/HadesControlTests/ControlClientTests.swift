import Foundation
import Testing

@testable import HadesControl

/// Serialized: every test here drives `MockURLProtocol` through its process-global `handler` -
/// see that type's own doc comment for why.
@Suite("ControlClient", .serialized)
struct ControlClientTests {
    static let connection = ControlConnection(port: 12345, token: "test-token-abc")

    @Test("every request carries Authorization: Bearer <token>, and a 2xx body decodes")
    func attachesBearerTokenAndDecodes() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("ping")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.ping()

        #expect(result.version == "2.0.0-dev")
        #expect(MockURLProtocol.lastRequest?.value(forHTTPHeaderField: "Authorization") == "Bearer test-token-abc")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/ping")
        #expect(MockURLProtocol.lastRequest?.httpMethod == "GET")
    }

    @Test("a 401 throws the distinct, named .staleToken case - not a generic failure")
    func unauthorizedThrowsStaleToken() async throws {
        MockURLProtocol.handler = { _ in .init(status: 401, body: try! Fixtures.data("error_401")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())

        do {
            _ = try await client.summary()
            Issue.record("expected summary() to throw")
        } catch let error {
            guard case .staleToken = error else {
                Issue.record("expected .staleToken, got \(error)")
                return
            }
        }
    }

    @Test("a non-401 error status maps to .server with the server's own message, not invented text")
    func notFoundMapsToServerErrorWithRealMessage() async throws {
        MockURLProtocol.handler = { _ in .init(status: 404, body: try! Fixtures.data("release_unknown_project_404")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())

        do {
            _ = try await client.releaseLease(id: "not-a-real-guid")
            Issue.record("expected releaseLease(id:) to throw")
        } catch let error {
            guard case .server(let status, let message) = error else {
                Issue.record("expected .server, got \(error)")
                return
            }
            #expect(status == 404)
            #expect(message == "Unknown project 'not-a-real-guid'.")
        }
    }

    @Test("releaseLease(id:) POSTs to /control/leases/<percent-encoded id>/release")
    func releaseLeasePostsToEncodedPath() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("release_idempotent_success")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.releaseLease(id: "15c012f27331e49229cef25e74537816")

        #expect(result.success == true)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/leases/15c012f27331e49229cef25e74537816/release")
    }

    @Test("an unreachable core throws .transport, never hangs or crashes")
    func unreachableCoreThrowsTransport() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 3
        let session = URLSession(configuration: configuration)

        // Port 1 (tcpmux): nothing listens there on a normal dev machine, so the connection is
        // refused immediately - proving a real transport failure maps to `.transport` rather than
        // escaping as an untyped error or hanging the caller.
        let unreachable = ControlConnection(port: 1, token: "irrelevant")
        let client = ControlClient(connection: unreachable, session: session)

        do {
            _ = try await client.ping()
            Issue.record("expected ping() to throw")
        } catch let error {
            guard case .transport = error else {
                Issue.record("expected .transport, got \(error)")
                return
            }
        }
    }

    // MARK: - Plan 13 Task 1: request-shape coverage for the 18 added endpoints
    //
    // Every new method funnels through the same private `request`/`send` plumbing already proven
    // above (bearer token, 401 -> .staleToken, non-2xx -> .server, decode -> .decoding) - these
    // tests exist only to prove the mechanics that ARE new: percent-encoded path segments, optional
    // query items that are OMITTED (not sent as empty strings) when nil, and a POST body encoded as
    // JSON, including one call that needs both a query item and a body at once.

    @Test("addProject(path:) POSTs a JSON body to /control/projects/add and decodes the ProjectRow response")
    func addProjectPostsJSONBody() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("projects_add")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.addProject(path: "/Users/mike/Projects/Hades-Unity-Client")

        #expect(result.productGuid == "15c012f27331e49229cef25e74537816")
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/projects/add")
        #expect(MockURLProtocol.lastRequest?.value(forHTTPHeaderField: "Content-Type") == "application/json")

        // AddProjectRequest is Encodable only (a request body this package sends, never receives),
        // so the body it actually put on the wire is read back with a small locally-declared mirror
        // of its shape - same technique as DTODecodingTests.swift's own local `ErrorBody`.
        struct SentBody: Decodable { let path: String }
        let sentBody = try #require(MockURLProtocol.lastRequestBody)
        let decoded = try JSONDecoder().decode(SentBody.self, from: sentBody)
        #expect(decoded.path == "/Users/mike/Projects/Hades-Unity-Client")
    }

    @Test("rebuildProject(productGuid:) POSTs to the percent-encoded productGuid path")
    func rebuildProjectPostsToEncodedPath() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("projects_action_rebuild_started")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.rebuildProject(productGuid: "15c012f27331e49229cef25e74537816")

        #expect(UUID(uuidString: result.operationId) != nil)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/projects/15c012f27331e49229cef25e74537816/rebuild")
    }

    @Test("operation(id:) GETs the percent-encoded operation id path")
    func operationGetsFromEncodedPath() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("operation_running")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.operation(id: "ab147404-1ed5-4e5a-82f8-ae31715d0f2f")

        #expect(result.state == .running)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "GET")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/operations/ab147404-1ed5-4e5a-82f8-ae31715d0f2f")
    }

    @Test("tracesSequences omits nil filters from the query string entirely, never as an empty value - limit included, so the core's own route default decides, not a stale Swift copy of it")
    func tracesSequencesOmitsNilFilters() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("traces_sequences")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        _ = try await client.tracesSequences(project: "15c012f27331e49229cef25e74537816")

        let items = queryItems(of: MockURLProtocol.lastRequest)
        #expect(items["project"] == "15c012f27331e49229cef25e74537816")
        #expect(items["limit"] == nil, "an unspecified limit must not appear at all - the core's own route default (200) decides")
        #expect(items["tool"] == nil, "an omitted filter must not appear at all - not even as 'tool='")
        #expect(items["outcome"] == nil)
        #expect(items["minDurationMs"] == nil)
        #expect(items["maxDurationMs"] == nil)
    }

    @Test("tracesSequences includes every filter that IS provided")
    func tracesSequencesIncludesProvidedFilters() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("traces_sequences")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        _ = try await client.tracesSequences(tool: "search_by_name", outcome: "error", minDurationMs: 10, maxDurationMs: 5000, limit: 50)

        let items = queryItems(of: MockURLProtocol.lastRequest)
        #expect(items["tool"] == "search_by_name")
        #expect(items["outcome"] == "error")
        #expect(items["minDurationMs"] == "10")
        #expect(items["maxDurationMs"] == "5000")
        #expect(items["limit"] == "50")
        #expect(items["project"] == nil)
    }

    @Test("dismissMemoryProposal(confirm:) sends confirm=true in the query string, and fileName alongside it")
    func dismissMemoryProposalIncludesConfirm() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("memory_action_dismiss_proposal")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.dismissMemoryProposal(fileName: "some-proposal.md", confirm: true, project: "guid-123")

        #expect(result.success == true)
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/memory/proposals/dismiss")
        let items = queryItems(of: MockURLProtocol.lastRequest)
        #expect(items["confirm"] == "true")
        #expect(items["fileName"] == "some-proposal.md")
        #expect(items["project"] == "guid-123")
    }

    @Test("writeMemoryDocument sends both query items (project, name) and a JSON body (content) on the same request")
    func writeMemoryDocumentSendsQueryAndBody() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("memory_action_write_document")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.writeMemoryDocument(name: "notes.md", content: "hello", project: "guid-123")

        #expect(result.success == true)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/memory/document")
        let items = queryItems(of: MockURLProtocol.lastRequest)
        #expect(items["name"] == "notes.md")
        #expect(items["project"] == "guid-123")

        // WriteMemoryDocumentRequest is Encodable only - see addProjectPostsJSONBody's own comment.
        struct SentBody: Decodable { let content: String }
        let sentBody = try #require(MockURLProtocol.lastRequestBody)
        let decoded = try JSONDecoder().decode(SentBody.self, from: sentBody)
        #expect(decoded.content == "hello")
    }

    // MARK: - Plan 14 Task 10: /control/migration/* - the missing caller

    @Test("migrationDetect GETs /control/migration/<productGuid>/detect")
    func migrationDetectGetsCorrectPath() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_detect_v12")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationDetect(productGuid: "15c012f27331e49229cef25e74537816")

        #expect(result.isV12Project == true)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "GET")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/15c012f27331e49229cef25e74537816/detect")
    }

    @Test("migrationImportMemory POSTs to /control/migration/<productGuid>/importMemory with no body")
    func migrationImportMemoryPosts() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_import_memory")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationImportMemory(productGuid: "abc123")

        #expect(result.skipped.count == 3)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/abc123/importMemory")
        #expect(MockURLProtocol.lastRequestBody == nil || MockURLProtocol.lastRequestBody?.isEmpty == true)
    }

    @Test("migrationImportTraces POSTs to /control/migration/<productGuid>/importTraces")
    func migrationImportTracesPosts() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_import_traces")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationImportTraces(productGuid: "abc123")

        #expect(result.imported == true)
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/abc123/importTraces")
    }

    @Test("migrationCleanClaudeMd sends {proceed:true} as the JSON body - the wire-level twin of V12Cleanup's required, no-default proceed parameter")
    func migrationCleanClaudeMdSendsProceedInBody() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_clean_claude_md_removed_with_remaining_content")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationCleanClaudeMd(productGuid: "abc123", proceed: true)

        #expect(result.removed == true)
        #expect(result.remainingContentOutsideBlock == true)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/abc123/cleanClaudeMd")

        struct SentBody: Decodable { let proceed: Bool }
        let sentBody = try #require(MockURLProtocol.lastRequestBody)
        #expect(try JSONDecoder().decode(SentBody.self, from: sentBody).proceed == true)
    }

    @Test("migrationCleanManifest sends proceed:false and decodes occurrencesFound/portConflictWarning even when nothing was removed")
    func migrationCleanManifestNoGoAhead() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_clean_manifest_no_go_ahead")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationCleanManifest(productGuid: "abc123", proceed: false)

        #expect(result.removed == false)
        #expect(result.occurrencesFound == 1)
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/abc123/cleanManifest")

        struct SentBody: Decodable { let proceed: Bool }
        let sentBody = try #require(MockURLProtocol.lastRequestBody)
        #expect(try JSONDecoder().decode(SentBody.self, from: sentBody).proceed == false)
    }

    @Test("migrationCleanMcpConfig POSTs to /control/migration/<productGuid>/cleanMcpConfig")
    func migrationCleanMcpConfigPosts() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_clean_mcp_config_removed")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationCleanMcpConfig(productGuid: "abc123", proceed: true)

        #expect(result.removed == true)
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/abc123/cleanMcpConfig")
    }

    @Test("migrationCleanClaudeDesktopConfig POSTs to the GLOBAL route - no productGuid anywhere in the path")
    func migrationCleanClaudeDesktopConfigPostsToGlobalRoute() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_clean_claude_desktop_config_removed")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationCleanClaudeDesktopConfig(proceed: true)

        #expect(result.removed == true)
        #expect(result.scopeWarning.localizedCaseInsensitiveContains("global"))
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/claudeDesktopConfig/clean")

        struct SentBody: Decodable { let proceed: Bool }
        let sentBody = try #require(MockURLProtocol.lastRequestBody)
        #expect(try JSONDecoder().decode(SentBody.self, from: sentBody).proceed == true)
    }

    @Test("migrationCleanHadesHub POSTs to the GLOBAL route - no productGuid anywhere in the path")
    func migrationCleanHadesHubPostsToGlobalRoute() async throws {
        MockURLProtocol.handler = { _ in .init(status: 200, body: try! Fixtures.data("migration_clean_hades_hub_removed")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())
        let result = try await client.migrationCleanHadesHub(proceed: true)

        #expect(result.removed == true)
        #expect(result.found == true)
        #expect(MockURLProtocol.lastRequest?.httpMethod == "POST")
        #expect(MockURLProtocol.lastRequest?.url?.path == "/control/migration/hadesHub/clean")

        struct SentBody: Decodable { let proceed: Bool }
        let sentBody = try #require(MockURLProtocol.lastRequestBody)
        #expect(try JSONDecoder().decode(SentBody.self, from: sentBody).proceed == true)
    }

    @Test("an unknown productGuid maps migrationDetect's 404 to .server with the real message")
    func migrationDetectUnknownProjectMapsToServerError() async throws {
        MockURLProtocol.handler = { _ in .init(status: 404, body: try! Fixtures.data("migration_detect_unknown_project_404")) }
        defer { MockURLProtocol.handler = nil }

        let client = ControlClient(connection: Self.connection, session: MockURLProtocol.makeSession())

        do {
            _ = try await client.migrationDetect(productGuid: "not-a-real-guid")
            Issue.record("expected migrationDetect to throw")
        } catch let error {
            guard case .server(let status, let message) = error else {
                Issue.record("expected .server, got \(error)")
                return
            }
            #expect(status == 404)
            #expect(message == "Unknown project 'not-a-real-guid'.")
        }
    }
}

/// Parses a request's query string into a `[name: value]` dictionary for assertions above - a
/// membership check rather than a raw string comparison, so these tests do not depend on this
/// package's own (unspecified) query-item ordering.
private func queryItems(of request: URLRequest?) -> [String: String] {
    guard let url = request?.url,
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
    else { return [:] }

    return Dictionary(items.map { ($0.name, $0.value ?? "") }, uniquingKeysWith: { first, _ in first })
}
