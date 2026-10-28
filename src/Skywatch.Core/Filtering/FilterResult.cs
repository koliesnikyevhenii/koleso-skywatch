namespace Skywatch.Core.Filtering;

public sealed record FilterResult(bool IsMatch, IReadOnlyList<PatternHit> Hits);

public sealed record PatternHit(string PatternName, string MatchedText);
