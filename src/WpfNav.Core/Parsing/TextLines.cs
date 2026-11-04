namespace WpfNav.Core.Parsing;

public static class TextLines
{
    /// <summary>The line count an editor shows: a trailing line break does not start another line.</summary>
    public static int Count(string text)
    {
        if (text.Length == 0) return 0;
        var breaks = 0;
        foreach (var c in text)
            if (c == '\n') breaks++;
        return text[^1] == '\n' ? breaks : breaks + 1;
    }
}
