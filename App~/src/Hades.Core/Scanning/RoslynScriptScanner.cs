using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hades.Core.Scanning;

/// <summary>
/// Parses C# with Roslyn — the compiler frontend maintained by the people who define the
/// language, so it always understands the newest C# version Unity adopts. Syntax-only:
/// no compilation or assembly references are needed, which keeps scanning fast and
/// independent of whether the project currently compiles.
/// </summary>
public static class RoslynScriptScanner
{
    public static IReadOnlyList<ScriptType> ScanFile(string projectRelativePath, string absolutePath) =>
        ScanText(projectRelativePath, File.ReadAllText(absolutePath));

    public static IReadOnlyList<ScriptType> ScanText(string projectRelativePath, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var results = new List<ScriptType>();

        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            results.Add(new ScriptType
            {
                Name = QualifiedName(declaration),
                Kind = KindOf(declaration),
                Path = projectRelativePath,
                Namespace = NamespaceOf(declaration),
                Line = declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                BaseTypes = BaseTypesOf(declaration),
            });
        }

        return results;
    }

    /// <summary>
    /// Nested types are reported as "Outer.Inner" so they are unambiguous. Each segment
    /// carries its generic arity as a backtick suffix — .NET's own metadata-name convention,
    /// e.g. "Foo`1" — so "Foo" and "Foo&lt;T&gt;", or "Outer&lt;T&gt;.Inner&lt;U&gt;" and
    /// "Outer&lt;T&gt;.Inner", are distinct identities instead of colliding on the same name.
    /// </summary>
    static string QualifiedName(BaseTypeDeclarationSyntax declaration)
    {
        var parts = new List<string> { Segment(declaration) };

        for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
            if (parent is BaseTypeDeclarationSyntax outer)
                parts.Insert(0, Segment(outer));

        return string.Join('.', parts);
    }

    /// <summary>Only class/struct/interface/record declarations can carry type parameters —
    /// enums cannot, so they always report arity 0 and never get a suffix.</summary>
    static string Segment(BaseTypeDeclarationSyntax declaration)
    {
        var arity = declaration is TypeDeclarationSyntax generic ? generic.Arity : 0;
        return arity > 0 ? $"{declaration.Identifier.ValueText}`{arity}" : declaration.Identifier.ValueText;
    }

    static string? NamespaceOf(SyntaxNode declaration)
    {
        for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
            if (parent is BaseNamespaceDeclarationSyntax ns)
                return ns.Name.ToString();

        // Global namespace — represented explicitly as null, never conflated with "unknown".
        return null;
    }

    static string KindOf(BaseTypeDeclarationSyntax declaration) => declaration switch
    {
        RecordDeclarationSyntax => "Record",
        ClassDeclarationSyntax => "Class",
        StructDeclarationSyntax => "Struct",
        InterfaceDeclarationSyntax => "Interface",
        EnumDeclarationSyntax => "Enum",
        _ => "Type",
    };

    static IReadOnlyList<string> BaseTypesOf(BaseTypeDeclarationSyntax declaration) =>
        declaration.BaseList is null
            ? []
            : declaration.BaseList.Types.Select(t => t.Type.ToString()).ToList();
}
