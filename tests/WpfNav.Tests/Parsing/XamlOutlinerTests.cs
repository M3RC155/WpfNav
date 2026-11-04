namespace WpfNav.Tests.Parsing;

public class XamlOutlinerTests
{
    public const string View = """
        <UserControl x:Class="Contoso.Orders.Views.OrderEditView"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                     xmlns:vm="clr-namespace:Contoso.Orders.ViewModels"
                     d:DataContext="{d:DesignInstance Type=vm:OrderEditViewModel}">
            <UserControl.Resources>
                <ResourceDictionary>
                    <ResourceDictionary.MergedDictionaries>
                        <ResourceDictionary Source="/Themes/Colors.xaml" />
                    </ResourceDictionary.MergedDictionaries>
                    <DataTemplate x:Key="OrderLineTemplate" DataType="{x:Type vm:OrderLine}">
                        <TextBlock Text="{Binding Sku}" />
                    </DataTemplate>
                    <Style TargetType="Button" />
                </ResourceDictionary>
            </UserControl.Resources>
            <Grid>
                <Grid.RowDefinitions><RowDefinition /></Grid.RowDefinitions>
                <StackPanel>
                    <Button Content="Save" Command="{Binding SaveCommand}" />
                    <Button Content="Cancel" Click="OnCancel" />
                </StackPanel>
                <DataGrid x:Name="LinesGrid"
                          ItemsSource="{Binding Lines}" />
                <Border><TextBlock /></Border>
            </Grid>
        </UserControl>
        """;

    static XamlOutline Parse() => XamlOutliner.Parse(View, "src/Views/OrderEditView.xaml",
        new XamlContext("src/Views/OrderEditView.xaml.cs", new HashSet<string> { "OnCancel" }));

    [Fact]
    public void Reads_header_resources_and_merged_dictionaries()
    {
        var o = Parse();
        Assert.Equal(("UserControl", "Contoso.Orders.Views.OrderEditView", "OrderEditViewModel (design)", 28),
                     (o.RootElement, o.XClass, o.DataContext, o.LineCount));
        Assert.Equal(new[] { new XamlResource("OrderLineTemplate", "DataTemplate", "DataType=vm:OrderLine", 12, 14),
                             new XamlResource(null, "Style", "TargetType=Button", 15, 15) }, o.Resources);
        Assert.Equal(new[] { "/Themes/Colors.xaml" }, o.MergedDictionaries);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Keeps_only_named_bound_and_labelled_elements_in_the_tree() =>
        Assert.Equal(new[]
        {
            "0 Grid [18-27]", "1 StackPanel [20-23]",
            "2 Button \"Save\" Command={Binding SaveCommand} [21-21]", "2 Button \"Cancel\" Click=OnCancel [22-22]",
            "1 DataGrid x:Name=LinesGrid ItemsSource={Binding Lines} [24-25]",
        }, Flatten(Parse().Tree));

    static IEnumerable<string> Flatten(IEnumerable<XamlNode> nodes, int depth = 0) =>
        nodes.SelectMany(n => Flatten(n.Children, depth + 1).Prepend($"{depth} {n.Label} [{n.StartLine}-{n.EndLine}]"));

    [Fact]
    public void Emits_symbols_for_the_class_keys_and_names() =>
        Assert.Equal(new[]
        {
            (SymbolKind.Class, "OrderEditView", "Contoso.Orders.Views", 1, 28),
            (SymbolKind.XamlResource, "OrderLineTemplate", "Contoso.Orders.Views.OrderEditView", 12, 14),
            (SymbolKind.XamlElement, "LinesGrid", "Contoso.Orders.Views.OrderEditView", 24, 25),
        }, Parse().Symbols.Select(s => (s.Kind, s.Name, s.Container, s.StartLine, s.EndLine)));

    [Fact]
    public void Collapses_single_child_chains_and_caps_children()
    {
        var chain = XamlOutliner.Parse("<Grid xmlns=\"p\"><StackPanel><Button Content=\"Only\" /></StackPanel></Grid>", "a.xaml");
        Assert.Equal("StackPanel/Button \"Only\"", chain.Tree.Single().Label);
        var many = XamlOutliner.Parse("<StackPanel xmlns=\"p\">" +
            string.Concat(Enumerable.Range(0, 25).Select(i => $"<Button Content=\"B{i}\" />")) + "</StackPanel>", "b.xaml");
        Assert.Equal((20, 5), (many.Tree.Count, many.HiddenTopLevel));
    }

    /// <summary>25 kept top-level panels, each with 22 kept buttons: past the 20-child cap at both levels.</summary>
    public static string Wide(int panels = 25, int buttons = 22) => "<StackPanel xmlns=\"p\">\n" + string.Concat(Enumerable.Range(0, panels).Select(i =>
        $"  <WrapPanel x:Name=\"Panel{i:00}\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
        + string.Concat(Enumerable.Range(0, buttons).Select(j => $"    <Button Content=\"B{i:00}-{j:00}\" />\n"))
        + "  </WrapPanel>\n")) + "</StackPanel>\n";

    [Fact]
    public void Uncapped_mode_keeps_every_kept_child_and_the_default_still_caps_at_20()
    {
        var capped = XamlOutliner.Parse(Wide(), "w.xaml");
        Assert.Equal((20, 5, 20, 2), (capped.Tree.Count, capped.HiddenTopLevel, capped.Tree[0].Children.Count, capped.Tree[0].HiddenChildren));
        var all = XamlOutliner.Parse(Wide(), "w.xaml", uncappedTree: true);
        Assert.Equal((25, 0), (all.Tree.Count, all.HiddenTopLevel));
        Assert.All(all.Tree, panel => Assert.Equal((22, 0), (panel.Children.Count, panel.HiddenChildren)));
        Assert.Equal("Button \"B24-21\"", all.Tree[24].Children[21].Label);
        var chain = XamlOutliner.Parse("<Grid xmlns=\"p\"><StackPanel><Button Content=\"Only\" /></StackPanel></Grid>", "a.xaml", uncappedTree: true);
        Assert.Equal("StackPanel/Button \"Only\"", chain.Tree.Single().Label);
    }

    [Fact]
    public void Malformed_xaml_returns_what_was_read_plus_the_error()
    {
        var o = XamlOutliner.Parse("<Window x:Class=\"A.B\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n<Grid>\n</Window>", "w.xaml");
        Assert.Equal("A.B", o.XClass);
        Assert.StartsWith("XML error at line 3:", o.Error);
    }

    [Fact]
    public void Captures_application_startup_and_known_events_without_code_behind()
    {
        var app = XamlOutliner.Parse("<Application x:Class=\"Demo.App\" StartupUri=\"MainWindow.xaml\" Startup=\"OnStartup\" xmlns=\"p\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" />", "App.xaml");
        Assert.Equal(("MainWindow.xaml", "OnStartup"), (app.RootAttributes["StartupUri"], app.RootAttributes["Startup"]));
        var grid = XamlOutliner.Parse("<Grid xmlns=\"p\"><Border MouseDoubleClick=\"Grid_DoubleClick\" /></Grid>", "g.xaml");
        Assert.Equal("Border MouseDoubleClick=Grid_DoubleClick", grid.Tree.Single().Label);
    }

    [Fact]
    public void Long_text_and_values_are_truncated()
    {
        var o = XamlOutliner.Parse($"<Grid xmlns=\"p\"><Button Content=\"{new string('a', 50)}\" Tag=\"{{Binding {new string('b', 70)}}}\" /></Grid>", "t.xaml");
        Assert.Equal($"Button \"{new string('a', 39)}…\" Tag={{Binding {new string('b', 50)}…", o.Tree.Single().Label);
    }
}
