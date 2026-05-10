using System;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class PathSandboxTests
    {
        [Test]
        public void Resolve_ValidRelativePath_ReturnsAbsolutePath()
        {
            var result = PathSandbox.Resolve("Assets/Scripts/Test.cs");
            StringAssert.EndsWith("Assets/Scripts/Test.cs", result.Replace('\\', '/'));
        }

        [Test]
        public void Resolve_NullPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve(null));
        }

        [Test]
        public void Resolve_EmptyPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve(""));
        }

        [Test]
        public void Resolve_AbsolutePath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve("/etc/passwd"));
        }

        [Test]
        public void Resolve_TraversalAttack_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("../../etc/passwd"));
        }

        [Test]
        public void Resolve_BackslashTraversal_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("..\\..\\etc\\passwd"));
        }

        [Test]
        public void ResolveWritable_GitFolder_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git/config"));
        }

        [Test]
        public void ResolveWritable_GitRoot_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git"));
        }

        [Test]
        public void ResolveWritable_NormalPath_Succeeds()
        {
            var result = PathSandbox.ResolveWritable("Assets/NewScript.cs");
            StringAssert.EndsWith("Assets/NewScript.cs", result.Replace('\\', '/'));
        }

        [Test]
        public void ResolveWritable_ArcforgeDirectory_Succeeds()
        {
            var result = PathSandbox.ResolveWritable(".arcforge/server.json");
            StringAssert.EndsWith(".arcforge/server.json", result.Replace('\\', '/'));
        }

        [Test]
        public void MakeRelative_PathInsideProject_ReturnsRelative()
        {
            var abs = System.IO.Path.Combine(PathSandbox.ProjectRoot, "Assets", "Test.cs");
            var result = PathSandbox.MakeRelative(abs);
            Assert.AreEqual("Assets" + System.IO.Path.DirectorySeparatorChar + "Test.cs", result);
        }

        [Test]
        public void MakeRelative_PathOutsideProject_ReturnsUnchanged()
        {
            var result = PathSandbox.MakeRelative("/tmp/outside.txt");
            Assert.AreEqual("/tmp/outside.txt", result);
        }

        [Test]
        public void ProjectRoot_IsNotNull()
        {
            Assert.IsNotNull(PathSandbox.ProjectRoot);
            Assert.IsNotEmpty(PathSandbox.ProjectRoot);
        }
    }
}
