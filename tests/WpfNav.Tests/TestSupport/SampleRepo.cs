using WpfNav.Tests.Navigation;
using WpfNav.Tests.Parsing;

namespace WpfNav.Tests.TestSupport;

/// <summary>A small WPF solution: Shell (app) → Orders (WPF library) → Core, plus a test project and a console tool.</summary>
public static class SampleRepo
{
    const string Sdk = "<Project Sdk=\"Microsoft.NET.Sdk\">";

    public static TempRepo Create() => TempRepo.Create(
        ("All.slnx", "<Solution><Project Path=\"src/Shell/Shell.csproj\" /><Project Path=\"src/Orders/Orders.csproj\" /><Project Path=\"src/Core/Core.csproj\" /><Project Path=\"tests/Core.Tests/Core.Tests.csproj\" /></Solution>"),
        ("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"CommunityToolkit.Mvvm\" Version=\"8.2.2\" /></ItemGroup></Project>"),
        ("src/Shell/Shell.csproj", Sdk + "<PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /><ProjectReference Include=\"..\\Orders\\Orders.csproj\" /><PackageReference Include=\"CommunityToolkit.Mvvm\" /></ItemGroup></Project>"),
        ("src/Shell/App.xaml", "<Application x:Class=\"Shell.App\" StartupUri=\"MainWindow.xaml\" xmlns=\"p\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" />"),
        ("src/Shell/App.xaml.cs", "namespace Shell;\npublic partial class App : Application\n{\n    protected override void OnStartup(StartupEventArgs e) { }\n}\n"),
        ("src/Shell/MainViewModel.cs", "namespace Shell;\npublic class MainViewModel : ViewModelBase { }\n"),
        ("src/Orders/Orders.csproj", Sdk + "<PropertyGroup><UseWPF>true</UseWPF><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>"),
        ("src/Orders/MainViewModel.cs", "namespace Orders;\npublic class MainViewModel : ViewModelBase { }\n"),
        ("src/Orders/ViewModels/OrderEditViewModel.cs", OutlineBuilderTests.Vm.Replace("Contoso.Orders", "Orders.ViewModels")),
        ("src/Orders/Views/OrderEditView.xaml", XamlOutlinerTests.View.Replace("Contoso.", "")),
        ("src/Orders/Views/OrderEditView.xaml.cs", "namespace Orders.Views;\npublic partial class OrderEditView : UserControl\n{\n    void OnCancel(object s, EventArgs e) { }\n}\n"),
        ("src/Orders/Themes/Generic.xaml", "<ResourceDictionary xmlns=\"p\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><SolidColorBrush x:Key=\"Accent\" Color=\"Red\" /></ResourceDictionary>"),
        ("src/Core/Core.csproj", Sdk + "<PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("src/Core/Services/OrderService.cs", "namespace Core.Services;\npublic class OrderService : IOrderService\n{\n    public void Create(Order order) { }\n}\n"),
        ("tests/Core.Tests/Core.Tests.csproj", Sdk + "<ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /><ProjectReference Include=\"..\\..\\src\\Core\\Core.csproj\" /></ItemGroup></Project>"),
        ("tests/Core.Tests/OrderServiceTests.cs", "public class OrderServiceTests { }\n"),
        ("tools/Gen/Gen.csproj", Sdk + "<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("tools/Gen/Program.cs", "class Program\n{\n    static void Main(string[] args) { }\n}\n"));
}
