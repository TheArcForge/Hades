// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "HadesSupervision",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(name: "HadesSupervision", targets: ["HadesSupervision"])
    ],
    dependencies: [
        .package(path: "../HadesControl")
    ],
    targets: [
        // The supervisor itself: adopt-or-spawn, restart-with-backoff, ownership. Depends on
        // HadesControl for Discovery (finding the core) and ControlClient (pinging it) - see
        // Plan 12 Task 2's report for why supervision lives in its own package rather than only
        // inside the (not-yet-built) HadesApp target: `swift test` cannot reach code that only
        // exists inside an .app bundle target.
        .target(
            name: "HadesSupervision",
            dependencies: [
                .product(name: "HadesControl", package: "HadesControl")
            ]
        ),

        // The parent-death watchdog. A separate, minimal executable - not a library API - because
        // the mechanism fundamentally requires a second OS process: when the app is SIGKILLed, no
        // code inside the app can run to clean up after itself, so something outside the app has
        // to notice and react. See its own doc comment for the getppid()-polling mechanism and why
        // it was chosen over a pipe or kqueue.
        .executableTarget(name: "HadesCoreReaper"),

        // Test fixture: a minimal stand-in for Hades.Server that speaks just enough of the control
        // API (GET /control/ping, token-checked) to exercise CoreSupervisor's adopt/spawn/restart
        // logic without depending on `dotnet` being installed or fast to cold-start. Not part of
        // the public product list - internal to the package, used only by tests.
        .executableTarget(name: "FakeCore"),

        // Test fixture: a tiny process that embeds CoreSupervisor directly (the way the real
        // HadesApp target will) so the force-kill test can SIGKILL something that is genuinely
        // "an app with a supervised core", not just the reaper in isolation. See
        // ReaperForceKillTests.swift for why this exists alongside the more direct reaper-only test.
        .executableTarget(
            name: "FakeAppHarness",
            dependencies: ["HadesSupervision"]
        ),

        .testTarget(
            name: "HadesSupervisionTests",
            dependencies: [
                "HadesSupervision",
                .product(name: "HadesControl", package: "HadesControl"),
            ]
        ),
    ]
)
