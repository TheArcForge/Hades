import Foundation

/// A `URLProtocol` stub so `ControlClientTests` can exercise `ControlClient`'s request/response
/// handling - the Authorization header, status-code mapping, body decoding - without a real
/// network call or a live Hades core.
///
/// `handler`/`lastRequest` are process-global mutable state: `URLProtocol` gives no per-request
/// hook a `URLSession` instance could carry its own closure through, so every test that uses this
/// must run serially. `ControlClientTests` is marked `@Suite(.serialized)` for exactly this
/// reason - do not add a parallel test elsewhere that also sets `MockURLProtocol.handler`.
final class MockURLProtocol: URLProtocol, @unchecked Sendable {
    struct StubResponse {
        let status: Int
        let body: Data

        init(status: Int, body: Data = Data()) {
            self.status = status
            self.body = body
        }
    }

    nonisolated(unsafe) static var handler: (@Sendable (URLRequest) -> StubResponse)?
    nonisolated(unsafe) static var lastRequest: URLRequest?

    /// The body `ControlClient` actually sent, for tests that must inspect it (a POST's JSON body -
    /// nothing before Plan 13 Task 1 needed this, since every phase-one POST was bodyless).
    /// `URLRequest.httpBody` is unreliable to read back here: `URLSession` converts it to
    /// `httpBodyStream` internally before handing the request to a `URLProtocol`, so `.httpBody`
    /// alone is `nil` for a real request even though the caller set it - this reads whichever of
    /// the two is actually present.
    nonisolated(unsafe) static var lastRequestBody: Data?

    /// A session that routes every request through this stub instead of the network.
    static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MockURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        MockURLProtocol.lastRequest = request
        MockURLProtocol.lastRequestBody = Self.bodyData(of: request)

        guard let handler = MockURLProtocol.handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
            return
        }

        let stub = handler(request)
        let response = HTTPURLResponse(
            url: request.url!,
            statusCode: stub.status,
            httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json; charset=utf-8"]
        )!

        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    /// Reads `request.httpBody` when present, otherwise drains `request.httpBodyStream` - see
    /// `lastRequestBody`'s own doc comment for why both paths exist.
    private static func bodyData(of request: URLRequest) -> Data? {
        if let httpBody = request.httpBody { return httpBody }

        guard let stream = request.httpBodyStream else { return nil }

        stream.open()
        defer { stream.close() }

        var data = Data()
        let bufferSize = 4096
        var buffer = [UInt8](repeating: 0, count: bufferSize)
        while stream.hasBytesAvailable {
            let bytesRead = stream.read(&buffer, maxLength: bufferSize)
            if bytesRead <= 0 { break }
            data.append(buffer, count: bytesRead)
        }
        return data
    }
}
