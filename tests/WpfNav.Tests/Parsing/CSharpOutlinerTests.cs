namespace WpfNav.Tests.Parsing;

public class CSharpOutlinerTests
{
    public const string ViewModel = """
        using CommunityToolkit.Mvvm.ComponentModel;
        namespace Contoso.Orders.ViewModels;

        /// <summary>Edits an order.</summary>
        [System.Diagnostics.DebuggerDisplay("x")]
        public partial class OrderEditViewModel : ViewModelBase, INavigationAware, IRecipient<OrderSaved>
        {
            private readonly IOrderService _orders;
            [ObservableProperty] private Order? _order;
            public static readonly DependencyProperty IsBusyProperty = null!;

            public OrderEditViewModel(IOrderService orders, int retries = 3) => _orders = orders;

            public string Title { get; private set; } = "";

            [RelayCommand]
            private async Task SaveAsync(CancellationToken ct)
            {
                await Task.Delay(1, ct);
            }

            public enum Mode { New, Edit, View }

            public record Line(int Id, string Sku);
        #if DEBUG
            public void DebugOnly() { }
        #endif
        }
        """;

    [Fact]
    public void Outlines_declarations_in_source_order_with_signatures_and_lines()
    {
        var o = CSharpOutliner.Parse(ViewModel, "src/OrderEditViewModel.cs");
        Assert.Equal(28, o.LineCount);
        Assert.Equal(new[]
        {
            (SymbolKind.Namespace, "namespace Contoso.Orders.ViewModels", 2, 28),
            (SymbolKind.Class, "public partial class OrderEditViewModel : ViewModelBase, INavigationAware, IRecipient<OrderSaved>", 6, 28),
            (SymbolKind.Field, "readonly IOrderService _orders", 8, 8),
            (SymbolKind.Field, "Order? _order", 9, 9),
            (SymbolKind.Field, "public static readonly DependencyProperty IsBusyProperty", 10, 10),
            (SymbolKind.Constructor, "public ctor(IOrderService orders, int retries)", 12, 12),
            (SymbolKind.Property, "public string Title { get; private set; }", 14, 14),
            (SymbolKind.Method, "async Task SaveAsync(CancellationToken ct)", 17, 20),
            (SymbolKind.Enum, "public enum Mode", 22, 22),
            (SymbolKind.EnumMember, "New", 22, 22), (SymbolKind.EnumMember, "Edit", 22, 22), (SymbolKind.EnumMember, "View", 22, 22),
            (SymbolKind.Record, "public record Line(int Id, string Sku)", 24, 24),
            (SymbolKind.Method, "public void DebugOnly()", 26, 26),
        }, o.AllSymbols().Select(s => (s.Kind, s.Signature, s.StartLine, s.EndLine)));
    }

    [Fact]
    public void Records_containers_bases_attributes_and_full_start()
    {
        var all = CSharpOutliner.Parse(ViewModel, "src/OrderEditViewModel.cs").AllSymbols().ToList();
        var cls = all.Single(s => s.Kind == SymbolKind.Class);
        Assert.Equal(("OrderEditViewModel", "Contoso.Orders.ViewModels", 4, true), (cls.Name, cls.Container, cls.FullStartLine, cls.IsPartial));
        Assert.Equal(new[] { "ViewModelBase", "INavigationAware", "IRecipient" }, cls.Bases);
        Assert.Equal(new[] { "DebuggerDisplay" }, cls.Attributes);
        var save = all.Single(s => s.Name == "SaveAsync");
        Assert.Equal(("Contoso.Orders.ViewModels.OrderEditViewModel", 16, "private"), (save.Container, save.FullStartLine, save.Accessibility));
        Assert.Equal(new[] { "RelayCommand" }, save.Attributes);
        Assert.Equal("Contoso.Orders.ViewModels.OrderEditViewModel.Line", all.Single(s => s.Name == "Line").QualifiedName);
        Assert.Equal("ctor", all.Single(s => s.Kind == SymbolKind.Constructor).Name);
    }

    [Fact]
    public void Covers_the_remaining_member_shapes()
    {
        var o = CSharpOutliner.Parse("""
            namespace A.B
            {
                public interface IRepo<T> where T : class { T? Get(int id); }
                internal sealed class Repo<T> : IRepo<T> where T : class
                {
                    int _a, _b;
                    public event EventHandler? Changed;
                    public T? this[int i] => default;
                    public static Repo<T> operator +(Repo<T> x, Repo<T> y) => x;
                    public int Count => 0;
                    public T? Get(int id) => default;
                    ~Repo() { }
                }
                public delegate void Done(int code);
            }
            """, "Repo.cs");
        Assert.Equal(new[]
        {
            "namespace A.B", "public interface IRepo<T>", "T? Get(int id)", "internal sealed class Repo<T> : IRepo<T>",
            "int _a", "int _b", "public event EventHandler? Changed", "public T? this[int i] { get; }",
            "public static Repo<T> operator +(Repo<T> x, Repo<T> y)", "public int Count { get; }", "public T? Get(int id)",
            "~Repo()", "public delegate void Done(int code)",
        }, o.AllSymbols().Select(s => s.Signature));
        Assert.Equal("A.B.Repo", o.AllSymbols().Single(s => s.Signature == "public T? Get(int id)").Container);
        Assert.Equal(new[] { "IRepo" }, o.AllSymbols().Single(s => s.Name == "Repo").Bases);
    }

    [Fact]
    public void Top_level_statements_become_one_entry()
    {
        var o = CSharpOutliner.Parse("using System;\nConsole.WriteLine(1);\nConsole.WriteLine(2);\nclass Helper { }\n", "Program.cs");
        var top = o.AllSymbols().Single(s => s.Kind == SymbolKind.TopLevelStatements);
        Assert.Equal((2, 3), (top.StartLine, top.EndLine));
        Assert.Contains(o.AllSymbols(), s => s.Name == "Helper");
    }

    [Fact]
    public void Broken_code_still_outlines_the_parts_that_parse()
    {
        var o = CSharpOutliner.Parse("class A {\n  void Ok() { }\n  void Broken( {\n}\nclass B { }\n", "Broken.cs");
        Assert.Contains(o.AllSymbols(), s => s.Name == "Ok");
        Assert.Contains(o.AllSymbols(), s => s.Name == "B");
    }

    [Fact]
    public void Long_signatures_are_capped_and_lines_are_counted_like_an_editor()
    {
        var p = string.Join(", ", Enumerable.Range(0, 30).Select(i => $"int parameter{i}"));
        var sig = CSharpOutliner.Parse($"class C {{ void M({p}) {{ }} }}", "C.cs").AllSymbols().Single(s => s.Name == "M").Signature;
        Assert.Equal((120, '…'), (sig.Length, sig[^1]));
        Assert.Equal((0, 1, 2, 2), (TextLines.Count(""), TextLines.Count("a"), TextLines.Count("a\nb"), TextLines.Count("a\nb\n")));
    }
}
