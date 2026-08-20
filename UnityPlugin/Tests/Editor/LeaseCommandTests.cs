// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The lease.acquire / lease.renew / lease.release JSON-RPC commands HadesBoot dispatches to
    /// (see HadesBoot.HandleRequest(ReloadGate, JsonRpcRequest) - a testable overload that takes
    /// an explicit gate rather than HadesBoot's own process-wide singleton, exactly so this suite
    /// can exercise real dispatch logic against a ReloadGate built on a FakeEditorLockApi instead
    /// of ever touching Unity's real reload-assemblies lock).
    ///
    /// No MainThreadPump ticking anywhere in this file: lease.acquire/renew/release call straight
    /// through to ReloadGate's synchronous Acquire/Renew/Release, same as any other lease.release
    /// RPC handler would - see ReloadReleasePathTests' Path 1 test ("What a 'lease.release' RPC
    /// handler calls - synchronously, already on the main thread by construction ... so no pump
    /// involvement is needed or expected here"). The TTL watchdog is parked with an hour-long
    /// poll interval in every test here, same reasoning as that file's isolation convention.
    ///
    /// Same SessionState hygiene as ReloadGateTests/ReloadReleasePathTests/ReloadLeaseTests: real,
    /// process-wide Unity state that outlives any single test.
    /// </summary>
    [TestFixture]
    public sealed class LeaseCommandTests
    {
        [SetUp]
        public void SetUp() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        [TearDown]
        public void TearDown() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        static JsonRpcRequest LeaseRequest(string method, string leaseId, double? ttlSeconds = null)
        {
            var @params = JsonValue.NewObject();
            if (leaseId != null) @params.SetProperty("leaseId", JsonValue.String(leaseId));
            if (ttlSeconds.HasValue) @params.SetProperty("ttlSeconds", JsonValue.Float(ttlSeconds.Value));
            return new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };
        }

        static bool ResultSuccess(JsonValue result) =>
            result.TryGetProperty("success", out var value) && value.AsBoolean();

        static string ResultLeaseId(JsonValue result) =>
            result.TryGetProperty("leaseId", out var value) && value.Kind == JsonValueKind.String ? value.AsString() : null;

        static long? ResultExpiresAtUtcMs(JsonValue result) =>
            result.TryGetProperty("expiresAtUtcMs", out var value) && value.Kind == JsonValueKind.Integer
                ? (long?)value.AsInteger()
                : null;

        static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // ---------------------------------------------------------------- lease.acquire

        [Test]
        public void Acquire_WhenNothingHeld_SucceedsAndReturnsTheAppliedExpiry()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromHours(1));

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.acquire", "lease-1"));

            Assert.IsTrue(ResultSuccess(result));
            Assert.AreEqual("lease-1", ResultLeaseId(result));
            Assert.AreEqual(ToUnixMs(clock + ReloadGate.DefaultTtl), ResultExpiresAtUtcMs(result));
            Assert.AreEqual(1, fake.LockCalls);
        }

        [Test]
        public void Acquire_WithExplicitTtlSeconds_AppliesThatTtl_NotTheDefault()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromHours(1));

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.acquire", "lease-1", ttlSeconds: 5));

            Assert.IsTrue(ResultSuccess(result));
            Assert.AreEqual(ToUnixMs(clock.AddSeconds(5)), ResultExpiresAtUtcMs(result));
        }

        [Test]
        public void Acquire_WhenADifferentLeaseAlreadyHolds_FailsAndReportsTheActualHolder()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromHours(1));
            gate.Acquire("owner", TimeSpan.FromSeconds(20));

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.acquire", "impostor"));

            Assert.IsFalse(ResultSuccess(result));
            Assert.AreEqual("owner", ResultLeaseId(result), "a rejected acquire must report the ACTUAL holder, not the rejected request");
            Assert.AreEqual(ToUnixMs(clock.AddSeconds(20)), ResultExpiresAtUtcMs(result));
            Assert.AreEqual(1, fake.LockCalls, "the rejected request must never call Lock");
        }

        // ---------------------------------------------------------------- lease.renew

        [Test]
        public void Renew_ByTheOwner_Succeeds_ReturnsTheExtendedExpiry()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromHours(1));
            gate.Acquire("lease-1", TimeSpan.FromSeconds(10));

            clock = clock.AddSeconds(3);
            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.renew", "lease-1"));

            Assert.IsTrue(ResultSuccess(result));
            Assert.AreEqual("lease-1", ResultLeaseId(result));
            Assert.AreEqual(ToUnixMs(clock.AddSeconds(10)), ResultExpiresAtUtcMs(result));
        }

        [Test]
        public void Renew_WhenNothingIsHeld_FailsWithNullLeaseIdAndExpiry()
        {
            // This exact shape (success=false, leaseId=null, expiresAtUtcMs=null) is what the app's
            // LeaseRegistry.ReconcileAsync reads as "the plugin reports none held" on reconnect.
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.renew", "lease-1"));

            Assert.IsFalse(ResultSuccess(result));
            Assert.IsNull(ResultLeaseId(result));
            Assert.IsNull(ResultExpiresAtUtcMs(result));
        }

        [Test]
        public void Renew_WithTheWrongLeaseId_Fails_ReportsTheActualHolder_DoesNotChangeItsExpiry()
        {
            var acquiredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock = acquiredAt;
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromHours(1));
            gate.Acquire("owner", TimeSpan.FromSeconds(10));

            clock = acquiredAt.AddSeconds(3);
            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.renew", "impostor"));

            Assert.IsFalse(ResultSuccess(result));
            Assert.AreEqual("owner", ResultLeaseId(result));
            Assert.AreEqual(ToUnixMs(acquiredAt.AddSeconds(10)), ResultExpiresAtUtcMs(result),
                "an impostor's renew must not push the real owner's expiry out");
        }

        // ---------------------------------------------------------------- lease.release

        [Test]
        public void Release_ByTheOwner_Succeeds_GateIsNowReleased()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("lease-1");

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.release", "lease-1"));

            Assert.IsTrue(ResultSuccess(result));
            Assert.IsNull(ResultLeaseId(result));
            Assert.IsNull(ResultExpiresAtUtcMs(result));
            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls);
        }

        [Test]
        public void Release_OfAnUnknownOrAlreadyReleasedId_SucceedsIdempotently_NotAnError()
        {
            // The entire point of the release path: retrying a release must always be safe.
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.release", "never-acquired"));

            Assert.IsTrue(ResultSuccess(result), "releasing an unknown id must succeed idempotently, not error");
            Assert.AreEqual(0, fake.UnlockCalls, "must never unlock what was never locked");
        }

        [Test]
        public void Release_WithTheWrongLeaseId_Fails_GateStaysHeldByTheRealOwner()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromHours(1));
            gate.Acquire("owner", TimeSpan.FromSeconds(10));

            var result = HadesBoot.HandleRequest(gate, LeaseRequest("lease.release", "impostor"));

            Assert.IsFalse(ResultSuccess(result));
            Assert.AreEqual("owner", ResultLeaseId(result));
            Assert.IsTrue(gate.IsHeld);
            Assert.AreEqual(0, fake.UnlockCalls);
        }

        // ---------------------------------------------------------------- malformed requests / unknown methods

        [Test]
        public void MissingLeaseIdParam_Throws()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            var request = new JsonRpcRequest { Id = JsonValue.Integer(1), Method = "lease.acquire", Params = JsonValue.NewObject() };

            Assert.Throws<ArgumentException>(() => HadesBoot.HandleRequest(gate, request));
        }

        [Test]
        public void UnknownMethod_StillThrowsNotSupportedException()
        {
            // Regression guard: every method that is not one of the three lease.* commands (or
            // "keepalive", answered elsewhere - see HadesClient) must still fall through to the
            // same "not implemented yet" placeholder HandleRequest always answered with.
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            var request = new JsonRpcRequest { Id = JsonValue.Integer(1), Method = "some.other.method", Params = null };

            Assert.Throws<NotSupportedException>(() => HadesBoot.HandleRequest(gate, request));
        }
    }
}
