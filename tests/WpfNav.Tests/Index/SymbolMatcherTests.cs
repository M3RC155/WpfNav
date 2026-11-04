using static WpfNav.Tests.TestSupport.TestSymbols;

namespace WpfNav.Tests.Index;

public class SymbolMatcherTests
{
    [Fact]
    public void Closest_suggests_near_misses_and_containing_names()
    {
        var syms = new[] { Sym("OrderService"), Sym("OrderServiceTests"), Sym("Customer") };
        Assert.Equal(new[] { "OrderService" }, SymbolMatcher.Closest(syms, "OrdrService", 5).Select(s => s.Name));
        Assert.Equal(new[] { "OrderService", "OrderServiceTests" }, SymbolMatcher.Closest(syms, "OrderServ", 5).Select(s => s.Name));
        Assert.Equal(new[] { "Orders" }, SymbolMatcher.ClosestNames(new[] { "Gen", "Shell", "Orders", "Core" }, "Ordrs", 5));
    }

    [Theory]
    [InlineData("OrderEditViewModel", "OrderEditViewModel", MatchTier.ExactCase)]
    [InlineData("OrderEditViewModel", "ordereditviewmodel", MatchTier.ExactIgnoreCase)]
    [InlineData("OrderEditViewModel", "*ViewModel", MatchTier.Prefix)]
    [InlineData("OrderEditViewModel", "OrderEd", MatchTier.Prefix)]
    [InlineData("OrderEditViewModel", "OEVM", MatchTier.CamelHumps)]
    [InlineData("OrderEditViewModel", "OrdEdVM", MatchTier.CamelHumps)]
    [InlineData("XmlParser", "XP", MatchTier.CamelHumps)]
    [InlineData("OrderEditViewModel", "rderEdit", MatchTier.Substring)]
    [InlineData("OrderEditViewModel", "Zzz", null)]
    public void Name_tiers(string name, string pattern, MatchTier? expected) =>
        Assert.Equal(expected, SymbolMatcher.MatchName(name, pattern));

    [Fact]
    public void Ranks_by_tier_then_types_then_non_test_then_shorter_names() =>
        Assert.Equal(new[] { "Core.OrderService", "Shell.MainViewModel.OrderService", "Tests.OrderServiceTests", "Core.IOrderService" },
            SymbolMatcher.Match(new[]
            {
                Sym("IOrderService", SymbolKind.Interface, "Core"),
                Sym("OrderServiceTests", container: "Tests", project: "Core.Tests", inTest: true),
                Sym("OrderService", container: "Core"),
                Sym("OrderService", SymbolKind.Property, "Shell.MainViewModel"),
            }, new SymbolQuery("OrderService")).Select(m => m.Symbol.QualifiedName));

    [Fact]
    public void Partial_parts_are_grouped_under_the_first_cs_part()
    {
        var m = Assert.Single(SymbolMatcher.Match(new[]
        {
            Sym("OrderEditView", path: "src/Views/OrderEditView.xaml", partial: true),
            Sym("OrderEditView", path: "src/Views/OrderEditView.xaml.cs", partial: true),
        }, new SymbolQuery("OrderEditView")));
        Assert.Equal("src/Views/OrderEditView.xaml.cs", m.Symbol.Path);
        Assert.Equal(new[] { "src/Views/OrderEditView.xaml" }, m.OtherParts.Select(p => p.Path));
    }

    [Fact]
    public void Same_partial_type_in_two_projects_stays_two_matches() =>
        Assert.Equal(2, SymbolMatcher.Match(new[]
        {
            Sym("MainWindow", path: "a/MainWindow.xaml.cs", project: "A", partial: true),
            Sym("MainWindow", path: "a/MainWindow.xaml", project: "A", partial: true),
            Sym("MainWindow", path: "b/MainWindow.xaml.cs", project: "B", partial: true),
        }, new SymbolQuery("MainWindow")).Count);

    [Fact]
    public void Derives_from_follows_bases_by_name_transitively() =>
        Assert.Equal(new[] { "A", "B" }, SymbolMatcher.Match(new[]
        {
            Sym("A", bases: new[] { "ViewModelBase" }), Sym("B", bases: new[] { "A" }), Sym("C", bases: new[] { "Other" }),
        }, new SymbolQuery("*", DerivesFrom: "ViewModelBase")).Select(m => m.Symbol.Name));

    [Fact]
    public void Kind_project_path_and_dotted_queries_narrow_matches()
    {
        var syms = new[]
        {
            Sym("Save", SymbolKind.Method, "Core.OrderService", "src/Core/OrderService.cs", project: "Core"),
            Sym("Save", SymbolKind.Method, "Shell.Settings", "src/Shell/Settings.cs", project: "Shell"),
            Sym("Save", SymbolKind.Class, "Core", "src/Core/Save.cs", project: "Core"),
        };
        Assert.Equal(2, SymbolMatcher.Match(syms, new SymbolQuery("Save", Kind: "member")).Count);
        Assert.Single(SymbolMatcher.Match(syms, new SymbolQuery("Save", Kind: "method", Project: "shell")));
        Assert.Single(SymbolMatcher.Match(syms, new SymbolQuery("Save", Path: "src/Shell")));
        Assert.Equal("Core.OrderService.Save", Assert.Single(SymbolMatcher.Match(syms, new SymbolQuery("OrderService.Save"))).Symbol.QualifiedName);
        Assert.Single(SymbolMatcher.Match(syms, new SymbolQuery("Core.OrderService.Save")));
        Assert.StartsWith("Unknown kind 'func'. Use one of: type, class,",
            Assert.Throws<WpfNavException>(() => SymbolMatcher.Match(syms, new SymbolQuery("Save", Kind: "func"))).Message);
    }
}
