using System.Text.RegularExpressions;
using WpfNav.Core.Model;

namespace WpfNav.Core.Index;

public enum MatchTier { ExactCase, ExactIgnoreCase, Prefix, CamelHumps, Substring }

/// <param name="Path">A repo-relative folder or file prefix (already resolved by the caller).</param>
/// <param name="DerivesFrom">A base type name; matches types deriving from it directly or indirectly, by name.</param>
public sealed record SymbolQuery(string Text, string? Kind = null, string? Project = null, string? Path = null, string? DerivesFrom = null);

/// <param name="OtherParts">Further parts of a partial type, in path order.</param>
public sealed record SymbolMatch(SymbolRecord Symbol, MatchTier Tier, IReadOnlyList<SymbolRecord> OtherParts);

public static class SymbolMatcher
{
    public static MatchTier? MatchName(string name, string pattern) => NamePattern.Create(pattern).Tier(name);

    /// <summary>Ranked matches: tier, then types before members, non-test before test, shorter qualified names first.</summary>
    public static IReadOnlyList<SymbolMatch> Match(IReadOnlyList<SymbolRecord> symbols, SymbolQuery query)
    {
        if (query.Kind is { } kindFilter) SymbolKind.Class.MatchesFilter(kindFilter); // validates the filter even when nothing is indexed
        var text = query.Text.Trim();
        var dot = text.LastIndexOf('.');
        var (suffix, pattern) = dot < 0 ? (null, text) : (text[..dot], text[(dot + 1)..]);
        var names = NamePattern.Create(pattern);
        var derived = query.DerivesFrom is { } baseName ? DerivedTypes(symbols, baseName) : null;

        var hits = new List<(SymbolRecord Symbol, MatchTier Tier)>();
        foreach (var s in symbols)
        {
            if (s.Kind == SymbolKind.Namespace) continue;
            if (query.Kind is { } k && !s.Kind.MatchesFilter(k)) continue;
            if (query.Project is { } p && !string.Equals(s.Project, p, StringComparison.OrdinalIgnoreCase)) continue;
            if (query.Path is { Length: > 0 } prefix && !UnderPath(s.Path, prefix)) continue;
            if (derived is not null && !(s.Kind.IsType() && derived.Contains(s.QualifiedName))) continue;
            if (!ContainerMatches(s.Container, suffix)) continue;
            if (names.Tier(s.Name) is { } tier) hits.Add((s, tier));
        }

        var matches = new List<SymbolMatch>();
        // Parts of one partial type share project and qualified name; the same name in two projects is two types.
        foreach (var group in hits.GroupBy(h => h.Symbol.Kind.IsType() ? $"T:{h.Symbol.Project}|{h.Symbol.QualifiedName}" : null))
        {
            var parts = group.ToList();
            if (group.Key is not null && parts.Count > 1 && parts.Any(h => h.Symbol.IsPartial))
            {
                var ordered = parts.OrderBy(h => h.Symbol.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(h => h.Symbol.Path, StringComparer.OrdinalIgnoreCase).ToList();
                var others = ordered.Skip(1).Select(h => h.Symbol).OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
                matches.Add(new SymbolMatch(ordered[0].Symbol, parts.Min(h => h.Tier), others));
            }
            else
            {
                matches.AddRange(parts.Select(h => new SymbolMatch(h.Symbol, h.Tier, [])));
            }
        }

        return matches
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Symbol.Kind.IsType() ? 0 : 1)
            .ThenBy(m => m.Symbol.InTestProject ? 1 : 0)
            .ThenBy(m => m.Symbol.QualifiedName.Length)
            .ThenBy(m => m.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(m => m.Symbol.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Symbol.StartLine)
            .ToList();
    }

    /// <summary>True when the container equals the suffix or ends with "." + suffix (ignoring case); a null suffix matches all.</summary>
    public static bool ContainerMatches(string container, string? suffix) =>
        suffix is null
        || container.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || container.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Suggestions for a name that matched nothing: names containing the text, or within a small edit distance.</summary>
    public static IReadOnlyList<SymbolRecord> Closest(IEnumerable<SymbolRecord> symbols, string text, int count) =>
        Near(symbols.Where(s => s.Kind != SymbolKind.Namespace), s => s.Name, text)
            .DistinctBy(s => (s.QualifiedName, s.Kind))
            .Take(count)
            .ToList();

    /// <summary>The same rule as <see cref="Closest"/>, applied to plain names such as project names.</summary>
    public static IReadOnlyList<string> ClosestNames(IEnumerable<string> names, string text, int count) =>
        Near(names, n => n, text).Distinct(StringComparer.OrdinalIgnoreCase).Take(count).ToList();

    static bool UnderPath(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Qualified names of every type whose base list reaches the given name, following bases by simple name.</summary>
    static HashSet<string> DerivedTypes(IReadOnlyList<SymbolRecord> symbols, string baseName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName };
        var derived = new HashSet<string>(StringComparer.Ordinal);
        var types = symbols.Where(s => s.Kind.IsType() && s.Bases.Count > 0).ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var t in types)
            {
                if (derived.Contains(t.QualifiedName) || !t.Bases.Any(names.Contains)) continue;
                derived.Add(t.QualifiedName);
                names.Add(t.Name);
                changed = true;
            }
        }
        return derived;
    }

    static IEnumerable<T> Near<T>(IEnumerable<T> items, Func<T, string> nameOf, string text)
    {
        var target = text.ToLowerInvariant();
        var max = Math.Max(2, text.Length / 3);
        return items
            .Select(item => (Item: item, Name: nameOf(item)))
            .Select(x => (x.Item, x.Name, Contains: x.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Contains || Math.Abs(x.Name.Length - text.Length) <= max)
            .Select(x => (x.Item, x.Name, x.Contains, Distance: Levenshtein(x.Name.ToLowerInvariant(), target)))
            .Where(x => x.Contains || x.Distance <= max)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Item);
    }

    static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>A name pattern prepared once per query.</summary>
    sealed class NamePattern
    {
        readonly string _pattern;
        readonly Regex? _wildcard;
        readonly List<string> _segments;

        NamePattern(string pattern)
        {
            _pattern = pattern;
            if (pattern.IndexOfAny(['*', '?']) >= 0)
                _wildcard = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _segments = pattern.Any(char.IsLower) ? SplitBeforeUppercase(pattern) : pattern.Select(c => c.ToString()).ToList();
        }

        public static NamePattern Create(string pattern) => new(pattern);

        public MatchTier? Tier(string name)
        {
            if (name.Equals(_pattern, StringComparison.Ordinal)) return MatchTier.ExactCase;
            if (name.Equals(_pattern, StringComparison.OrdinalIgnoreCase)) return MatchTier.ExactIgnoreCase;
            if (_wildcard is not null) return _wildcard.IsMatch(name) ? MatchTier.Prefix : null;
            if (name.StartsWith(_pattern, StringComparison.OrdinalIgnoreCase)) return MatchTier.Prefix;
            if (CamelHumps(name)) return MatchTier.CamelHumps;
            if (name.Contains(_pattern, StringComparison.OrdinalIgnoreCase)) return MatchTier.Substring;
            return null;
        }

        // Each segment must start a later hump than the previous one; humps may be skipped.
        bool CamelHumps(string name)
        {
            if (_segments.Count < 2) return false;
            var from = 0;
            foreach (var segment in _segments)
            {
                var found = -1;
                for (var i = from; i < name.Length; i++)
                {
                    if (IsHumpStart(name, i) && name.AsSpan(i).StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                    {
                        found = i;
                        break;
                    }
                }
                if (found < 0) return false;
                from = found + 1;
            }
            return true;
        }

        // Hump starts: index 0; an uppercase letter after a non-uppercase one; an uppercase letter followed by a
        // lowercase one (XMLParser → X, Parser); a letter after '_'.
        static bool IsHumpStart(string s, int i) =>
            i == 0
            || (char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
            || (char.IsUpper(s[i]) && i + 1 < s.Length && char.IsLower(s[i + 1]))
            || (char.IsLetter(s[i]) && s[i - 1] == '_');

        static List<string> SplitBeforeUppercase(string pattern)
        {
            var segments = new List<string>();
            var start = 0;
            for (var i = 1; i <= pattern.Length; i++)
            {
                if (i < pattern.Length && !char.IsUpper(pattern[i])) continue;
                segments.Add(pattern[start..i]);
                start = i;
            }
            return segments;
        }
    }
}
