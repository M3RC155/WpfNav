using System.Xml;
using System.Xml.Linq;
using WpfNav.Core.Inventory;

namespace WpfNav.Core.Projects;

/// <summary>Reads a .csproj as plain XML (SDK-style or legacy). MSBuild conditions and imports are ignored.</summary>
public static class ProjectFileReader
{
    const string WpfProjectTypeGuid = "{60dc8134-eba5-43b8-bcc9-bb4bc16c2548}";

    static readonly HashSet<string> TestPackages = new(
        ["xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "Microsoft.NET.Test.Sdk"], StringComparer.OrdinalIgnoreCase);

    public static ProjectInfo Read(RepoRoot root, string projectPath)
    {
        var full = root.Resolve(projectPath);
        var relative = root.ToRelative(full);
        var name = Path.GetFileNameWithoutExtension(full);
        var folder = Path.GetDirectoryName(full)!;
        var directory = folder.Equals(root.FullPath, StringComparison.OrdinalIgnoreCase) ? "" : root.ToRelative(folder);
        try
        {
            var project = XDocument.Load(full);
            string? Prop(XDocument doc, string property) =>
                doc.Descendants().LastOrDefault(e => e.Name.LocalName == property && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();

            var tfm = Prop(project, "TargetFramework");
            var tfms = Prop(project, "TargetFrameworks");
            var legacyTfm = Prop(project, "TargetFrameworkVersion");
            var useWpf = Prop(project, "UseWPF");
            var outputType = Prop(project, "OutputType");
            var approximate = false;

            if (Nearest(root, folder, "Directory.Build.props") is { } inherited)
            {
                if (tfm is null && tfms is null && legacyTfm is null)
                {
                    tfm = Prop(inherited, "TargetFramework");
                    tfms = Prop(inherited, "TargetFrameworks");
                    approximate |= tfm is not null || tfms is not null;
                }
                if (useWpf is null && Prop(inherited, "UseWPF") is { } inheritedWpf) (useWpf, approximate) = (inheritedWpf, true);
                if (outputType is null && Prop(inherited, "OutputType") is { } inheritedOutput) (outputType, approximate) = (inheritedOutput, true);
            }

            var frameworks = tfms is not null
                ? tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : tfm is not null ? [tfm]
                : legacyTfm is not null ? ["net" + legacyTfm.TrimStart('v', 'V').Replace(".", "")]
                : [];

            var items = project.Descendants().Where(e => e.Parent?.Name.LocalName == "ItemGroup").ToList();
            var wpf = string.Equals(useWpf, "true", StringComparison.OrdinalIgnoreCase)
                || (Prop(project, "ProjectTypeGuids")?.Contains(WpfProjectTypeGuid, StringComparison.OrdinalIgnoreCase) ?? false)
                || items.Any(e => e.Name.LocalName == "Reference" && Include(e)?.Split(',')[0].Trim() == "PresentationFramework");

            var references = items.Where(e => e.Name.LocalName == "ProjectReference" && Include(e) is not null)
                .Select(e => Relative(root, folder, Include(e)!))
                .ToList();

            var packages = Packages(root, folder, items);
            var exe = outputType is not null && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase));
            var test = string.Equals(Prop(project, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                || packages.Any(p => TestPackages.Contains(p.Name));
            var kind = test ? ProjectKind.Test
                : exe && wpf ? ProjectKind.WpfApp
                : wpf ? ProjectKind.WpfLibrary
                : exe ? ProjectKind.App
                : ProjectKind.Library;

            return new ProjectInfo(name, relative, directory, kind, frameworks, approximate, references, packages, null);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return new ProjectInfo(name, relative, directory, ProjectKind.Library, [], false, [], [], $"not parsed: {e.Message}");
        }
    }

    static List<PackageRef> Packages(RepoRoot root, string folder, List<XElement> items)
    {
        Dictionary<string, string>? central = null;
        var packages = new List<PackageRef>();
        foreach (var e in items.Where(e => e.Name.LocalName == "PackageReference" && Include(e) is not null))
        {
            var version = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value;
            if (version is null)
            {
                central ??= CentralVersions(root, folder);
                central.TryGetValue(Include(e)!, out version);
            }
            packages.Add(new PackageRef(Include(e)!, version));
        }

        var packagesConfig = Path.Combine(folder, "packages.config");
        if (File.Exists(packagesConfig))
        {
            var doc = XDocument.Load(packagesConfig);
            packages.AddRange(doc.Descendants().Where(e => e.Name.LocalName == "package" && e.Attribute("id") is not null)
                .Select(e => new PackageRef(e.Attribute("id")!.Value, e.Attribute("version")?.Value)));
        }
        return packages;
    }

    static Dictionary<string, string> CentralVersions(RepoRoot root, string folder)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Nearest(root, folder, "Directory.Packages.props") is not { } props) return versions;
        foreach (var e in props.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
            if (Include(e) is { } id && e.Attribute("Version")?.Value is { } version)
                versions[id] = version;
        return versions;
    }

    /// <summary>The nearest file of that name in the project folder or any folder above it, up to the root.</summary>
    static XDocument? Nearest(RepoRoot root, string folder, string fileName)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                try { return XDocument.Load(candidate); }
                catch (XmlException) { return null; }
            }
            if (dir.FullName.Equals(root.FullPath, StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    static string? Include(XElement e) => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value;

    static string Relative(RepoRoot root, string folder, string include)
    {
        var full = Path.GetFullPath(Path.Combine(folder, include.Replace('\\', Path.DirectorySeparatorChar)));
        try { return root.ToRelative(full); }
        catch (WpfNavException) { return include.Replace('\\', '/'); }
    }
}
