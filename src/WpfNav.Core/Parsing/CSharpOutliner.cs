using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WpfNav.Core.Model;
using SymbolKind = WpfNav.Core.Model.SymbolKind;

namespace WpfNav.Core.Parsing;

/// <summary>Outlines a C# file from Roslyn's syntax tree alone: declarations, one-line signatures and line ranges.</summary>
public static partial class CSharpOutliner
{
    const int MaxSignature = 120;

    static readonly CSharpParseOptions Options =
        new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular, ["DEBUG", "TRACE"]);

    public static FileOutline Parse(string text, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Options, path);
        var roots = new Walker(tree, path).Members(tree.GetCompilationUnitRoot().Members, "", parent: null);
        return new FileOutline(path, TextLines.Count(text), roots);
    }

    /// <summary>Collapses whitespace runs and caps the length at 120 characters.</summary>
    internal static string Cap(string text)
    {
        var flat = Whitespace().Replace(text, " ").Trim();
        return flat.Length <= MaxSignature ? flat : flat[..(MaxSignature - 1)] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    sealed class Walker(SyntaxTree tree, string path)
    {
        static readonly IReadOnlyList<string> None = [];

        public List<OutlineNode> Members(SyntaxList<MemberDeclarationSyntax> members, string container, SymbolKind? parent)
        {
            var nodes = new List<OutlineNode>();
            var globals = members.OfType<GlobalStatementSyntax>().ToList();
            if (globals.Count > 0) nodes.Add(TopLevel(globals));
            foreach (var member in members)
                if (member is not GlobalStatementSyntax) nodes.AddRange(Declare(member, container, parent));
            return nodes;
        }

        IEnumerable<OutlineNode> Declare(MemberDeclarationSyntax member, string container, SymbolKind? parent)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                {
                    var name = container.Length == 0 ? ns.Name.ToString() : $"{container}.{ns.Name}";
                    var symbol = Record(ns, name, SymbolKind.Namespace, "", $"namespace {name}", "");
                    yield return new OutlineNode(symbol, Members(ns.Members, name, null));
                    break;
                }
                case BaseTypeDeclarationSyntax type:
                    yield return Type(type, container, parent);
                    break;
                case DelegateDeclarationSyntax d:
                    yield return Leaf(Record(d, d.Identifier.Text, SymbolKind.Delegate, container,
                        Join(Mods(d.Modifiers), "delegate", Text(d.ReturnType), d.Identifier.Text + Text(d.TypeParameterList) + Params(d.ParameterList)),
                        TypeAccess(d.Modifiers, parent)));
                    break;
                default:
                    foreach (var node in Member(member, container, parent)) yield return node;
                    break;
            }
        }

        OutlineNode Type(BaseTypeDeclarationSyntax type, string container, SymbolKind? parent)
        {
            var (kind, keyword) = type switch
            {
                RecordDeclarationSyntax r => (SymbolKind.Record,
                    r.ClassOrStructKeyword.IsKind(SyntaxKind.None) ? "record" : $"record {r.ClassOrStructKeyword.Text}"),
                ClassDeclarationSyntax => (SymbolKind.Class, "class"),
                StructDeclarationSyntax => (SymbolKind.Struct, "struct"),
                InterfaceDeclarationSyntax => (SymbolKind.Interface, "interface"),
                _ => (SymbolKind.Enum, "enum"),
            };
            var head = type.Identifier.Text;
            if (type is TypeDeclarationSyntax t) head += Text(t.TypeParameterList) + (t.ParameterList is null ? "" : Params(t.ParameterList));
            var baseTypes = type.BaseList?.Types.Select(b => b.Type).ToList() ?? [];
            var signature = Join(Mods(type.Modifiers), keyword, head)
                + (baseTypes.Count == 0 ? "" : " : " + string.Join(", ", baseTypes.Select(Text)));
            var symbol = Record(type, type.Identifier.Text, kind, container, signature, TypeAccess(type.Modifiers, parent))
                with { Bases = baseTypes.Select(SimpleName).ToList() };
            var qualified = symbol.QualifiedName;
            var children = type switch
            {
                TypeDeclarationSyntax td => Members(td.Members, qualified, kind),
                EnumDeclarationSyntax e => e.Members
                    .Select(m => Leaf(Record(m, m.Identifier.Text, SymbolKind.EnumMember, qualified, m.Identifier.Text, "public")))
                    .ToList(),
                _ => [],
            };
            return new OutlineNode(symbol, children);
        }

        IEnumerable<OutlineNode> Member(MemberDeclarationSyntax member, string container, SymbolKind? parent)
        {
            var access = MemberAccess(member.Modifiers, parent);
            var mods = Mods(member.Modifiers);
            switch (member)
            {
                case ConstructorDeclarationSyntax c:
                    yield return Leaf(Record(c, "ctor", SymbolKind.Constructor, container, Join(mods, "ctor" + Params(c.ParameterList)), access));
                    break;
                case DestructorDeclarationSyntax d:
                    yield return Leaf(Record(d, "~" + d.Identifier.Text, SymbolKind.Destructor, container, $"~{d.Identifier.Text}()", access));
                    break;
                case MethodDeclarationSyntax m:
                    yield return Leaf(Record(m, m.Identifier.Text, SymbolKind.Method, container,
                        Join(mods, Text(m.ReturnType), m.Identifier.Text + Text(m.TypeParameterList) + Params(m.ParameterList)), access));
                    break;
                case PropertyDeclarationSyntax p:
                    yield return Leaf(Record(p, p.Identifier.Text, SymbolKind.Property, container,
                        Join(mods, Text(p.Type), p.Identifier.Text, Accessors(p.AccessorList, p.ExpressionBody)), access));
                    break;
                case IndexerDeclarationSyntax i:
                    yield return Leaf(Record(i, "this", SymbolKind.Indexer, container,
                        Join(mods, Text(i.Type), "this" + BracketParams(i.ParameterList), Accessors(i.AccessorList, i.ExpressionBody)), access));
                    break;
                case EventDeclarationSyntax e:
                    yield return Leaf(Record(e, e.Identifier.Text, SymbolKind.Event, container, Join(mods, "event", Text(e.Type), e.Identifier.Text), access));
                    break;
                case EventFieldDeclarationSyntax ef:
                    foreach (var v in ef.Declaration.Variables)
                        yield return Leaf(Record(ef, v.Identifier.Text, SymbolKind.Event, container,
                            Join(mods, "event", Text(ef.Declaration.Type), v.Identifier.Text), access));
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                        yield return Leaf(Record(f, v.Identifier.Text, SymbolKind.Field, container,
                            Join(mods, Text(f.Declaration.Type), v.Identifier.Text), access));
                    break;
                case OperatorDeclarationSyntax o:
                    yield return Leaf(Record(o, $"operator {o.OperatorToken.Text}", SymbolKind.Operator, container,
                        Join(mods, Text(o.ReturnType), $"operator {o.OperatorToken.Text}" + Params(o.ParameterList)), access));
                    break;
                case ConversionOperatorDeclarationSyntax co:
                    var name = $"{co.ImplicitOrExplicitKeyword.Text} operator {Text(co.Type)}";
                    yield return Leaf(Record(co, name, SymbolKind.Operator, container, Join(mods, name + Params(co.ParameterList)), access));
                    break;
            }
        }

        OutlineNode TopLevel(List<GlobalStatementSyntax> statements)
        {
            const string name = "<top-level statements>";
            var start = Line(statements[0].GetFirstToken());
            var end = Line(statements[^1].GetLastToken());
            return Leaf(new SymbolRecord(name, SymbolKind.TopLevelStatements, "", name, path, start, end, start, "", None, None, false));
        }

        SymbolRecord Record(MemberDeclarationSyntax node, string name, SymbolKind kind, string container, string signature, string access)
        {
            var attributes = node.AttributeLists;
            var startToken = attributes.Count > 0 ? attributes[^1].GetLastToken().GetNextToken() : node.GetFirstToken();
            var start = Line(startToken);
            var fullStart = attributes.Count > 0 ? Line(attributes[0].GetFirstToken()) : start;
            foreach (var trivia in node.GetFirstToken().LeadingTrivia)
            {
                var text = trivia.ToString();
                if (text.StartsWith("///") || text.StartsWith("/**"))
                {
                    fullStart = Math.Min(fullStart, tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1);
                    break;
                }
            }
            var attributeNames = attributes.SelectMany(a => a.Attributes).Select(a => AttributeName(a.Name)).ToList();
            var isPartial = node.Modifiers.Any(SyntaxKind.PartialKeyword);
            return new SymbolRecord(name, kind, container, Cap(signature), path, start, Line(node.GetLastToken()), fullStart,
                access, None, attributeNames, isPartial);
        }

        int Line(SyntaxToken token) => tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;

        static OutlineNode Leaf(SymbolRecord symbol) => new(symbol, []);

        static string Mods(SyntaxTokenList modifiers) =>
            string.Join(" ", modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword)).Select(m => m.Text));

        static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

        static string Text(SyntaxNode? node) => node is null ? "" : Whitespace().Replace(node.ToString(), " ");

        static string Params(BaseParameterListSyntax list) => "(" + ParamList(list) + ")";

        static string BracketParams(BaseParameterListSyntax list) => "[" + ParamList(list) + "]";

        static string ParamList(BaseParameterListSyntax list) =>
            string.Join(", ", list.Parameters.Select(p => Join(Mods(p.Modifiers), Text(p.Type), p is ParameterSyntax ps ? ps.Identifier.Text : "")));

        static string Accessors(AccessorListSyntax? accessors, ArrowExpressionClauseSyntax? expressionBody)
        {
            if (accessors is null) return expressionBody is null ? "" : "{ get; }";
            // Accessor modifiers keep "private": `{ get; private set; }` differs from `{ get; set; }`.
            return "{ " + string.Concat(accessors.Accessors.Select(a =>
                Join(string.Join(" ", a.Modifiers.Select(m => m.Text)), a.Keyword.Text) + "; ")) + "}";
        }

        static string AccessOf(SyntaxTokenList modifiers) => string.Join(" ", modifiers
            .Where(m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword
                or SyntaxKind.InternalKeyword or SyntaxKind.FileKeyword)
            .Select(m => m.Text));

        static string TypeAccess(SyntaxTokenList modifiers, SymbolKind? parent) =>
            AccessOf(modifiers) is { Length: > 0 } declared ? declared : parent is null ? "internal" : "private";

        static string MemberAccess(SyntaxTokenList modifiers, SymbolKind? parent) =>
            AccessOf(modifiers) is { Length: > 0 } declared ? declared : parent == SymbolKind.Interface ? "public" : "private";

        static string SimpleName(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax q => SimpleName(q.Right),
            AliasQualifiedNameSyntax a => SimpleName(a.Name),
            GenericNameSyntax g => g.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            _ => Text(type),
        };

        static string AttributeName(NameSyntax name)
        {
            var simple = SimpleName(name);
            return simple.Length > "Attribute".Length && simple.EndsWith("Attribute", StringComparison.Ordinal)
                ? simple[..^"Attribute".Length]
                : simple;
        }
    }
}
