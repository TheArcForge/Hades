import Foundation

/// Which CPU-architecture slice of THIS process is executing right now - not which chip is
/// physically in the Mac. See `ArchitectureGate`'s own doc comment for exactly why that distinction,
/// not "what chip is this Mac", is the question that actually matters here: a universal binary
/// launched normally always runs its native slice, so on Apple Silicon this is `.appleSilicon` and
/// on a genuine Intel Mac this is `.intel` - the two only part ways through a user's own deliberate
/// choice to force the other slice (see `ArchitectureGate.currentSlice`'s own doc comment for that
/// one corner case and why it is not handled specially).
public enum ExecutingArchitecture: Equatable, Sendable {
    case appleSilicon
    case intel
}

/// **Release blocker #3 (external tester report): "arm64-only, and the current failure mode is the
/// worst available: the DMG opens on an Intel Mac, allows the drag to Applications, then the app
/// silently fails to launch."** Product's fix is not a universal .NET core - `scripts/build-app.sh`'s
/// own "osx-arm64 only, not a universal build" comment on the `dotnet publish` step still applies
/// unchanged, and for the same reasons it already gives. The fix is a universal SWIFT SHELL that can
/// run far enough on Intel to say so, in plain language, then quit cleanly - instead of the OS
/// silently refusing to start an arm64-only Mach-O at all, which is what a drag-installed arm64-only
/// Hades.app does today on an Intel Mac.
///
/// **Why Swift has to be the one to decide and say this, with no .NET involved at all.** This
/// project already carves out a narrow exception to "Swift renders, .NET decides" for exactly this
/// shape of fact - see `ResourceGuardReading`'s own doc comment ("an OS fact about the shell's own
/// process or machine is Swift's") and `LaunchAtLoginReading`'s. Which architecture this process is
/// currently running as is about as pure an instance of that carve-out as exists. On a genuine Intel
/// Mac there is not even a .NET side to defer to in principle: the embedded core is published `-r
/// osx-arm64 --self-contained`, and arm64-only machine code cannot execute on Intel silicon at all.
/// Only code that can itself run on Intel can ever detect Intel and say so - which is the entire
/// reason `build-app.sh` now builds this shell for both architectures while the core stays
/// arm64-only (see that script's own comment next to its `ARCHS` setting).
///
/// **`HadesMenuBarApp.main()` calls `decide(for: currentSlice)` as its very first decision** - before
/// `NSApp.setActivationPolicy`, before the main menu, before `AppDelegate` exists, before anything
/// that would create a window, a menu-bar item, or attempt to spawn the core, and before anything
/// that would touch `~/Library/Application Support/Hades`. A refusal here is the only path through
/// this app that never reaches any of that.
public enum ArchitectureGate {
    /// The outcome of the gate: either launch proceeds exactly as it always has (Apple Silicon), or
    /// it must stop with this exact message and do nothing else (Intel). Never a third option, and
    /// never decided anywhere but here.
    public enum Decision: Equatable, Sendable {
        case proceed
        case refuse(message: String)
    }

    /// Every fact release blocker #3 itself asks for, and nothing else: Hades requires Apple
    /// Silicon; this Mac has an Intel processor instead; there is no Intel version today; there is
    /// nothing on this Mac to install or change that would fix that. Deliberately silent about *why*
    /// (arm64, x86_64, "universal binary", Rosetta) - none of that is this user's problem to
    /// understand, and naming Rosetta specifically would read as a workaround worth trying when none
    /// exists for a real Intel Mac (Rosetta translates x86_64 code to run on Apple Silicon; there is
    /// no direction that helps an Intel Mac run arm64 code). Hand-typed, never built from a format
    /// string with a variable in it - the same "no re-derivation" discipline `ThermalStateDisplay`
    /// holds its own fixed strings to - so `ArchitectureGateTests` can assert this exact text without
    /// tautologically re-deriving it from the code under test.
    public static let unsupportedMessage = """
        Hades requires an Apple Silicon Mac, and this Mac has an Intel processor. Hades doesn't have a version that runs on Intel Macs today.

        There's nothing to install or change on this Mac that would fix this. Hades will now quit.
        """

    /// The pure decision at the heart of the gate - a value in, a value out, no AppKit, no process
    /// exit, nothing that requires real Intel hardware or an actual x86_64 build slice to exercise.
    /// See `HadesMenuBarApp.main()` for the one production call site, and `currentSlice`'s own doc
    /// comment for the one fact this decision depends on that a unit test genuinely cannot reach.
    public static func decide(for slice: ExecutingArchitecture) -> Decision {
        switch slice {
        case .appleSilicon: return .proceed
        case .intel: return .refuse(message: unsupportedMessage)
        }
    }

    /// The one fact this gate depends on that `decide(for:)` itself cannot compute: which slice of
    /// THIS exact binary is executing right now. `#if arch(x86_64)` - a compile-time branch, not a
    /// runtime check - is the correct tool for that, evaluated against the two real alternatives:
    ///
    /// - A runtime hardware check (`sysctl` `hw.optional.arm64`, or `sysctl.proc_translated` to
    ///   detect Rosetta translation) answers a DIFFERENT question - "what chip is in this Mac" -
    ///   which happens to coincide with the question that matters here in every case except one: a
    ///   user manually forcing this app's x86_64 slice to run under Rosetta on an Apple Silicon Mac
    ///   (Finder > Get Info > "Open using Rosetta"). That is a deliberate, self-inflicted choice to
    ///   emulate Intel instead of running the native arm64 slice already sitting in the same bundle -
    ///   not the silent-failure scenario release blocker #3 reports, and not worth the real runtime
    ///   C-interop surface (`sysctlbyname`, buffer sizing, `errno` handling) a correct
    ///   Rosetta-aware check would require, spent on a corner nobody reaches by accident.
    /// - `NSRunningApplication` is not a candidate at all - every property it exposes describes
    ///   OTHER running applications (front-most app, activation policy, bundle identifier), never
    ///   this process's own CPU architecture. It answers no version of this question.
    ///
    /// `#if arch(x86_64)` costs nothing at runtime and cannot be spoofed by anything short of
    /// literally executing the other slice: the Swift compiler builds this branch into the x86_64
    /// slice ALONE (see `scripts/build-app.sh`'s own `ARCHS="x86_64 arm64"` comment) - the arm64
    /// slice's Mach-O does not contain this code at all, the same way `#if arch(arm64)` code would
    /// not exist in the x86_64 slice. Because a universal binary launched normally always runs its
    /// native slice - never arm64 under Rosetta, which is not a real combination - "the x86_64 slice
    /// is executing" and "this is a genuine Intel Mac" are the same fact in every case this gate
    /// needs to handle.
    public static var currentSlice: ExecutingArchitecture {
        #if arch(x86_64)
        return .intel
        #else
        return .appleSilicon
        #endif
    }
}
