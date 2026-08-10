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
    public static IReadOnlyList<ScriptType> ScanFile(string projectRelativePath, string absolutePath,
        IEnumerable<string>? defines = null) =>
        ScanText(projectRelativePath, File.ReadAllText(absolutePath), defines);

    /// <summary>
    /// <paramref name="defines"/> is every preprocessor symbol <c>#if</c> should treat as true —
    /// with no options at all, <see cref="CSharpSyntaxTree.ParseText(string, CSharpParseOptions?,
    /// string, System.Text.Encoding?, System.Threading.CancellationToken)"/> evaluates every
    /// <c>#if</c> as false and silently drops the guarded declarations from the tree, which is
    /// the Plan 15 Task 3 defect this parameter fixes. This method is deliberately just the
    /// mechanism ("parse with exactly these symbols"); which symbols a project actually compiles
    /// with is policy decided by <see cref="Hades.Core.Projects.ProjectDefines"/>, not here — a
    /// caller that passes nothing (every existing call site before this fix, and any test that
    /// does not care about conditional compilation) gets the same "nothing defined" behaviour the
    /// scanner always had.
    /// </summary>
    public static IReadOnlyList<ScriptType> ScanText(string projectRelativePath, string source,
        IEnumerable<string>? defines = null)
    {
        var options = new CSharpParseOptions(preprocessorSymbols: defines ?? []);
        var tree = CSharpSyntaxTree.ParseText(source, options);
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
