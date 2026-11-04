using System.Globalization;
using System.Text;

namespace WpfNav.Core.Output;

/// <summary>
/// Builds tool output line by line within an estimated-token limit. Every write is checked, including the root line;
/// a renderer keeps room for the footer it may need by passing that exact footer as the reserve of each line it adds.
/// </summary>
public sealed class OutputWriter
{
    public const double CharsPerToken = 3.5;

    readonly StringBuilder _text = new();

    /// <exception cref="WpfNavException">The root line alone does not fit the limit.</exception>
    public OutputWriter(int tokenLimit, string? root = null)
    {
        Limit = tokenLimit;
        Capacity = CapacityOf(tokenLimit);
        if (root is not null && !TryAppend("root: " + root))
            throw Budget.TooSmall(tokenLimit,
                string.Create(CultureInfo.InvariantCulture, $"the repository root line ({root.Length + 6:N0} chars)"));
    }

    public int Limit { get; }

    /// <summary>The most characters whose estimate stays within the limit.</summary>
    public int Capacity { get; }

    public int Length => _text.Length;

    /// <summary>True once a line or block was refused because it did not fit.</summary>
    public bool Truncated { get; private set; }

    public static int EstimateTokens(string text) => EstimateTokens(text.Length);

    public static int EstimateTokens(int chars) => (int)Math.Ceiling(chars / CharsPerToken);

    public static int CapacityOf(int tokenLimit) => (int)Math.Floor(Math.Max(0, tokenLimit) * CharsPerToken);

    /// <summary>Characters still free, newlines included, after keeping room for <paramref name="reserve"/> and its newline.</summary>
    public int Room(string? reserve = null) => Capacity - _text.Length - Cost(reserve);

    public bool Fits(string line, string? reserve = null) => line.Length + 1 <= Room(reserve);

    /// <summary>Appends the line (never part of it) if it and the reserve both fit.</summary>
    public bool TryAppend(string line, string? reserve = null)
    {
        if (!Fits(line, reserve)) return Refuse();
        _text.Append(line).Append('\n');
        return true;
    }

    /// <summary>Appends every line or none. Stops reading the lines as soon as they cannot fit.</summary>
    public bool TryAppendBlock(IEnumerable<string> lines, string? reserve = null)
    {
        var room = Room(reserve);
        var buffered = new List<string>();
        var used = 0;
        foreach (var line in lines)
        {
            used += line.Length + 1;
            if (used > room) return Refuse();
            buffered.Add(line);
        }
        foreach (var line in buffered) _text.Append(line).Append('\n');
        return true;
    }

    /// <summary>Appends metadata the result cannot do without (status, identity, context).</summary>
    /// <exception cref="WpfNavException">It does not fit with the reserve; names <paramref name="what"/>.</exception>
    public void AppendRequired(string line, string? reserve, string what)
    {
        if (!TryAppend(line, reserve)) throw Budget.TooSmall(Limit, what);
    }

    /// <summary>
    /// Appends the first entry of a page, which must make progress. Below the tool's ceiling an entry that cannot fit is an
    /// error asking for a larger budget; at the ceiling it is abbreviated and marked, rather than dropped.
    /// </summary>
    public void AppendProgress(string line, string? reserve, int ceiling, string what)
    {
        if (TryAppend(line, reserve)) return;
        if (Limit < ceiling) throw Budget.TooSmall(Limit, what);
        var marker = string.Create(CultureInfo.InvariantCulture, $"… (abbreviated from {line.Length:N0} chars)");
        var keep = Room(reserve) - 1 - marker.Length;
        if (keep < 1) throw Budget.TooSmall(Limit, what);
        if (char.IsHighSurrogate(line[keep - 1])) keep--;
        _text.Append(line, 0, keep).Append(marker).Append('\n');
    }

    /// <summary>Appends a footer that earlier writes reserved room for.</summary>
    /// <exception cref="InvalidOperationException">It does not fit, so a renderer skipped its reservation.</exception>
    public void AppendReserved(string line)
    {
        if (!Fits(line))
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Unreserved footer of {line.Length + 1} chars does not fit {Room()} remaining of {Capacity}."));
        _text.Append(line).Append('\n');
    }

    public static string MoreNote(int remaining, int nextOffset, bool canNarrow = true) => canNarrow
        ? $"… {remaining} more — narrow with project=/path= or pass offset={nextOffset}"
        : $"… {remaining} more — pass offset={nextOffset}";

    public override string ToString() => _text.ToString();

    static int Cost(string? reserve) => reserve is null ? 0 : reserve.Length + 1;

    bool Refuse()
    {
        Truncated = true;
        return false;
    }
}
