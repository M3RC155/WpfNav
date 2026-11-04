namespace WpfNav.Tests.Output;

public class OutputWriterTests
{
    [Fact]
    public void Starts_with_the_root_line() =>
        Assert.Equal("root: C:\\repo\n", new OutputWriter(100, @"C:\repo").ToString());

    [Theory, InlineData("1234567", 2), InlineData("12345678", 3), InlineData("", 0)]
    public void Estimates_tokens_as_chars_over_3_5(string text, int tokens) =>
        Assert.Equal(tokens, OutputWriter.EstimateTokens(text));

    [Fact]
    public void A_line_fits_exactly_at_the_boundary_including_its_newline()
    {
        var w = new OutputWriter(100);
        Assert.Equal(350, w.Capacity);
        Assert.False(w.TryAppend(new string('x', 350)));
        Assert.True(w.Truncated);
        Assert.True(w.TryAppend(new string('x', 349)));
        Assert.Equal(100, OutputWriter.EstimateTokens(w.ToString()));
        Assert.False(w.TryAppend(""));
    }

    [Fact]
    public void The_root_line_counts_against_the_limit()
    {
        var w = new OutputWriter(100, @"C:\repo");
        Assert.Equal(336, w.Room());
        Assert.False(w.TryAppend(new string('x', 336)));
        Assert.True(w.TryAppend(new string('x', 335)));
        Assert.Equal(350, w.Length);
    }

    [Fact]
    public void A_root_that_does_not_fit_is_an_error_not_a_partial_root()
    {
        var ex = Assert.Throws<WpfNavException>(() => new OutputWriter(Budget.Minimum, @"C:\" + new string('r', 2000)));
        Assert.StartsWith("max_tokens=256 leaves no room for the repository root line", ex.Message);
        Assert.True(OutputWriter.EstimateTokens(ex.Message) <= Budget.Minimum);
    }

    [Fact]
    public void A_reserved_footer_always_fits_after_the_lines_it_guarded()
    {
        var w = new OutputWriter(100);
        var written = 0;
        while (w.TryAppend(new string('x', 34), reserve: OutputWriter.MoreNote(4, written + 1))) written++;
        w.AppendReserved(OutputWriter.MoreNote(4, written));
        Assert.Equal(8, written);
        Assert.True(OutputWriter.EstimateTokens(w.ToString()) <= 100);
        Assert.EndsWith("… 4 more — narrow with project=/path= or pass offset=8\n", w.ToString());
    }

    [Fact]
    public void A_reserve_can_hold_several_notes()
    {
        var w = new OutputWriter(100);
        const string notes = "note one\nnote two";
        Assert.True(w.TryAppend(new string('x', 350 - (notes.Length + 1) - 1), notes));
        Assert.False(w.TryAppend("", notes));
        w.AppendReserved(notes);
        Assert.Equal(350, w.Length);
        Assert.EndsWith("note one\nnote two\n", w.ToString());
    }

    [Fact]
    public void An_unreserved_footer_that_does_not_fit_is_refused_loudly()
    {
        var w = new OutputWriter(100);
        Assert.True(w.TryAppend(new string('x', 349)));
        Assert.Throws<InvalidOperationException>(() => w.AppendReserved("late note"));
        Assert.Equal(350, w.Length);
    }

    [Fact]
    public void A_block_is_written_whole_or_not_at_all()
    {
        var w = new OutputWriter(100);
        Assert.True(w.TryAppend(new string('x', 200)));
        Assert.False(w.TryAppendBlock([new string('a', 100), new string('b', 100)]));
        Assert.Equal(201, w.Length);
        Assert.True(w.Truncated);
        Assert.True(w.TryAppendBlock(["a", "b"], reserve: "note"));
        Assert.EndsWith("x\na\nb\n", w.ToString());
    }

    [Fact]
    public void A_block_stops_reading_lines_once_it_is_over_the_limit()
    {
        var w = new OutputWriter(100);
        var read = 0;
        IEnumerable<string> Endless()
        {
            while (true)
            {
                read++;
                yield return new string('x', 50);
            }
        }
        Assert.False(w.TryAppendBlock(Endless()));
        Assert.Equal(7, read);
        Assert.Equal(0, w.Length);
    }

    [Fact]
    public void The_smallest_budget_with_a_long_root_and_footer_stays_within_it()
    {
        var footer = "… " + new string('f', 150);
        var w = new OutputWriter(Budget.Minimum, @"C:\" + new string('r', 600));
        var i = 0;
        while (w.TryAppend($"entry {i}", footer)) i++;
        w.AppendReserved(footer);
        Assert.True(i > 0);
        Assert.True(OutputWriter.EstimateTokens(w.ToString()) <= Budget.Minimum);
        Assert.EndsWith(footer + "\n", w.ToString());
    }

    [Fact]
    public void Required_metadata_that_does_not_fit_is_an_error()
    {
        var w = new OutputWriter(100);
        w.AppendRequired("status", reserve: "note", what: "the status line");
        var ex = Assert.Throws<WpfNavException>(() => w.AppendRequired(new string('s', 400), null, "the status line"));
        Assert.Equal("max_tokens=100 leaves no room for the status line; pass a larger max_tokens.", ex.Message);
    }

    [Fact]
    public void The_first_entry_of_a_page_must_fit_below_the_ceiling_and_is_abbreviated_at_it()
    {
        var below = new OutputWriter(100);
        Assert.Throws<WpfNavException>(() => below.AppendProgress(new string('e', 400), "footer", ceiling: 200, what: "the next entry"));
        var at = new OutputWriter(100);
        at.AppendProgress(new string('e', 400), "footer", ceiling: 100, what: "the next entry");
        at.AppendReserved("footer");
        Assert.Equal(350, at.Length);
        Assert.Matches(@"^e+… \(abbreviated from 400 chars\)\nfooter\n$", at.ToString());
    }

    [Fact]
    public void More_note_without_narrowing() =>
        Assert.Equal("… 4 more — pass offset=6", OutputWriter.MoreNote(4, 6, canNarrow: false));

    [Theory, InlineData(null, 3000), InlineData(256, 256), InlineData(1000, 1000), InlineData(3000, 3000)]
    public void Budgets_default_to_the_ceiling_and_accept_256_up_to_it(int? requested, int resolved) =>
        Assert.Equal(resolved, Budget.Resolve(requested, Limits.Outline));

    [Theory, InlineData(255), InlineData(0), InlineData(-1), InlineData(3001)]
    public void Budgets_outside_the_range_are_rejected_not_clamped(int requested)
    {
        var ex = Assert.Throws<WpfNavException>(() => Budget.Resolve(requested, Limits.Outline));
        Assert.Equal($"max_tokens must be between 256 and 3000; got {requested}. Omit it for the default (3000).", ex.Message);
        Assert.Equal(Limits.Outline, Budget.Bound(requested, Limits.Outline));
    }

    [Fact]
    public void Fit_abbreviates_the_middle_and_keeps_the_reason_and_the_remedy()
    {
        var text = "Unknown project '" + new string('p', 5000) + "'. Closest: Core";
        var fit = Budget.Fit(text, Budget.Minimum);
        Assert.True(OutputWriter.EstimateTokens(fit) <= Budget.Minimum);
        Assert.StartsWith("Unknown project 'ppp", fit);
        Assert.EndsWith("ppp'. Closest: Core", fit);
        Assert.Contains("ppp … ppp", fit);
        Assert.Equal("short", Budget.Fit("short", Budget.Minimum));
    }

    [Fact]
    public void Echo_cuts_long_values()
    {
        Assert.Equal("abc", Echo.Cut("abc"));
        var cut = Echo.Cut(new string('a', 500));
        Assert.Equal(100, cut.Length);
        Assert.EndsWith("…", cut);
    }
}
