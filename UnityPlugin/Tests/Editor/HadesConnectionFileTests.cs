// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using Hades.Contract.Wire;
using Hades.Transport;
using NUnit.Framework;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// <see cref="HadesConnectionFile"/> reads a file written by a different process (the app),
    /// so every test here is really about "what happens when that file is not what we expect
    /// yet" - missing, mid-write, or malformed must all resolve to null, never an exception.
    /// </summary>
    [TestFixture]
    public sealed class HadesConnectionFileTests
    {
        string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hades-connection-file-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }

        [Test]
        public void TryRead_ValidFile_ReturnsPortAndToken()
        {
            var path = Path.Combine(_tempDir, "editor.token");
            File.WriteAllText(path, "{\"port\":54321,\"token\":\"abc123\"}");

            var info = HadesConnectionFile.TryRead(path);

            Assert.IsNotNull(info);
            Assert.AreEqual(54321, info.Port);
            Assert.AreEqual("abc123", info.Token);
        }

        [Test]
        public void TryRead_MissingFile_ReturnsNull()
        {
            var info = HadesConnectionFile.TryRead(Path.Combine(_tempDir, "does-not-exist"));

            Assert.IsNull(info);
        }

        [Test]
        public void TryRead_MalformedJson_ReturnsNullWithoutThrowing()
        {
            var path = Path.Combine(_tempDir, "editor.token");
            File.WriteAllText(path, "{ this is not json");

            EditorConnectionInfo info = null;
            Assert.DoesNotThrow(() => info = HadesConnectionFile.TryRead(path));
            Assert.IsNull(info);
        }

        [Test]
        public void TryRead_ValidJsonWrongShape_ReturnsNull()
        {
            var path = Path.Combine(_tempDir, "editor.token");
            File.WriteAllText(path, "{\"port\":54321}"); // missing "token"

            var info = HadesConnectionFile.TryRead(path);

            Assert.IsNull(info);
        }

        [Test]
        public void TryRead_NullOrEmptyPath_ReturnsNull()
        {
            Assert.IsNull(HadesConnectionFile.TryRead(null));
            Assert.IsNull(HadesConnectionFile.TryRead(string.Empty));
        }

        [Test]
        public void DefaultPath_IsUnderUserProfileLibraryApplicationSupportHades()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var path = HadesConnectionFile.DefaultPath;

            Assert.IsTrue(path.StartsWith(home), "expected a path under the user's home, got: " + path);
            Assert.IsTrue(path.EndsWith(Path.Combine("Library", "Application Support", "Hades", "editor.token")),
                "expected the macOS-native Application Support path, got: " + path);
        }
    }
}
