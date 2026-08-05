using Hades.Core.Scanning;

namespace Hades.Core.Tests.Scanning;

public class RoslynScriptScannerTests
{
    static IReadOnlyList<ScriptType> Scan(string source) =>
        RoslynScriptScanner.ScanText("Assets/Scripts/Test.cs", source);

    [Fact]
    public void FindsAClassWithItsNamespaceAndBaseType()
    {
        var types = Scan("""
            using UnityEngine;

            namespace Game.Player
            {
                public class PlayerController : MonoBehaviour
                {
                }
            }
            """);

        var type = Assert.Single(types);
        Assert.Equal("PlayerController", type.Name);
        Assert.Equal("Game.Player", type.Namespace);
        Assert.Equal("Class", type.Kind);
        Assert.Contains("MonoBehaviour", type.BaseTypes);
        Assert.Equal("Assets/Scripts/Test.cs", type.Path);
    }

    [Fact]
    public void HandlesFileScopedNamespaces()
    {
        var types = Scan("""
            namespace Game.Combat;

            public class Weapon { }
            """);

        Assert.Equal("Game.Combat", Assert.Single(types).Namespace);
    }

    [Fact]
    public void ReportsNullNamespaceForGlobalTypes()
    {
        // The audit records a bug where global-namespace scripts silently lost their
        // instance_of edges. Global namespace must be represented explicitly.
        Assert.Null(Assert.Single(Scan("public class Loose { }")).Namespace);
    }

    [Fact]
    public void FindsNestedTypesWithQualifiedNames()
    {
        var types = Scan("""
            public class Outer
            {
                public class Inner { }
            }
            """);

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "Outer");
        Assert.Contains(types, t => t.Name == "Outer.Inner");
    }

    [Fact]
    public void DistinguishesGenericArityInTheName()
    {
        // Foo<T> and Foo<T1,T2> both used to qualify to the bare name "Foo" — genuinely
        // different types (the Result / Result<T> pattern) must not collide.
        var types = Scan("""
            public class Foo<T> { }
            public class Foo<T1, T2> { }
            """);

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "Foo`1");
        Assert.Contains(types, t => t.Name == "Foo`2");
    }

    [Fact]
    public void NonGenericAndGenericSameNameAreDistinctTypes()
    {
        var types = Scan("""
            public class Foo { }
            public class Foo<T> { }
            """);

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "Foo");
        Assert.Contains(types, t => t.Name == "Foo`1");
    }

    [Fact]
    public void NestedGenericArityAppliesPerSegment()
    {
        // Outer<T>.Inner<U> and Outer<T>.Inner both used to qualify to the same "Outer.Inner".
        var types = Scan("""
            public class Outer<T>
            {
                public class Inner<U> { }
            }
            public class Outer<T>
            {
                public class Inner { }
            }
            """);

        Assert.Contains(types, t => t.Name == "Outer`1.Inner`1");
        Assert.Contains(types, t => t.Name == "Outer`1.Inner");
    }

    [Fact]
    public void EnumsNeverGetAnArityMarker()
    {
        // Enums cannot carry type parameters — they must report plain "E", never "E`0".
        Assert.Equal("E", Assert.Single(Scan("public enum E { A }")).Name);
    }

    [Theory]
    [InlineData("public class C { }", "C", "Class")]
    [InlineData("public struct S { }", "S", "Struct")]
    [InlineData("public interface I { }", "I", "Interface")]
    [InlineData("public enum E { A }", "E", "Enum")]
    [InlineData("public record R(int X);", "R", "Record")]
    public void ClassifiesEachDeclarationKind(string source, string name, string kind)
    {
        var type = Assert.Single(Scan(source));

        Assert.Equal(name, type.Name);
        Assert.Equal(kind, type.Kind);
    }

    [Fact]
    public void RecordsOneBasedLineNumbers()
    {
        var types = Scan("""
            using UnityEngine;

            public class Thing { }
            """);

        Assert.Equal(3, Assert.Single(types).Line);
    }

    [Fact]
    public void ReturnsEmptyForFileWithNoTypes()
    {
        Assert.Empty(Scan("// just a comment"));
    }

    [Fact]
    public void DoesNotThrowOnSyntaxErrors()
    {
        // A half-typed file must degrade, never crash the indexer.
        var types = Scan("public class Broken { void M( { }");

        Assert.Contains(types, t => t.Name == "Broken");
    }
}
