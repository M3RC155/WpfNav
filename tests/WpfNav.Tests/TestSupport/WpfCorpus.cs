namespace WpfNav.Tests.TestSupport;

/// <summary>
/// A generated WPF codebase at the scale of a large sample repository (about 2,650 indexed files and 150K lines),
/// written to a temp folder so the corpus smoke test needs nothing outside this repo. It has an MVVM gallery app on
/// CommunityToolkit.Mvvm and Microsoft.Extensions.Hosting, 60 sample apps and libraries, a hand-written converter of
/// about 2,700 lines and a generated dataset designer file of about 3,000 lines.
/// </summary>
public static class WpfCorpus
{
    public const string GalleryProject = "Gallery";
    public const string ConverterFile = "Converters/HtmlToXamlConverter.cs";
    public const string ConverterType = "HtmlToXamlConverter";
    public const string DesignerFile = "Data/NorthwindDataSet.Designer.cs";

    const int Samples = 60, SamplePages = 9, SampleModels = 10, GalleryPages = 50, ConverterMethods = 140;
    const string Sdk = "<Project Sdk=\"Microsoft.NET.Sdk\">";
    const string XamlNs = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    static readonly string[] Tables = ["Customers", "Orders", "OrderDetails", "Products", "Categories", "Suppliers", "Employees", "Shippers", "Regions", "Territories"];
    static readonly string[] Columns = ["Id", "Name", "Code", "Description", "City", "Country", "Phone", "CreatedOn", "ModifiedOn", "IsActive"];

    public static TempRepo Create()
    {
        var files = new List<(string Path, string Content)>();
        var projects = new List<string> { "Gallery/Gallery.csproj", "Converters/Converters.csproj", "Data/Data.csproj", "Shared/Shared.csproj" };
        files.Add(("Directory.Build.props", "<Project><PropertyGroup><Nullable>enable</Nullable><LangVersion>latest</LangVersion></PropertyGroup></Project>"));
        AddGallery(files);
        AddConverters(files);
        AddData(files);
        AddShared(files);
        for (var i = 0; i < Samples; i++) projects.Add(AddSample(files, i));
        files.Add(("Samples.slnx", "<Solution>" + string.Concat(projects.Select(p => $"<Project Path=\"{p}\" />")) + "</Solution>"));
        return TempRepo.Create(files.ToArray());
    }

    static void AddGallery(List<(string, string)> files)
    {
        files.Add(("Gallery/Gallery.csproj", Sdk + """
            <PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup>
            <ItemGroup>
              <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
              <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
              <PackageReference Include="Microsoft.Xaml.Behaviors.Wpf" Version="1.1.122" />
              <ProjectReference Include="..\Shared\Shared.csproj" />
              <ProjectReference Include="..\Converters\Converters.csproj" />
            </ItemGroup>
            </Project>
            """));
        files.Add(("Gallery/App.xaml", $"""
            <Application x:Class="Gallery.App" {XamlNs}>
              <Application.Resources>
                <ResourceDictionary Source="Themes/Theme.xaml" />
              </Application.Resources>
            </Application>
            """));
        files.Add(("Gallery/App.xaml.cs", """
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            namespace Gallery;

            public partial class App : Application
            {
                readonly IHost _host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<MainWindow>();
                        services.AddSingleton<Services.INavigationService, Services.NavigationService>();
                    })
                    .Build();

                protected override async void OnStartup(StartupEventArgs e)
                {
                    await _host.StartAsync();
                    _host.Services.GetRequiredService<MainWindow>().Show();
                    base.OnStartup(e);
                }

                protected override async void OnExit(ExitEventArgs e)
                {
                    await _host.StopAsync();
                    _host.Dispose();
                    base.OnExit(e);
                }
            }
            """));
        files.Add(("Gallery/MainWindow.xaml", Window("Gallery.MainWindow")));
        files.Add(("Gallery/MainWindow.xaml.cs", CodeBehind("Gallery", "MainWindow", "Window", "Gallery.ViewModels.MainWindowViewModel")));
        files.Add(("Gallery/ViewModels/MainWindowViewModel.cs", ToolkitViewModel("Gallery.ViewModels", "MainWindowViewModel")));
        files.Add(("Gallery/Themes/Theme.xaml", Theme("Gallery", 40)));
        for (var j = 0; j < GalleryPages; j++)
        {
            files.Add(($"Gallery/Views/Control{j:00}Page.xaml", Page($"Gallery.Views.Control{j:00}Page", j)));
            files.Add(($"Gallery/Views/Control{j:00}Page.xaml.cs", CodeBehind("Gallery.Views", $"Control{j:00}Page", "Page", $"Gallery.ViewModels.Control{j:00}PageViewModel")));
            files.Add(($"Gallery/ViewModels/Control{j:00}PageViewModel.cs", ToolkitViewModel("Gallery.ViewModels", $"Control{j:00}PageViewModel")));
        }
        files.Add(("Gallery/Services/INavigationService.cs", """
            namespace Gallery.Services;

            public interface INavigationService
            {
                void Navigate(Type pageType);
                bool CanGoBack { get; }
                void GoBack();
            }
            """));
        for (var j = 0; j < 9; j++) files.Add(($"Gallery/Services/Service{j}.cs", Model("Gallery.Services", $"Service{j}", 6)));
        files.Add(("Gallery/Services/NavigationService.cs", """
            namespace Gallery.Services;

            public sealed class NavigationService : INavigationService
            {
                readonly Stack<Type> _history = new();

                public bool CanGoBack => _history.Count > 1;

                public void Navigate(Type pageType) => _history.Push(pageType);

                public void GoBack()
                {
                    if (CanGoBack) _history.Pop();
                }
            }
            """));
    }

    static void AddConverters(List<(string, string)> files)
    {
        files.Add(("Converters/Converters.csproj", Sdk + "<PropertyGroup><UseWPF>true</UseWPF><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup></Project>"));
        var sb = new StringBuilder("""
            using System.Text;
            using System.Xml;

            namespace Converters;

            /// <summary>Converts HTML fragments to FlowDocument XAML, one element kind at a time.</summary>
            public static class HtmlToXamlConverter
            {
                public const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

            """);
        for (var m = 0; m < ConverterMethods; m++)
            sb.Append($$"""

                    /// <summary>Converts element kind {{m}} and its children.</summary>
                    internal static void AddElement{{m:000}}(XmlElement xamlParent, XmlNode htmlNode, Dictionary<string, string> inherited)
                    {
                        if (htmlNode is not XmlElement htmlElement) return;
                        var xaml = xamlParent.OwnerDocument.CreateElement(null, "Section", XamlNamespace);
                        foreach (XmlAttribute attribute in htmlElement.Attributes)
                        {
                            switch (attribute.Name.ToLowerInvariant())
                            {
                                case "style": inherited["style{{m}}"] = attribute.Value; break;
                                case "class": inherited["class{{m}}"] = attribute.Value; break;
                                default: xaml.SetAttribute(attribute.Name, attribute.Value); break;
                            }
                        }
                        for (var child = htmlNode.FirstChild; child is not null; child = child.NextSibling)
                            AddElement{{(m + 1) % ConverterMethods:000}}(xaml, child, inherited);
                        xamlParent.AppendChild(xaml);
                    }

                """);
        sb.Append("}\n");
        files.Add((ConverterFile, sb.ToString()));
        for (var j = 0; j < 5; j++) files.Add(($"Converters/Css/CssStylesheet{j}.cs", Model("Converters.Css", $"CssStylesheet{j}", 8)));
    }

    static void AddData(List<(string, string)> files)
    {
        files.Add(("Data/Data.csproj", Sdk + "<PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms></PropertyGroup></Project>"));
        const string Attr = "[global::System.Diagnostics.DebuggerNonUserCodeAttribute()]\n        [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"System.Data.Design.TypedDataSetGenerator\", \"17.0.0.0\")]";
        var sb = new StringBuilder("""
            //------------------------------------------------------------------------------
            // <auto-generated>
            //     This code was generated by a tool.
            // </auto-generated>
            //------------------------------------------------------------------------------

            namespace Data;

            [global::System.Serializable()]
            [global::System.ComponentModel.DesignerCategoryAttribute("code")]
            public partial class NorthwindDataSet : global::System.Data.DataSet
            {

            """);
        foreach (var table in Tables)
        {
            sb.Append($$"""
                    private {{table}}DataTable table{{table}};

                    {{Attr}}
                    public {{table}}DataTable {{table}} => this.table{{table}};

                    public partial class {{table}}DataTable : global::System.Data.TypedTableBase<{{table}}Row>
                    {

                """);
            foreach (var column in Columns)
                sb.Append($$"""
                            private global::System.Data.DataColumn column{{column}};

                            {{Attr}}
                            public global::System.Data.DataColumn {{column}}Column
                            {
                                get { return this.column{{column}}; }
                            }

                    """);
            foreach (var verb in new[] { "Add", "Remove", "FindBy", "New", "Clone" })
                sb.Append($$"""
                            {{Attr}}
                            public {{table}}Row {{verb}}{{table}}Row({{table}}Row row)
                            {
                                this.Rows.Add(row);
                                return row;
                            }

                    """);
            sb.Append($$"""
                    }

                    public partial class {{table}}Row : global::System.Data.DataRow
                    {
                        private {{table}}DataTable table{{table}};

                """);
            foreach (var column in Columns)
                sb.Append($$"""
                            {{Attr}}
                            public string {{column}}
                            {
                                get
                                {
                                    try { return ((string)(this[this.table{{table}}.{{column}}Column])); }
                                    catch (global::System.InvalidCastException e) { throw new global::System.Data.StrongTypingException("The value for column '{{column}}' is DBNull.", e); }
                                }
                                set { this[this.table{{table}}.{{column}}Column] = value; }
                            }

                            {{Attr}}
                            public bool Is{{column}}Null() => this.IsNull(this.table{{table}}.{{column}}Column);

                    """);
            sb.Append($$"""
                    }

                    public class {{table}}RowChangeEvent : global::System.EventArgs
                    {
                        private readonly {{table}}Row eventRow;
                        private readonly global::System.Data.DataRowAction eventAction;

                        {{Attr}}
                        public {{table}}RowChangeEvent({{table}}Row row, global::System.Data.DataRowAction action)
                        {
                            this.eventRow = row;
                            this.eventAction = action;
                        }

                        public {{table}}Row Row => this.eventRow;

                        public global::System.Data.DataRowAction Action => this.eventAction;
                    }

                """);
        }
        sb.Append("}\n");
        files.Add((DesignerFile, sb.ToString()));
    }

    static void AddShared(List<(string, string)> files)
    {
        files.Add(("Shared/Shared.csproj", Sdk + "<PropertyGroup><TargetFrameworks>net48;net8.0-windows</TargetFrameworks><UseWPF>true</UseWPF></PropertyGroup></Project>"));
        files.Add(("Shared/ViewModelBase.cs", """
            using System.ComponentModel;
            using System.Runtime.CompilerServices;

            namespace Shared;

            public abstract class ViewModelBase : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;

                protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
                {
                    if (EqualityComparer<T>.Default.Equals(field, value)) return false;
                    field = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                    return true;
                }
            }
            """));
        files.Add(("Shared/RelayCommand.cs", """
            using System.Windows.Input;

            namespace Shared;

            public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
            {
                public event EventHandler? CanExecuteChanged
                {
                    add => CommandManager.RequerySuggested += value;
                    remove => CommandManager.RequerySuggested -= value;
                }

                public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

                public void Execute(object? parameter) => execute();
            }
            """));
        for (var j = 0; j < 8; j++) files.Add(($"Shared/Helpers/Helper{j}.cs", Model("Shared.Helpers", $"Helper{j}", 6)));
    }

    static string AddSample(List<(string, string)> files, int i)
    {
        var name = $"Sample{i:000}";
        var isApp = i % 4 != 3;
        var tfm = (i % 3) switch { 0 => "net8.0-windows", 1 => "net10.0-windows", _ => "net48" };
        var project = $"Samples/{name}/{name}.csproj";
        files.Add((project, Sdk + $"""
            <PropertyGroup>{(isApp ? "<OutputType>WinExe</OutputType>" : "")}<UseWPF>true</UseWPF><TargetFramework>{tfm}</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="..\..\Shared\Shared.csproj" /></ItemGroup>
            </Project>
            """));
        if (isApp)
        {
            files.Add(($"Samples/{name}/App.xaml", $"<Application x:Class=\"{name}.App\" StartupUri=\"MainWindow.xaml\" {XamlNs} />"));
            files.Add(($"Samples/{name}/App.xaml.cs", $"namespace {name};\n\npublic partial class App : Application\n{{\n}}\n"));
            files.Add(($"Samples/{name}/MainWindow.xaml", Window($"{name}.MainWindow")));
            files.Add(($"Samples/{name}/MainWindow.xaml.cs", CodeBehind(name, "MainWindow", "Window", $"{name}.ViewModels.MainViewModel")));
        }
        files.Add(($"Samples/{name}/Themes/Generic.xaml", Theme(name, 12)));
        for (var j = 0; j < SamplePages; j++)
        {
            files.Add(($"Samples/{name}/Views/Page{j}.xaml", Page($"{name}.Views.Page{j}", j)));
            files.Add(($"Samples/{name}/Views/Page{j}.xaml.cs", CodeBehind($"{name}.Views", $"Page{j}", "Page", $"{name}.ViewModels.Page{j}ViewModel")));
            files.Add(($"Samples/{name}/ViewModels/Page{j}ViewModel.cs", ViewModel($"{name}.ViewModels", $"Page{j}ViewModel")));
        }
        for (var j = 0; j < SampleModels; j++) files.Add(($"Samples/{name}/Models/Item{j}.cs", Model($"{name}.Models", $"Item{j}", 40)));
        return project;
    }

    static string Window(string type) => $$"""
        <Window x:Class="{{type}}" {{XamlNs}} Title="{Binding Title}" Width="900" Height="600">
          <DockPanel>
            <Menu DockPanel.Dock="Top">
              <MenuItem Header="_File"><MenuItem Header="E_xit" Command="{Binding ExitCommand}" /></MenuItem>
            </Menu>
            <Frame x:Name="ContentFrame" NavigationUIVisibility="Hidden" />
          </DockPanel>
        </Window>
        """;

    static string Page(string type, int j) => $$"""
        <Page x:Class="{{type}}" {{XamlNs}} Loaded="OnLoaded">
          <Page.Resources>
            <Style x:Key="HeaderText{{j}}" TargetType="TextBlock">
              <Setter Property="FontSize" Value="20" />
              <Setter Property="Margin" Value="0,0,0,12" />
            </Style>
          </Page.Resources>
          <Grid x:Name="Root">
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto" />
              <RowDefinition Height="*" />
              <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <TextBlock Text="{Binding Title}" Style="{StaticResource HeaderText{{j}}}" />
            <ListBox x:Name="ItemsList" Grid.Row="1" ItemsSource="{Binding Items}" SelectedItem="{Binding SelectedItem}" />
            <StackPanel Grid.Row="2" Orientation="Horizontal">
              <TextBox x:Name="NameBox" Width="200" Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}" />
              <Button Content="Save" Command="{Binding SaveCommand}" />
              <Button Content="Refresh" Click="OnRefresh" />
            </StackPanel>
          </Grid>
        </Page>
        """;

    static string Theme(string prefix, int brushes)
    {
        var sb = new StringBuilder($"<ResourceDictionary {XamlNs}>\n");
        for (var b = 0; b < brushes; b++) sb.Append($"  <SolidColorBrush x:Key=\"{prefix}Brush{b:00}\" Color=\"#FF{b * 5 % 256:X2}{b * 11 % 256:X2}{b * 17 % 256:X2}\" />\n");
        sb.Append($"  <Style x:Key=\"{prefix}ButtonStyle\" TargetType=\"Button\">\n    <Setter Property=\"Padding\" Value=\"8,4\" />\n  </Style>\n");
        return sb.Append("</ResourceDictionary>\n").ToString();
    }

    static string CodeBehind(string ns, string type, string baseType, string viewModel) => $$"""
        using System.Windows.Threading;

        namespace {{ns}};

        /// <summary>Interaction logic for {{type}}.xaml.</summary>
        public partial class {{type}} : {{baseType}}
        {
            readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

            public {{type}}()
            {
                InitializeComponent();
                DataContext = new {{viewModel}}();
                _timer.Tick += (_, _) => Refresh();
            }

            void OnLoaded(object sender, RoutedEventArgs e)
            {
                _timer.Start();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
            }

            void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

            void Refresh()
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(Refresh);
                    return;
                }
                UpdateLayout();
            }
        }
        """;

    static string ViewModel(string ns, string type)
    {
        var sb = new StringBuilder($$"""
            using System.Collections.ObjectModel;
            using Shared;

            namespace {{ns}};

            public class {{type}} : ViewModelBase
            {
                public {{type}}() => SaveCommand = new RelayCommand(Save, () => !string.IsNullOrEmpty(Name));

                public ObservableCollection<string> Items { get; } = [];

                public RelayCommand SaveCommand { get; }

            """);
        foreach (var property in new[] { "Title", "Name", "SelectedItem", "Status", "Filter", "Description", "Category", "Owner", "Notes", "Error" })
            sb.Append($$"""

                    string? _{{char.ToLowerInvariant(property[0]) + property[1..]}};

                    public string? {{property}}
                    {
                        get => _{{char.ToLowerInvariant(property[0]) + property[1..]}};
                        set => Set(ref _{{char.ToLowerInvariant(property[0]) + property[1..]}}, value);
                    }

                """);
        sb.Append("""

                void Save()
                {
                    if (Name is null) return;
                    Items.Add(Name);
                    Status = $"Saved {Items.Count} items";
                }
            }

            """);
        return sb.ToString();
    }

    static string ToolkitViewModel(string ns, string type) => $$"""
        using System.Collections.ObjectModel;
        using CommunityToolkit.Mvvm.ComponentModel;
        using CommunityToolkit.Mvvm.Input;

        namespace {{ns}};

        public partial class {{type}} : ObservableObject
        {
            [ObservableProperty]
            private string? _title = "{{type}}";

            [ObservableProperty]
            private string? _name;

            [ObservableProperty]
            private string? _selectedItem;

            public ObservableCollection<string> Items { get; } = [];

            [RelayCommand]
            private void Save()
            {
                if (Name is null) return;
                Items.Add(Name);
            }

            [RelayCommand]
            private async Task RefreshAsync()
            {
                await Task.Delay(10);
                Items.Clear();
            }
        }
        """;

    static string Model(string ns, string type, int properties)
    {
        var sb = new StringBuilder($$"""
            namespace {{ns}};

            /// <summary>A plain data type with {{properties}} properties.</summary>
            public sealed class {{type}}
            {

            """);
        for (var p = 0; p < properties; p++)
            sb.Append($$"""
                    /// <summary>Value {{p}}.</summary>
                    public string? Value{{p}} { get; set; }

                """);
        sb.Append($$"""
                public override string ToString() => $"{{type}}({Value0})";

                public bool IsEmpty()
                {
                    return string.IsNullOrEmpty(Value0);
                }
            }

            """);
        return sb.ToString();
    }
}
