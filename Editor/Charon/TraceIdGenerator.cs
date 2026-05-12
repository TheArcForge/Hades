// Editor/Charon/TraceIdGenerator.cs
using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ArcForge.Hades.Editor.Charon
{
    public static class TraceIdGenerator
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
        static readonly Regex TraceIdPattern = new Regex("^[0-9a-f]{32}$", RegexOptions.Compiled);

        public static string NewTraceId()
        {
            var bytes = new byte[16];
            Rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static string NewSpanId()
        {
            var bytes = new byte[8];
            Rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static bool IsValidTraceId(string id)
        {
            return !string.IsNullOrEmpty(id) && TraceIdPattern.IsMatch(id);
        }
    }
}
