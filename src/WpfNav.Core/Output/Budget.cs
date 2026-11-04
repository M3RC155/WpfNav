using System.Globalization;

namespace WpfNav.Core.Output;

/// <summary>Per-tool default and maximum output budgets, in estimated tokens.</summary>
public static class Limits
{
    public const int Overview = 2500, Outline = 3000, FindSymbol = 2000, ReadSymbol = 4000, SearchCode = 2500;
}

/// <summary>Caller-selected budgets: validation, defaults, and keeping diagnostic text within them.</summary>
public static class Budget
{
    /// <summary>The smallest max_tokens any tool accepts.</summary>
    public const int Minimum = 256;

    /// <summary>The budget for a request: the ceiling when omitted, the value when it is in 256..ceiling, an error otherwise.</summary>
    public static int Resolve(int? maxTokens, int ceiling)
    {
        if (maxTokens is null) return ceiling;
        if (maxTokens < Minimum || maxTokens > ceiling)
            throw new WpfNavException(string.Create(CultureInfo.InvariantCulture,
                $"max_tokens must be between {Minimum} and {ceiling}; got {maxTokens}. Omit it for the default ({ceiling})."));
        return maxTokens.Value;
    }

    /// <summary>The budget that bounds a request's result or error: the resolved budget, or the ceiling when the request is invalid.</summary>
    public static int Bound(int? maxTokens, int ceiling) =>
        maxTokens is { } v && v >= Minimum && v <= ceiling ? v : ceiling;

    /// <summary>The error for output whose required metadata or first entry cannot fit the budget.</summary>
    public static WpfNavException TooSmall(int budget, string what) =>
        new(string.Create(CultureInfo.InvariantCulture, $"max_tokens={budget} leaves no room for {what}; pass a larger max_tokens."));

    /// <summary>The text unchanged when it fits the budget, else its start and end joined by " … ", so reason and remedy survive.</summary>
    public static string Fit(string text, int budget)
    {
        var capacity = OutputWriter.CapacityOf(budget);
        if (text.Length <= capacity) return text;
        const string Gap = " … ";
        var keep = Math.Max(0, capacity - Gap.Length);
        var head = keep * 2 / 3;
        var tail = keep - head;
        if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
        if (tail > 0 && char.IsLowSurrogate(text[^tail])) tail--;
        return text[..head] + Gap + text[^tail..];
    }
}

/// <summary>Bounds user input echoed back in messages.</summary>
public static class Echo
{
    public const int MaxChars = 100;

    public static string Cut(string value, int max = MaxChars)
    {
        if (value.Length <= max) return value;
        var keep = max - 1;
        if (char.IsHighSurrogate(value[keep - 1])) keep--;
        return value[..keep] + "…";
    }
}
