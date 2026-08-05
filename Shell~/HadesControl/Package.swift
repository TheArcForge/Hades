// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "HadesControl",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(name: "HadesControl", targets: ["HadesControl"])
    ],
    targets: [
        .target(
            name: "HadesControl"
        ),
        .testTarget(
            name: "HadesControlTests",
            dependencies: ["HadesControl"],
            resources: [
                .copy("Fixtures")
            ]
        ),
    ]
)
