// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "HadesApp",
    platforms: [
        .macOS(.v14)
    ],
    dependencies: [
        .package(path: "../HadesControl"),
        .package(path: "../HadesSupervision"),
    ],
    targets: [
        // The menu bar app: AppKit/SwiftUI glue (AppDelegate, MenuBarController, SwiftUI views)
        // AND the testable state-mapping layer (MenuBarContent, StatusIcon, MenuBarViewModel) in
        // one target. Deliberately not split into a separate library target: a single executable
        // target whose entry point is a `@main` TYPE (never a file literally named `main.swift`,
        // which SwiftPM disallows testable-importing) is directly `@testable import`-able under
        // Swift 6.3.3 / Xcode 26.6 - verified empirically with a throwaway probe package (an
        // AppKit-importing executable target + a test target depending on it) before relying on
        // it, exactly per this project's "verified empirically, not assumed" standard. See the
        // Plan 12 Task 3 report for that probe.
        //
        // `xcodebuild` builds this target non-interactively with no checked-in .xcodeproj: Xcode
        // 26.6 auto-generates a scheme per product/target for a bare SwiftPM manifest (confirmed
        // with `xcodebuild -list` / `xcodebuild build -scheme HadesApp -destination
        // 'platform=macOS'` against the same probe package). `scripts/build-app.sh` drives exactly
        // that to produce the real .app bundle NSStatusItem requires - see that script's own
        // header comment for the bundling step itself.
        .executableTarget(
            name: "HadesApp",
            dependencies: [
                .product(name: "HadesControl", package: "HadesControl"),
                .product(name: "HadesSupervision", package: "HadesSupervision"),
            ]
        ),
        .testTarget(
            name: "HadesAppTests",
            dependencies: [
                "HadesApp",
                .product(name: "HadesControl", package: "HadesControl"),
                .product(name: "HadesSupervision", package: "HadesSupervision"),
            ]
        ),
    ]
)
