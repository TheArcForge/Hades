// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;

namespace Hades.Runtime
{
    /// <summary>
    /// One lease held against <see cref="ReloadGate"/>: an id, a TTL, and the UTC timestamp of the
    /// last activity that justified keeping it alive. There is no parameterless constructor - a
    /// lease without an id or a TTL is not representable, which is what makes a parameterless
    /// <c>ReloadGate.Acquire()</c> impossible too (it would have nothing to construct).
    ///
    /// Activity renews; intent does not (spec rule 4). This type has no "I promise to come back"
    /// method - the only way to push <see cref="ExpiresAtUtc"/> out is <see cref="Renew"/>, called
    /// only when real work actually happens. Silence past <see cref="ExpiresAtUtc"/> is expiry.
    /// </summary>
    public sealed class ReloadLease
    {
        public string Id { get; }
        public TimeSpan Ttl { get; }
        public DateTime LastActivityUtc { get; private set; }

        /// <summary>The moment this lease expires if nothing renews it - recomputed from
        /// <see cref="LastActivityUtc"/> and <see cref="Ttl"/>, not stored independently, so the
        /// two can never drift apart.</summary>
        public DateTime ExpiresAtUtc => LastActivityUtc + Ttl;

        public ReloadLease(string id, TimeSpan ttl, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Lease id must not be null or empty.", nameof(id));
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");

            Id = id;
            Ttl = ttl;
            LastActivityUtc = nowUtc;
        }

        /// <summary>Records real activity now, pushing <see cref="ExpiresAtUtc"/> out by
        /// <see cref="Ttl"/> from <paramref name="nowUtc"/>.</summary>
        public void Renew(DateTime nowUtc) => LastActivityUtc = nowUtc;

        public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
    }
}
