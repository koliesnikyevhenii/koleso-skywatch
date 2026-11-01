using System.Text.RegularExpressions;
using Skywatch.Core.Normalization;

namespace Skywatch.Core.Filtering;

/// <summary>Evaluates the enabled patterns in configuration order (AC-F5.7–F5.11). Compiled once; a singleton.</summary>
public sealed class KeywordFilter
{
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly (string Name, Regex Regex, bool CaseSensitive)[] _patterns;
    private readonly Action<string>? _onTimeout;

    /// <param name="onTimeout">Called with the pattern name when a match times out (AC-F5.10); Core does no logging itself.</param>
    public KeywordFilter(IEnumerable<PatternDefinition> patterns, Action<string>? onTimeout = null)
    {
        // AC-F5.8: disabled patterns are not even compiled.
        _patterns = [.. patterns.Where(p => p.Enabled).Select(p => (p.Name, Compile(p), p.CaseSensitive))];
        _onTimeout = onTimeout;
    }

    /// <summary>The compiled regex for a pattern, with the options and timeout of AC-F5.9.</summary>
    public static Regex Compile(PatternDefinition pattern) => new(
        pattern.Regex,
        RegexOptions.CultureInvariant | (pattern.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
        _matchTimeout);

    public FilterResult Match(NormalizedText text)
    {
        if (text.Lower.Length == 0)
        {
            return new FilterResult(false, []); // AC-F5.11
        }

        List<PatternHit> hits = [];
        foreach (var (name, regex, caseSensitive) in _patterns)
        {
            try
            {
                var match = regex.Match(caseSensitive ? text.CasePreserved : text.Lower);
                if (match.Success)
                {
                    hits.Add(new PatternHit(name, match.Value));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                _onTimeout?.Invoke(name); // AC-F5.10: no match for this pattern, carry on with the rest
            }
        }

        return new FilterResult(hits.Count > 0, hits);
    }

    /// <summary>
    /// AC-F8.14: every place an enabled pattern matches the original text, sorted and with overlaps merged. The
    /// original is searched, not the normalized text, because normalization changes the length; a mention split by
    /// an invisible character is then simply not highlighted.
    /// </summary>
    public IReadOnlyList<TextSpan> Highlights(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        List<TextSpan> spans = [];
        foreach (var (_, regex, _) in _patterns)
        {
            try
            {
                for (var match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    if (match.Length > 0)
                    {
                        spans.Add(new TextSpan(match.Index, match.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Highlighting is cosmetic: the match itself already reported the timeout (AC-F5.10).
            }
        }

        return Merge(spans);
    }

    private static List<TextSpan> Merge(List<TextSpan> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<TextSpan> merged = [];
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { Length = Math.Max(last.End, span.End) - last.Start };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }
}
