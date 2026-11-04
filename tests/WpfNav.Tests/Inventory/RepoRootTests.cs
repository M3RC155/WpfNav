namespace WpfNav.Tests.Inventory;

public class RepoRootTests
{
    [Fact]
    public void Long_escaping_paths_and_kind_filters_are_echoed_bounded()
    {
        using var repo = TempRepo.Create();
        var path = "../" + new string('o', 3000) + ".cs";
        var ex = Assert.Throws<WpfNavException>(() => new RepoRoot(repo.Root).Resolve(path));
        Assert.StartsWith("Path is outside the repository root: ../ooo", ex.Message);
        Assert.True(ex.Message.Length <= 140, ex.Message.Length.ToString());
        var kind = Assert.Throws<WpfNavException>(() => SymbolKind.Class.MatchesFilter(new string('k', 3000)));
        Assert.StartsWith("Unknown kind 'kkk", kind.Message);
        Assert.True(kind.Message.Length <= 260, kind.Message.Length.ToString());
    }

    [Fact]
    public void Missing_root_is_rejected() =>
        Assert.Equal(@"Repository root not found: C:\nope\missing",
            Assert.Throws<WpfNavException>(() => new RepoRoot(@"C:\nope\missing")).Message);

    [Fact]
    public void Relative_absolute_and_differently_cased_paths_resolve_to_the_same_file()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        var root = new RepoRoot(repo.Root);
        var full = Path.Combine(repo.Root, "src", "A.cs");
        Assert.Equal(full, root.Resolve("src/A.cs"));
        Assert.Equal(full, root.Resolve(@"src\A.cs"));
        Assert.Equal(full, root.Resolve(full));
        Assert.Equal(full.ToUpperInvariant(), root.Resolve(full.ToUpperInvariant()));
        Assert.Equal("src/A.cs", root.ToRelative(full));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(@"..\..\Windows\win.ini")]
    public void Paths_escaping_the_root_are_rejected(string input)
    {
        using var repo = TempRepo.Create();
        Assert.Equal($"Path is outside the repository root: {input}",
            Assert.Throws<WpfNavException>(() => new RepoRoot(repo.Root).Resolve(input)).Message);
    }

    [Fact]
    public void Folder_paths_with_a_trailing_separator_are_normalized()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        var root = new RepoRoot(repo.Root);
        Assert.Equal("src", root.ToRelative(root.Resolve("src/")));
        Assert.Equal("src", root.ToRelative(root.Resolve(@"src\")));
    }

    [Fact]
    public void A_sibling_folder_sharing_the_prefix_is_outside()
    {
        using var repo = TempRepo.Create();
        Assert.Throws<WpfNavException>(() => new RepoRoot(repo.Root).Resolve(repo.Root + @"2\x.cs"));
    }
}
