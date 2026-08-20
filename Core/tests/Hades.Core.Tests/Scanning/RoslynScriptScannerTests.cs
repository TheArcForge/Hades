using Hades.Core.Scanning;

namespace Hades.Core.Tests.Scanning;

public class RoslynScriptScannerTests
{
    static IReadOnlyList<ScriptType> Scan(string source, IEnumerable<string>? defines = null) =>
        RoslynScriptScanner.ScanText("Assets/Scripts/Test.cs", source, defines);

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

    // --- Plan 15 Task 3: conditional compilation ---------------------------------------------
    // Defect: ParseText was called with no CSharpParseOptions at all, so Roslyn evaluated every
    // #if as false and silently dropped the guarded declarations from the syntax tree — not
    // "missed", never indexed at all. Repro: project_aurora's MathematicsDrawers.cs, 64 type
    // declarations inside one #if UNITY_EDITOR / #endif pair, invisible to search_by_name.

    [Fact]
    public void IndexesCodeInsideAnActiveIfDirective()
    {
        var types = Scan("""
            #if UNITY_EDITOR
            public class EditorOnlyDrawer { }
            #endif
            """, defines: ["UNITY_EDITOR"]);

        Assert.Contains(types, t => t.Name == "EditorOnlyDrawer");
    }

    [Fact]
    public void ExcludesCodeInsideAnInactiveIfDirective()
    {
        // Proves this is real preprocessing, not "make everything visible": a symbol genuinely
        // absent from the define set must still evaluate false.
        var types = Scan("""
            #if SOME_UNDEFINED_SYMBOL
            public class NeverCompiled { }
            #endif
            """, defines: ["UNITY_EDITOR"]);

        Assert.Empty(types);
    }

    [Fact]
    public void ExcludesTheElseBranchOfATrueCondition()
    {
        // The shortcut Plan 15 Task 3 explicitly rejects: stripping #if/#else/#endif so BOTH
        // branches land in the graph, producing a declaration that exists in no real compile.
        var types = Scan("""
            #if UNITY_EDITOR
            public class EditorBranch { }
            #else
            public class RuntimeBranch { }
            #endif
            """, defines: ["UNITY_EDITOR"]);

        Assert.Contains(types, t => t.Name == "EditorBranch");
        Assert.DoesNotContain(types, t => t.Name == "RuntimeBranch");
    }

    [Fact]
    public void ExcludesTheIfBranchOfAFalseCondition()
    {
        // Mirror of the test above with a different define set — proves the #else branch is
        // the one selected, not both, and not neither.
        var types = Scan("""
            #if UNITY_EDITOR
            public class EditorBranch { }
            #else
            public class RuntimeBranch { }
            #endif
            """, defines: ["SOME_OTHER_SYMBOL"]);

        Assert.Contains(types, t => t.Name == "RuntimeBranch");
        Assert.DoesNotContain(types, t => t.Name == "EditorBranch");
    }

    [Fact]
    public void DefaultsToNoDefinesWhenNoneGiven()
    {
        // The scanner is a mechanism, not a policy: which symbols apply is
        // Hades.Core.Projects.ProjectDefines' job. A caller that passes nothing gets the same
        // "nothing defined" behaviour the scanner always had, rather than a silently-injected
        // guess.
        var types = Scan("""
            #if UNITY_EDITOR
            public class EditorOnlyDrawer { }
            #endif
            """);

        Assert.Empty(types);
    }

    [Fact]
    public void SkipsATypeDeclarationWithNoName()
    {
        // I11: a syntactically broken declaration (mid-edit, or a genuine syntax error) still
        // produces a BaseTypeDeclarationSyntax node in Roslyn's error-tolerant tree, but with a
        // MISSING identifier token - empty text. Previously that became a real Class node named
        // "", polluting search results with an unnamed, unusable entry instead of just not being
        // indexed at all.
        var types = Scan("public class { }\npublic class Real { }");

        var type = Assert.Single(types);
        Assert.Equal("Real", type.Name);
    }
}
