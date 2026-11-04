namespace WpfNav.Core.Model;

public enum SymbolKind
{
    Namespace, Class, Interface, Struct, Record, Enum, Delegate,
    Constructor, Destructor, Method, Property, Indexer, Event, Field, Operator, EnumMember,
    TopLevelStatements, XamlResource, XamlElement,
}

public static class SymbolKinds
{
    static readonly string[] Labels =
    [
        "namespace", "class", "interface", "struct", "record", "enum", "delegate",
        "ctor", "dtor", "method", "property", "indexer", "event", "field", "operator", "enum member",
        "top-level", "resource", "element",
    ];

    public static bool IsType(this SymbolKind kind) => kind is >= SymbolKind.Class and <= SymbolKind.Delegate;

    public static bool IsMember(this SymbolKind kind) => kind is >= SymbolKind.Constructor and <= SymbolKind.EnumMember;

    public static string Label(this SymbolKind kind) => Labels[(int)kind];

    /// <summary>Whether the kind belongs to a find_symbol kind filter such as "type", "member" or "method".</summary>
    public static bool MatchesFilter(this SymbolKind kind, string filter) => filter.ToLowerInvariant() switch
    {
        "type" => kind.IsType(),
        "class" => kind == SymbolKind.Class,
        "interface" => kind == SymbolKind.Interface,
        "struct" => kind == SymbolKind.Struct,
        "record" => kind == SymbolKind.Record,
        "enum" => kind == SymbolKind.Enum,
        "delegate" => kind == SymbolKind.Delegate,
        "member" => kind.IsMember(),
        "method" => kind is SymbolKind.Method or SymbolKind.Operator or SymbolKind.Destructor,
        "ctor" => kind == SymbolKind.Constructor,
        "property" => kind is SymbolKind.Property or SymbolKind.Indexer,
        "field" => kind is SymbolKind.Field or SymbolKind.EnumMember,
        "event" => kind == SymbolKind.Event,
        "resource" => kind == SymbolKind.XamlResource,
        "element" => kind == SymbolKind.XamlElement,
        _ => throw new WpfNavException(
            $"Unknown kind '{Output.Echo.Cut(filter)}'. Use one of: type, class, interface, struct, record, enum, delegate, member, method, ctor, property, field, event, resource, element."),
    };
}
