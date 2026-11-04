using System.Text.RegularExpressions;
using System.Xml;
using WpfNav.Core.Model;

namespace WpfNav.Core.Parsing;

/// <summary>Outlines a XAML file: header facts, resources, symbols and a tree reduced to the elements worth reading.</summary>
public static partial class XamlOutliner
{
    const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    const string DesignNamespace = "http://schemas.microsoft.com/expression/blend/2008";
    const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
    const int MaxUiText = 40, MaxValue = 60, MaxChildren = 20;

    static readonly string[] UiTextAttributes = ["Content", "Header", "Text", "Title", "ToolTip"];
    static readonly string[] RootAttributeNames = ["StartupUri", "Startup", "Exit"];
    static readonly HashSet<string> KeptTypes = ["Style", "DataTemplate", "ControlTemplate", "ItemsPanelTemplate"];
    static readonly IReadOnlyList<string> None = [];

    public static IReadOnlySet<string> KnownEvents { get; } = new HashSet<string>(
    [
        "Click", "Loaded", "Unloaded", "Initialized", "SelectionChanged", "TextChanged", "Checked", "Unchecked", "ValueChanged",
        "MouseDown", "MouseUp", "MouseMove", "MouseEnter", "MouseLeave", "MouseDoubleClick", "MouseLeftButtonDown",
        "MouseLeftButtonUp", "MouseRightButtonDown", "MouseRightButtonUp", "MouseWheel", "PreviewMouseDown", "PreviewMouseUp",
        "PreviewMouseLeftButtonDown", "PreviewMouseMove", "PreviewMouseWheel", "KeyDown", "KeyUp", "PreviewKeyDown",
        "PreviewKeyUp", "PreviewTextInput", "GotFocus", "LostFocus", "DragEnter", "DragOver", "DragLeave", "Drop",
        "SizeChanged", "Closing", "Closed", "Activated", "Deactivated", "ContentRendered", "StateChanged", "Executed",
        "CanExecute",
    ]);

    /// <param name="uncappedTree">Keep every kept child instead of the first 20 per element (outline pages show them all).</param>
    public static XamlOutline Parse(string text, string path, XamlContext? context = null, bool uncappedTree = false)
    {
        context ??= XamlContext.Empty;
        var lineCount = TextLines.Count(text);
        var (root, error) = Read(text);
        if (root is null)
            return new XamlOutline(path, lineCount, "", null, context.CodeBehindPath, null, new Dictionary<string, string>(),
                [], [], [], 0, [], error);

        var xClass = root.Value(XamlNamespace, "Class");
        var all = root.Descendants().ToList();
        var cap = uncappedTree ? int.MaxValue : MaxChildren;
        var top = Children(root, context, cap);
        var symbols = new List<SymbolRecord>();
        if (xClass is not null)
        {
            var dot = xClass.LastIndexOf('.');
            var (container, name) = dot < 0 ? ("", xClass) : (xClass[..dot], xClass[(dot + 1)..]);
            symbols.Add(Symbol(name, SymbolKind.Class, container, $"partial class {name} (XAML {root.Name})", path, 1, lineCount, isPartial: true));
        }
        foreach (var e in all)
        {
            var key = e.Value(XamlNamespace, "Key");
            if (key is not null && IsResource(e))
                symbols.Add(Symbol(key, SymbolKind.XamlResource, xClass ?? "", $"{e.Name} x:Key={key}", path, e.StartLine, e.EndLine));
            if (e.ElementName() is { } elementName)
                symbols.Add(Symbol(elementName, SymbolKind.XamlElement, xClass ?? "", $"{e.Name} x:Name={elementName}", path, e.StartLine, e.EndLine));
        }

        return new XamlOutline(
            path, lineCount, root.Name, xClass, context.CodeBehindPath, DataContextOf(root),
            RootAttributeNames.Where(n => root.Value("", n) is not null).ToDictionary(n => n, n => root.Value("", n)!),
            all.Where(IsResource).Select(e => new XamlResource(e.Value(XamlNamespace, "Key"), e.Name, ResourceDetail(e), e.StartLine, e.EndLine)).ToList(),
            all.Where(e => e.Parent?.LocalName == "ResourceDictionary.MergedDictionaries" && e.Value("", "Source") is not null)
                .Select(e => e.Value("", "Source")!).ToList(),
            top.Take(cap).ToList(), Math.Max(0, top.Count - cap), symbols, error);
    }

    static (Element? Root, string? Error) Read(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };
        Element? root = null;
        var open = new Stack<Element>();
        var lastLine = 1;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var info = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                lastLine = info.LineNumber;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var element = new Element(reader.Name, reader.LocalName, info.LineNumber, open.TryPeek(out var parent) ? parent : null);
                    var end = element.StartLine;
                    for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                    {
                        if (reader.NamespaceURI != XmlnsNamespace)
                            element.Attributes.Add(new Attr(reader.Name, reader.LocalName, reader.NamespaceURI, reader.Value));
                        end = Math.Max(end, info.LineNumber);
                    }
                    reader.MoveToElement();
                    if (parent is null) root ??= element;
                    else parent.Children.Add(element);
                    if (reader.IsEmptyElement) element.EndLine = end;
                    else open.Push(element);
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    open.Pop().EndLine = info.LineNumber;
                }
            }
            return (root, null);
        }
        catch (XmlException ex)
        {
            foreach (var unclosed in open) unclosed.EndLine = Math.Max(unclosed.StartLine, lastLine);
            return (root, $"XML error at line {ex.LineNumber}: {ex.Message}");
        }
    }

    static bool IsResource(Element e) =>
        e.Parent is { } p
        && (p.IsPropertyElement ? p.LocalName.EndsWith(".Resources", StringComparison.Ordinal) : p.LocalName == "ResourceDictionary")
        && !e.IsPropertyElement
        && !(e.LocalName == "ResourceDictionary" && e.Value(XamlNamespace, "Key") is null);

    static string? ResourceDetail(Element e)
    {
        foreach (var name in new[] { "DataType", "TargetType" })
            if (e.Value("", name) is { } value)
                return $"{name}={XTypeArgument().Replace(value, "$1")}";
        return null;
    }

    static string? DataContextOf(Element root)
    {
        var design = root.Attributes.FirstOrDefault(a => a.LocalName == "DataContext" && (a.Namespace == DesignNamespace || a.Name == "d:DataContext"));
        if (design is not null && DesignInstance().Match(design.Value) is { Success: true } m)
            return $"{m.Groups[1].Value.Split(':')[^1]} (design)";
        var property = root.Children.FirstOrDefault(c => c.IsPropertyElement && c.LocalName.EndsWith(".DataContext", StringComparison.Ordinal));
        return property?.Children.FirstOrDefault()?.LocalName;
    }

    /// <summary>The reduced-tree nodes for an element's logical children.</summary>
    static List<XamlNode> Children(Element e, XamlContext context, int cap) =>
        e.LocalName == "ResourceDictionary"
            ? []
            : LogicalChildren(e).Select(c => Build(c, context, cap)).OfType<XamlNode>().ToList();

    static IEnumerable<Element> LogicalChildren(Element e)
    {
        foreach (var child in e.Children)
        {
            if (!child.IsPropertyElement) yield return child;
            else if (!child.LocalName.EndsWith(".Resources", StringComparison.Ordinal) && !child.LocalName.EndsWith(".DataContext", StringComparison.Ordinal))
                foreach (var grandchild in LogicalChildren(child)) yield return grandchild;
        }
    }

    static XamlNode? Build(Element e, XamlContext context, int cap)
    {
        var children = Children(e, context, cap);
        var (label, kept) = Label(e, context);
        if (!kept && children.Count == 0) return null;
        if (!kept && children.Count == 1)
        {
            var only = children[0];
            return only with { Label = $"{e.Name}/{only.Label}" };
        }
        return new XamlNode(label, e.StartLine, e.EndLine, children.Take(cap).ToList(), Math.Max(0, children.Count - cap));
    }

    static (string Label, bool Kept) Label(Element e, XamlContext context)
    {
        var parts = new List<string> { e.Name };
        var name = e.ElementName();
        if (name is not null) parts.Add($"x:Name={name}");
        var uiText = e.Attributes.FirstOrDefault(a => a.Namespace.Length == 0 && UiTextAttributes.Contains(a.LocalName) && !IsMarkup(a.Value));
        if (uiText is not null) parts.Add($"\"{Cut(uiText.Value, MaxUiText)}\"");
        foreach (var a in e.Attributes)
        {
            if (a == uiText || IsName(a)) continue;
            if (a.Namespace == XamlNamespace && a.LocalName == "Key") parts.Add($"x:Key={Cut(a.Value, MaxValue)}");
            else if (IsMarkup(a.Value) || a.LocalName is "Command" or "DataType" or "TargetType") parts.Add($"{a.Name}={Cut(a.Value, MaxValue)}");
            else if (Identifier().IsMatch(a.Value) && (KnownEvents.Contains(a.LocalName) || context.CodeBehindMethods.Contains(a.Value)))
                parts.Add($"{a.Name}={a.Value}");
        }
        return (string.Join(" ", parts), parts.Count > 1 || KeptTypes.Contains(e.LocalName));
    }

    static bool IsName(Attr a) => a.LocalName == "Name" && (a.Namespace == XamlNamespace || a.Namespace.Length == 0);

    static bool IsMarkup(string value) => value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal);

    static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    static SymbolRecord Symbol(string name, SymbolKind kind, string container, string signature, string path, int start, int end, bool isPartial = false) =>
        new(name, kind, container, signature, path, start, end, start, "", None, None, isPartial);

    [GeneratedRegex(@"^\{x:Type\s+([^}]+)\}$")]
    private static partial Regex XTypeArgument();

    [GeneratedRegex(@"DesignInstance\s+(?:Type\s*=\s*)?([\w:.]+)")]
    private static partial Regex DesignInstance();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    sealed record Attr(string Name, string LocalName, string Namespace, string Value);

    sealed class Element(string name, string localName, int startLine, Element? parent)
    {
        public string Name { get; } = name;
        public string LocalName { get; } = localName;
        public int StartLine { get; } = startLine;
        public int EndLine { get; set; } = startLine;
        public Element? Parent { get; } = parent;
        public List<Attr> Attributes { get; } = [];
        public List<Element> Children { get; } = [];
        public bool IsPropertyElement => LocalName.Contains('.');

        public string? Value(string ns, string localName) =>
            Attributes.FirstOrDefault(a => a.Namespace == ns && a.LocalName == localName)?.Value;

        public string? ElementName() => Value(XamlNamespace, "Name") ?? Value("", "Name");

        public IEnumerable<Element> Descendants()
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var d in child.Descendants()) yield return d;
            }
        }
    }
}
