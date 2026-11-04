namespace WpfNav.Tests.TestSupport;

public static class TestText
{
    public static string Lf(string s) => s.ReplaceLineEndings("\n");

    /// <summary>The single text block of a tool result.</summary>
    public static string Text(this CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;
}
